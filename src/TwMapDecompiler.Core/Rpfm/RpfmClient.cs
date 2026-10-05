using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwMapDecompiler.Core.Rpfm;

/// <summary>
/// Minimal client for the RPFM 5 server (rpfm_server.exe): JSON commands over a
/// WebSocket at ws://127.0.0.1:45127/ws, correlated by message id. Starts the
/// server from the RPFM install folder when none is running; the server exits
/// by itself once its last session disconnects.
/// </summary>
public sealed class RpfmClient : IDisposable
{
    public const int DefaultPort = 45127;

    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancel = new();
    private readonly TaskCompletionSource<long> _sessionConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;

    public long SessionId { get; private set; }

    private RpfmClient()
    {
        // Pack listings and decoded tables arrive as single large text messages.
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    /// <summary>Connects to a running server, or starts one from <paramref name="rpfmFolder"/> first.</summary>
    public static async Task<RpfmClient> ConnectAsync(string? rpfmFolder, Action<string> log, int port = DefaultPort, CancellationToken ct = default)
    {
        if (!await IsListeningAsync(port))
        {
            string server = FindServerExecutable(rpfmFolder);
            log($"Starting RPFM server: {server}");
            Process.Start(new ProcessStartInfo
            {
                FileName = server,
                WorkingDirectory = Path.GetDirectoryName(server)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!await IsListeningAsync(port))
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"RPFM server did not start listening on port {port} within 60s.");
                await Task.Delay(250, ct);
            }
        }

        var client = new RpfmClient();
        await client._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), ct);
        _ = Task.Run(client.ReceiveLoop);
        client.SessionId = await client._sessionConnected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        log($"Connected to RPFM server (session {client.SessionId})");
        return client;
    }

    private static string FindServerExecutable(string? rpfmFolder)
    {
        if (!string.IsNullOrWhiteSpace(rpfmFolder))
        {
            string candidate = File.Exists(rpfmFolder) ? rpfmFolder : Path.Combine(rpfmFolder, "rpfm_server.exe");
            if (File.Exists(candidate))
                return candidate;
            throw new FileNotFoundException($"rpfm_server.exe not found in {rpfmFolder} (RPFM 5.0 or newer is required).");
        }
        throw new InvalidOperationException("No RPFM server is running and no RPFM folder was configured to start one from " +
                                            "(set it in the GUI, pass --rpfm, or set the RPFM_FOLDER environment variable).");
    }

    private static async Task<bool> IsListeningAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1 << 20];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _receiveCancel.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("RPFM server closed the connection.");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var root = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length))!;
                int id = root["id"]!.GetValue<int>();
                var data = root["data"];

                if (id == 0)
                {
                    if (data is JsonObject o && o["SessionConnected"] is { } session)
                        _sessionConnected.TrySetResult(session.GetValue<long>());
                    continue;
                }

                if (_pending.TryRemove(id, out var tcs))
                {
                    if (data is JsonObject obj && obj.Count == 1 && obj["Error"] is { } error)
                        tcs.TrySetException(new RpfmException(error.ToString()));
                    else
                        tcs.TrySetResult(data);
                }
            }
        }
        catch (Exception ex)
        {
            _sessionConnected.TrySetException(ex);
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(ex);
        }
    }

    /// <summary>Sends a command and returns the response's <c>data</c> payload.</summary>
    /// <param name="command">A string for unit commands, or a JsonObject {"Name": params}.</param>
    public async Task<JsonNode?> SendAsync(JsonNode command, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var envelope = new JsonObject { ["id"] = id, ["data"] = command };
        byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    /// <summary>Command with a single (newtype) or tuple parameter: {"Name": value}.</summary>
    public Task<JsonNode?> SendAsync(string name, JsonNode? parameter, CancellationToken ct = default) =>
        SendAsync(new JsonObject { [name] = parameter }, ct);

    /// <summary>Unwraps the single-variant response object {"Variant": payload}.</summary>
    public static JsonNode? Unwrap(JsonNode? response, string variant) =>
        response is JsonObject obj && obj.TryGetPropertyValue(variant, out var value)
            ? value
            : throw new RpfmException($"Unexpected RPFM response (wanted {variant}): {Truncate(response?.ToJsonString())}");

    private static string Truncate(string? s) => s == null ? "null" : s.Length > 300 ? s[..300] + "..." : s;

    public void Dispose()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                // Lets the server drop the session now instead of after its 5-minute grace period.
                var envelope = new JsonObject { ["id"] = Interlocked.Increment(ref _nextId), ["data"] = "ClientDisconnecting" };
                _ws.SendAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Best effort; the server times the session out on its own.
        }
        _receiveCancel.Cancel();
        _ws.Dispose();
        _sendLock.Dispose();
    }
}

public sealed class RpfmException(string message) : Exception(message);
