namespace TwMapDecompiler.Core.Imaging;

/// <summary>
/// The stretch BOB puts a campaign snow mask through. Terry's SnowMask is a quarter of the height
/// map, like the tree and corruption maps (2880x1941 on combi), but BOB compiles it one pixel bigger
/// each way, rounded up to whole BC4 blocks (snow_mask.dds 2884x1944), by stretching it bilinearly
/// over the same extent. The values go through unchanged, apart from BC4 noise.
///
/// Measured on rob's old IEE project, whose 3200x1941 TIFF BOB compiled to 3204x1944: the content
/// drifts from 0 at the north-west corner to 3.75 pixels east and 3 south (3204/3200, 1944/1941),
/// and a bilinear stretch of the TIFF matches the DDS to 0.26 levels on average. Every vanilla
/// campaign map fits the size rule (chaos 2216x1445 -> 2220x1448, prologue 1600x1201 -> 1604x1204),
/// and so do rob's mod projects. A TIFF that is already the compiled size goes through unchanged.
/// </summary>
public static class SnowMaskStretch
{
    /// <summary>The snow_mask.dds size BOB compiles a snow mask of this size to.</summary>
    public static (int Width, int Height) CompiledSize(int width, int height) =>
        (RoundUpToBlock(width + 1), RoundUpToBlock(height + 1));

    /// <summary>
    /// Undoes the stretch: turns a decoded snow_mask.dds back into a <paramref name="width"/> x
    /// <paramref name="height"/> Terry map. The method is a bilinear shrink, then one back-projection
    /// step: stretch the guess again, shrink what it misses and add that on. On the old IEE project
    /// this recovers the TIFF to 0.39 levels on average (a plain shrink gets 0.51). The rest is BC4 loss.
    /// </summary>
    public static byte[] Unstretch(byte[] dds, int ddsWidth, int ddsHeight, int width, int height)
    {
        float[] compiled = new float[dds.Length];
        for (int i = 0; i < dds.Length; i++)
            compiled[i] = dds[i];

        float[] guess = Resample(compiled, ddsWidth, ddsHeight, width, height);
        float[] missed = Resample(guess, width, height, ddsWidth, ddsHeight);
        for (int i = 0; i < missed.Length; i++)
            missed[i] = compiled[i] - missed[i];
        float[] correction = Resample(missed, ddsWidth, ddsHeight, width, height);

        var result = new byte[guess.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)Math.Clamp(Math.Round(guess[i] + correction[i]), 0, 255);
        return result;
    }

    /// <summary>
    /// Bilinear resize that keeps the map's extent: pixel centres line up, target pixel i samples
    /// the source at (i + 0.5) * from / to - 0.5, clamped to the edge pixels.
    /// </summary>
    public static float[] Resample(float[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var (columns, columnWeights) = Taps(sourceWidth, width);
        var rows = new float[(long)sourceHeight * width];
        Parallel.For(0, sourceHeight, y =>
        {
            long from = (long)y * sourceWidth, to = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                int c = columns[x];
                float w = columnWeights[x];
                rows[to + x] = source[from + c] * (1 - w) + source[from + Math.Min(c + 1, sourceWidth - 1)] * w;
            }
        });

        var (lines, lineWeights) = Taps(sourceHeight, height);
        var result = new float[(long)width * height];
        Parallel.For(0, height, y =>
        {
            long first = (long)lines[y] * width, second = (long)Math.Min(lines[y] + 1, sourceHeight - 1) * width;
            long to = (long)y * width;
            float w = lineWeights[y];
            for (int x = 0; x < width; x++)
                result[to + x] = rows[first + x] * (1 - w) + rows[second + x] * w;
        });
        return result;
    }

    private static (int[] First, float[] Weight) Taps(int from, int to)
    {
        var first = new int[to];
        var weight = new float[to];
        for (int i = 0; i < to; i++)
        {
            double s = Math.Clamp((i + 0.5) * from / to - 0.5, 0, from - 1);
            int f = Math.Max(0, Math.Min((int)s, from - 2));
            first[i] = f;
            weight[i] = (float)(s - f);
        }
        return (first, weight);
    }

    private static int RoundUpToBlock(int size) => (size + 3) / 4 * 4;
}
