using TwMapDecompiler.Core.Formats.CompressedMap;

namespace TwMapDecompiler.Core.Imaging;

/// <summary>
/// The land height map, read from full_logic_map.compressed_map rather than the BC6H
/// full_height_map.dds. The heightmap compile writes both from the same composited Height
/// map, and the logic map is the faithful one: every texel at full resolution, quantised
/// linearly over the map's own range,
///
///     v = trunc((h - lo) * (1 / (hi - lo)) * 65535)        (float32; lo, hi in header floats 1 and 4)
///
/// one step being (hi - lo) / 65535 - 0.0007 on IEE, 0.0008 on vanilla combi, 0.0015 on The
/// Old World - where BC6H snaps each 4x4 block onto a few interpolated half-float levels. Its
/// rows run south to north, the other way round from the decoded DDS layers.
///
/// <see cref="Decode"/> puts each texel in the middle of its step, so the heights recompile to
/// the same logic map; the lowest and highest steps decode to lo and hi themselves, which
/// keeps the range. On IEE and Old World that is within half a step of the Terry source
/// everywhere, and the recompiled logic map is byte-identical.
///
/// <see cref="Refine"/> (optional) moves each texel within its step to fit lf_normal.dds; see there.
/// </summary>
public static class LogicMapHeights
{
    /// <param name="Values">The quantised heights, row 0 at the top (north).</param>
    public sealed record LogicMap(int Width, int Height, ushort[] Values, float Lo, float Hi)
    {
        public double Step => ((double)Hi - Lo) / 65535;
    }

    public static LogicMap Load(string path)
    {
        var map = CompressedMapReader.ReadTableIndexed(path);
        int w = map.Width, h = map.Height;
        var values = new ushort[(long)w * h];
        Parallel.For(0, h, y => Array.Copy(map.Pixels, (long)(h - 1 - y) * w, values, (long)y * w, w));
        return new LogicMap(w, h, values, map.Values[1], map.Values[4]);
    }

    /// <summary>Heights, row 0 at the top (north) like the decoded DDS layers.</summary>
    public static float[] Read(string path, out int width, out int height)
    {
        var map = Load(path);
        (width, height) = (map.Width, map.Height);
        return Decode(map);
    }

    /// <summary>Each texel in the middle of its step; the lowest and highest at lo and hi.</summary>
    public static float[] Decode(LogicMap map)
    {
        var heights = new float[map.Values.LongLength];
        Parallel.For(0, map.Height, y =>
        {
            long row = (long)y * map.Width;
            for (int x = 0; x < map.Width; x++)
                heights[row + x] = Middle(map, map.Values[row + x]);
        });
        return heights;
    }

    private static float Middle(LogicMap map, ushort v) => v switch
    {
        0 => map.Lo,
        ushort.MaxValue => map.Hi,
        _ => (float)(map.Lo + (v + 0.5) * map.Step),
    };

    /// <summary>
    /// Weights of <see cref="Refine"/>'s terms (see there), tuned against The Old World's Terry
    /// source on two 3072x3072 crops (central, 89% land; lowland, 35%): normal RMS 0.341 -> 0.266
    /// and 0.236 -> 0.187 degrees. Fitting the normal map alone does worse than the midpoints:
    /// its own noise is a few times the logic map's.
    /// </summary>
    public sealed record RefineOptions
    {
        public float Smooth { get; init; } = 1f;
        /// <summary>Penalty on a move of a whole step, in squared Sobel units.</summary>
        public double AnchorPerStep { get; init; } = 4 * 0.0057 * 0.0057;
        public int Iterations { get; init; } = 60;
        /// <summary>Below this correlation between the normal map's slopes and the logic map's, nothing is refined.</summary>
        public double MinCorrelation { get; init; } = 0.5;
    }

    private const float StepMargin = 0.05f;

