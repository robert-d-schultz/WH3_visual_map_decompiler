using System.Text;
using TwMapDecompiler.Core.Imaging;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// LogicMapHeights: the land heights from full_logic_map.compressed_map, against a logic map
/// quantised the way the heightmap compile does it.
/// </summary>
public sealed class LogicMapHeightsTests
{
    private const int W = 21, H = 18, Block = 16;

    /// <summary>The compile's quantisation, in float32 like the compiler.</summary>
    private static ushort Quantise(float h, float lo, float hi) => (ushort)(int)((h - lo) * (1f / (hi - lo)) * 65535f);

    /// <summary>Source heights, row 0 north; a smooth slope plus ripples, spanning lo..hi exactly.</summary>
    private static float[] Source(out float lo, out float hi)
    {
        var heights = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                heights[y * W + x] = -17.25f + 1.7f * x + 0.9f * y + 2.3f * MathF.Sin(x * 0.7f) * MathF.Cos(y * 0.4f);
        lo = heights.Min();
        hi = heights.Max();
        return heights;
    }

    /// <summary>A TABLE_INDEXED map of uncompressed 16x16 blocks, rows south first as the compile writes them.</summary>
    private static string WriteLogicMap(float[] heights, float lo, float hi)
    {
        int bw = (W + Block - 1) / Block, bh = (H + Block - 1) / Block;
        var blocks = new MemoryStream();
        var offsets = new List<uint>();
        var bwr = new BinaryWriter(blocks);
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                offsets.Add((uint)blocks.Position);
                bwr.Write((byte)143);
                for (int j = 0; j < Block; j++)
                    for (int i = 0; i < Block; i++)
                    {
                        int x = Math.Min(bx * Block + i, W - 1), row = Math.Min(by * Block + j, H - 1);
                        bwr.Write(Quantise(heights[(H - 1 - row) * W + x], lo, hi));
                    }
            }

        string path = Path.Combine(Path.GetTempPath(), $"logic_{Guid.NewGuid():N}.compressed_map");
        using var w = new BinaryWriter(File.Create(path));
        w.Write(new byte[8]);
        w.Write((ushort)3);
        foreach (int v in new[] { W, H, Block, Block })
            w.Write((uint)v);
        foreach (float v in new[] { 0, lo, 0, 0, hi, 0 })
            w.Write(v);
        w.Write((ushort)"TABLE_INDEXED".Length);
        w.Write(Encoding.ASCII.GetBytes("TABLE_INDEXED"));
        w.Write((uint)offsets.Count);
        offsets.ForEach(w.Write);
        w.Write(0u);
        w.Write((uint)blocks.Length);
        w.Write(blocks.ToArray());
        return path;
    }

    [Fact]
    public void DecodesWithinHalfAStepRowZeroNorth()
    {
        float[] source = Source(out float lo, out float hi);
        string path = WriteLogicMap(source, lo, hi);
        try
        {
            float[] decoded = LogicMapHeights.Read(path, out int width, out int height);
            Assert.Equal((W, H), (width, height));
            double halfStep = (hi - lo) / 65535.0 / 2;
            for (int i = 0; i < source.Length; i++)
                Assert.True(Math.Abs(decoded[i] - source[i]) <= halfStep * 1.01, $"texel {i}: {decoded[i]} against {source[i]}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecompilesToTheSameLogicMap()
    {
        float[] source = Source(out float lo, out float hi);
        string path = WriteLogicMap(source, lo, hi);
        try
        {
            float[] decoded = LogicMapHeights.Read(path, out _, out _);
            Assert.Equal(lo, decoded.Min());
            Assert.Equal(hi, decoded.Max());
            for (int i = 0; i < source.Length; i++)
                Assert.Equal(Quantise(source[i], lo, hi), Quantise(decoded[i], lo, hi));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A refined decode of a smooth hill: closer normals than the midpoints, the same logic map.</summary>
    [Fact]
    public void RefiningAgainstTheCompilesNormalsHelpsAndKeepsTheLogicMap()
    {
        const int n = 96;
        var truth = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                truth[y * n + x] = 0.02f * x + 0.01f * y + 0.15f * MathF.Sin(x * 0.11f) * MathF.Cos(y * 0.07f);
        float lo = truth.Min(), hi = truth.Max();
        var values = truth.Select(t => Quantise(t, lo, hi)).ToArray();
        var map = new LogicMapHeights.LogicMap(n, n, values, lo, hi);

        // lf_normal as the compile writes it (Sobel, spacing 4, 8-bit), X in alpha and Y in green.
        var rgba = new byte[n * n * 4];
        for (int y = 1; y < n - 1; y++)
            for (int x = 1; x < n - 1; x++)
            {
                var (nx, ny, _) = Normal(truth, x, y, n);
                int o = (y * n + x) * 4;
                rgba[o + 1] = (byte)(int)((ny + 1) * 127.5f);
                rgba[o + 3] = (byte)(int)((nx + 1) * 127.5f);
                rgba[o] = rgba[o + 2] = 128;
            }

        float[] mid = LogicMapHeights.Decode(map);
        float[] refined = LogicMapHeights.Refine(map, rgba);
        Assert.True(NormalError(refined, truth, n) < NormalError(mid, truth, n));
        for (int i = 0; i < values.Length; i++)
            Assert.Equal(values[i], Quantise(refined[i], lo, hi));
    }

    private static (float X, float Y, float Z) Normal(float[] a, int x, int y, int n)
    {
        int i = y * n + x;
        float sx = a[i - n - 1] + 2 * a[i - 1] + a[i + n - 1] - a[i - n + 1] - 2 * a[i + 1] - a[i + n + 1];
        float sy = a[i - n - 1] + 2 * a[i - n] + a[i - n + 1] - a[i + n - 1] - 2 * a[i + n] - a[i + n + 1];
        float inv = 1f / MathF.Sqrt(sx * sx + sy * sy + 1f / 16);
        return (sx * inv, sy * inv, 0.25f * inv);
    }

    private static double NormalError(float[] a, float[] truth, int n)
    {
        double sum = 0;
        for (int y = 3; y < n - 3; y++)
            for (int x = 3; x < n - 3; x++)
            {
                var (ax, ay, az) = Normal(a, x, y, n);
                var (tx, ty, tz) = Normal(truth, x, y, n);
                double angle = Math.Acos(Math.Clamp(ax * tx + ay * ty + az * tz, -1, 1));
                sum += angle * angle;
            }
        return sum;
    }
}
