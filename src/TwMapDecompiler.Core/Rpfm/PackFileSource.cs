using System.Text.Json.Nodes;

namespace TwMapDecompiler.Core.Rpfm;

/// <summary>
/// The game's vanilla .pack files, read through the RPFM server. Files are
/// extracted to a local folder keeping their in-pack paths, so the rest of the
/// pipeline keeps working on plain files.
/// </summary>
public sealed class PackFileSource : IDisposable
{
    private const string CaPacksKey = "CA PackFiles";

    private readonly RpfmClient _client;
    private readonly string _packKey;

    private PackFileSource(RpfmClient client, string packKey)
    {
        _client = client;
        _packKey = packKey;
    }

    /// <param name="game">RPFM game key, e.g. "warhammer_3".</param>
    public static async Task<PackFileSource> OpenAsync(string? rpfmFolder, string game, Action<string> log, CancellationToken ct = default)
    {
        var client = await RpfmClient.ConnectAsync(rpfmFolder, log, ct: ct);
        try
        {
            // Selecting the game also loads its schema, which table decoding needs.
            await client.SendAsync("SetGameSelected", new JsonArray(game, false), ct);

            var caKey = RpfmClient.Unwrap(await client.SendAsync(JsonValue.Create("LoadAllCAPackFiles")!, ct), "StringContainerInfo");
            log($"Loaded vanilla {game} packs");
            return new PackFileSource(client, caKey?[0]?.GetValue<string>() ?? CaPacksKey);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The Assembly Kit root RPFM has configured for the selected game (the folder containing raw_data).</summary>
    public async Task<string?> GetAssemblyKitRootAsync(CancellationToken ct = default)
    {
        try
        {
            // RPFM reports the AK's db folder: <ak>/raw_data/db.
            string? path = RpfmClient.Unwrap(await _client.SendAsync(JsonValue.Create("AssemblyKitPath")!, ct), "PathBuf")?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir != null && (dir.Name.Equals("db", StringComparison.OrdinalIgnoreCase) ||
                                   dir.Name.Equals("raw_data", StringComparison.OrdinalIgnoreCase)))
                dir = dir.Parent;
            return dir?.FullName;
        }
        catch (RpfmException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts files and folders (in-pack paths, '/'-separated) under <paramref name="destination"/>,
    /// keeping their in-pack paths. Missing folders are silently skipped; a missing file
    /// fails the request with an <see cref="RpfmException"/>.
    /// </summary>
    public async Task ExtractAsync(IEnumerable<(string Path, bool IsFolder)> paths, string destination, CancellationToken ct = default)
    {
        var containerPaths = new JsonArray();
        foreach (var (path, isFolder) in paths)
            containerPaths.Add(new JsonObject { [isFolder ? "Folder" : "File"] = path.Trim('/') });

        var request = new JsonArray(_packKey, new JsonObject { ["PackFile"] = containerPaths }, destination, false);
        await _client.SendAsync("ExtractPackedFiles", request, ct);
    }

    /// <summary>
    /// Extracts one file under <paramref name="destination"/> and returns its local path, or null
    /// when no pack has it. The path's casing need not match the pack's, and unlike a folder a
    /// missing file fails the whole extraction, so each spelling is asked for on its own.
    /// </summary>
    public async Task<string?> ExtractFileAsync(string packPath, string destination, CancellationToken ct = default)
    {
        packPath = packPath.Replace('\\', '/').Trim('/');
        foreach (string candidate in new[] { packPath, packPath.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await ExtractAsync([(candidate, false)], destination, ct);
            }
            catch (RpfmException)
            {
                continue;
            }
            string local = Path.Combine(destination, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local))
                return local;
        }
        return null;
    }

    /// <summary>Reads every row of a DB table, as column name -> value.</summary>
    public async Task<List<Dictionary<string, JsonNode?>>> ReadTableAsync(string tableName, string scratchFolder, CancellationToken ct = default)
    {
        var rows = new List<Dictionary<string, JsonNode?>>();

        // No listing command is cheap on the merged vanilla packs, so find the table
        // files by extracting the table folder and reading back what arrived.
        string listing = Path.Combine(scratchFolder, $"table_listing_{tableName}");
        var request = new JsonArray(_packKey, new JsonObject { ["PackFile"] = new JsonArray(new JsonObject { ["Folder"] = $"db/{tableName}" }) },
            listing, false);
        await _client.SendAsync("ExtractPackedFiles", request, ct);

        string tableFolder = Path.Combine(listing, "db", tableName);
        if (!Directory.Exists(tableFolder))
            return rows;

        foreach (string file in Directory.EnumerateFiles(tableFolder).Order(StringComparer.Ordinal))
        {
            string packPath = $"db/{tableName}/{Path.GetFileName(file)}";
            var decoded = RpfmClient.Unwrap(await _client.SendAsync("DecodePackedFile", new JsonArray(_packKey, packPath, "PackFile"), ct),
                "DBRFileInfo");
            var table = decoded![0]!["table"]!;

            // table_data is in processed column order, which can differ from the definition's
            // binary field order; RPFM computes the processed order for us.
            var fields = RpfmClient.Unwrap(await _client.SendAsync("FieldsProcessed", table["definition"]!.DeepClone(), ct), "VecField")!.AsArray();
            var names = fields.Select(f => f!["name"]!.GetValue<string>()).ToList();

            foreach (var rowNode in table["table_data"]!.AsArray())
            {
                var cells = rowNode!.AsArray();
                var row = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                for (int c = 0; c < cells.Count && c < names.Count; c++)
                    row[names[c]] = CellValue(cells[c]);
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>Cells are tagged {"F32": 961.3}; returns the untagged value.</summary>
    private static JsonNode? CellValue(JsonNode? cell) =>
        cell is JsonObject obj && obj.Count == 1 ? obj.First().Value?.DeepClone() : cell?.DeepClone();

    public void Dispose() => _client.Dispose();
}
