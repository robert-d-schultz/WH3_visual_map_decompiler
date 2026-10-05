using TwMapDecompiler.Core.Imaging;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// BOB's snow mask stretch (SnowMaskStretch): the compiled size, and undoing the stretch.
/// </summary>
public sealed class SnowMaskStretchTests
{
    [Theory]
    [InlineData(2880, 1941, 2884, 1944)] // wh3_main_combi_map_1
    [InlineData(2216, 1445, 2220, 1448)] // wh3_main_chaos_map_1
    [InlineData(1600, 1201, 1604, 1204)] // wh3_main_prologue_map
    [InlineData(3200, 1941, 3204, 1944)] // old IEE project
    [InlineData(1300, 1001, 1304, 1004)] // ovn_mootland_map_1
    [InlineData(1664, 1441, 1668, 1444)] // old ovn_southlands_map_1
    public void CompiledSizeIsOnePixelMoreRoundedUpToBlocks(int width, int height, int ddsWidth, int ddsHeight)
    {
        Assert.Equal((ddsWidth, ddsHeight), SnowMaskStretch.CompiledSize(width, height));
    }

    [Fact]
    public void ResamplingToTheSameSizeChangesNothing()
    {
        float[] map = [0, 10, 20, 30, 40, 50];
        Assert.Equal(map, SnowMaskStretch.Resample(map, 3, 2, 3, 2));
    }

    [Fact]
    public void StretchDriftsTowardsTheSouthEast()
    {
        // A one-pixel line near the east edge lands further east once stretched.
        const int width = 400, height = 1;
        var map = new float[width];
        map[396] = 255;
        var (ddsWidth, _) = SnowMaskStretch.CompiledSize(width, height);

        float[] stretched = SnowMaskStretch.Resample(map, width, height, ddsWidth, height);

        double centre = stretched.Select((v, x) => v * x).Sum() / stretched.Sum();
        Assert.Equal((396 + 0.5) * ddsWidth / width - 0.5, centre, 0.05);
    }

    [Fact]
    public void UnstretchRecoversTheTerryMap()
    {
        const int width = 320, height = 194;
        var (ddsWidth, ddsHeight) = SnowMaskStretch.CompiledSize(width, height);
        var terry = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // Snow fields with soft edges, like the real masks.
                double field = Math.Sin(x * 0.05) * Math.Cos(y * 0.08) + 0.3 * Math.Sin((x + y) * 0.21);
                terry[y * width + x] = (byte)Math.Clamp(Math.Round(128 + field * 200), 0, 255);
            }

        float[] compiled = SnowMaskStretch.Resample(terry.Select(v => (float)v).ToArray(), width, height, ddsWidth, ddsHeight);
        byte[] dds = compiled.Select(v => (byte)Math.Round(v)).ToArray();
        byte[] recovered = SnowMaskStretch.Unstretch(dds, ddsWidth, ddsHeight, width, height);

        double meanError = terry.Zip(recovered, (a, b) => Math.Abs(a - b)).Average();
        Assert.True(meanError < 0.5, $"mean error {meanError:0.###}");
        // A plain shrink leaves the stretch's smoothing in.
        byte[] shrunk = SnowMaskStretch.Resample(dds.Select(v => (float)v).ToArray(), ddsWidth, ddsHeight, width, height)
            .Select(v => (byte)Math.Round(v)).ToArray();
        Assert.True(meanError < terry.Zip(shrunk, (a, b) => Math.Abs(a - b)).Average());
    }
}