    /// <summary>
    /// Heights that also fit lf_normal.dds, each texel kept inside its logic-map step, so the
    /// heights still recompile to the same logic map (a texel the compile's float32 rounding
    /// would give another value goes back to its midpoint). lf_normal.dds is the compile's 3x3
    /// Sobel of the same heights, 8-bit and DXT5nm (X in alpha, Y in green); its slope scale
    /// (Terry's spacing, 4 on IEE and Old World) is fitted from the two maps. Minimises, by
    /// projected gradient descent from the midpoints,
    ///
    ///     ||nz^2 (sobel(h) - g)||^2  +  ||laplacian(h)||^2  +  0.00013 ||(h - mid) / step||^2
    ///
    /// nz^2 turns a slope residual into an angle one: a steep normal's slope is ill-conditioned.
    /// Only worth it where lf_normal.dds was made from the same heights: IEE's keeps vanilla's
    /// normals (of CA's heights) in the vanilla areas. The two-texel border stays at the
    /// midpoints, as do texels at lo or hi. Returns the midpoints, with a note, when the normal
    /// map does not follow the heights.
    /// </summary>
    /// <param name="normalRgba">lf_normal.dds decoded to RGBA8, the same size, row 0 at the top.</param>
    public static float[] Refine(LogicMap map, byte[] normalRgba, Action<string>? log = null, Action<double>? progress = null,
        RefineOptions? options = null)
    {
        var o = options ?? new RefineOptions();
        int w = map.Width, h = map.Height;
        var c = Decode(map);
        if (w < 5 || h < 5)
            return c;

        // Slope per normal: g = k * (nx, ny) / nz, k = 1 / spacing, fitted with the nz^4 weights.
        var slope = new float[256 * 256 * 2];
        var weight = new float[256 * 256];
        for (int ny = 0; ny < 256; ny++)
            for (int nx = 0; nx < 256; nx++)
            {
                double x = (nx + 0.5) / 127.5 - 1, y = (ny + 0.5) / 127.5 - 1, z = Math.Sqrt(Math.Max(1e-4, 1 - x * x - y * y));
                slope[(ny * 256 + nx) * 2] = (float)(x / z);
                slope[(ny * 256 + nx) * 2 + 1] = (float)(y / z);
                weight[ny * 256 + nx] = (float)(z * z * z * z);
            }
        int Lut(long i) => (normalRgba[i * 4 + 1] * 256 + normalRgba[i * 4 + 3]) * 2;

        double st = 0, tt = 0, ss = 0;
        for (int y = 2; y < h - 2; y += 3)
            for (int x = 2; x < w - 2; x += 3)
            {
                long i = (long)y * w + x;
                var (sx, sy) = Sobel(c, i, w);
                int t = Lut(i);
                double tx = slope[t], ty = slope[t + 1], q = weight[t / 2];
                st += q * (sx * tx + sy * ty);
                tt += q * (tx * tx + ty * ty);
                ss += q * (sx * sx + sy * sy);
            }
        double r = tt > 0 && ss > 0 ? st / Math.Sqrt(tt * ss) : 0;
        if (r < o.MinCorrelation)
        {
            log?.Invoke($"Warning: lf_normal.dds does not follow the logic map's slopes (r = {r:F2}): heights not refined.");
            return c;
        }
        float k = (float)(st / tt);
        for (int i = 0; i < slope.Length; i++)
            slope[i] *= k;

        log?.Invoke($"lf_normal.dds: slope scale 1/{1 / k:F3} (r = {r:F3}); refining, {o.Iterations} iterations");

        long n = (long)w * h;
        float step = (float)map.Step;
        float smooth = o.Smooth;
        float anchor = (float)(o.AnchorPerStep / (map.Step * map.Step));
        float eta = 1f / (2 * 32 + 2 * 64 * smooth + 2 * anchor);
        var rx = new float[n];
        var ry = new float[n];
        var lap = new float[n];
        for (int it = 0; it < o.Iterations; it++)
        {
            Parallel.For(1, h - 1, y =>
            {
                for (int x = 1; x < w - 1; x++)
                {
                    long i = (long)y * w + x;
                    var (sx, sy) = Sobel(c, i, w);
                    int t = Lut(i);
                    float q = weight[t / 2];
                    rx[i] = q * (sx - slope[t]);
                    ry[i] = q * (sy - slope[t + 1]);
                    lap[i] = c[i - 1] + c[i + 1] + c[i - w] + c[i + w] - 4 * c[i];
                }
            });
            Parallel.For(2, h - 2, y =>
            {
                for (int x = 2; x < w - 2; x++)
                {
                    long i = (long)y * w + x;
                    ushort v = map.Values[i];
                    if (v is 0 or ushort.MaxValue)
                        continue;
                    // The transposed Sobel of the residuals, the bi-Laplacian, and the anchor.
                    float gx = rx[i + w + 1] + 2 * rx[i + 1] + rx[i - w + 1] - rx[i + w - 1] - 2 * rx[i - 1] - rx[i - w - 1];
                    float gy = ry[i + w - 1] + 2 * ry[i + w] + ry[i + w + 1] - ry[i - w - 1] - 2 * ry[i - w] - ry[i - w + 1];
                    float bilap = lap[i - 1] + lap[i + 1] + lap[i - w] + lap[i + w] - 4 * lap[i];
                    float lowest = (float)(map.Lo + (v + StepMargin) * map.Step);
                    float mid = (float)(map.Lo + (v + 0.5) * map.Step);
                    float grad = 2 * (gx + gy) + 2 * smooth * bilap + 2 * anchor * (c[i] - mid);
                    c[i] = Math.Clamp(c[i] - eta * grad, lowest, lowest + (1 - 2 * StepMargin) * step);
                }
            });
            progress?.Invoke((it + 1.0) / o.Iterations);
        }

        float inv = 1f / (map.Hi - map.Lo);
        Parallel.For(0, h, y =>
        {
            for (long i = (long)y * w, end = i + w; i < end; i++)
                if ((ushort)(int)((c[i] - map.Lo) * inv * 65535f) != map.Values[i])
                    c[i] = Middle(map, map.Values[i]);
        });
        return c;
    }

    private static (float X, float Y) Sobel(float[] a, long i, int w) => (
        a[i - w - 1] + 2 * a[i - 1] + a[i + w - 1] - a[i - w + 1] - 2 * a[i + 1] - a[i + w + 1],
        a[i - w - 1] + 2 * a[i - w] + a[i - w + 1] - a[i + w - 1] - 2 * a[i + w] - a[i + w + 1]);
}
