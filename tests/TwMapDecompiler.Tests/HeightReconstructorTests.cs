using SyntheticTerrain;
using TwMapDecompiler.Core.Imaging;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// Regression tests for HeightReconstructor built on synthetic terrain with exactly-known
/// ground truth, round-tripped through the real BC6H/BC3 codecs. See tools/SyntheticTerrain
/// and tools/HeightReconstructionBench for the exploratory harness these grew out of.
/// </summary>
public class HeightReconstructorTests
{
    private const int W = TerrainScenes.Width;
    private const int H = TerrainScenes.Height;
    private const double TexelSize = TerrainScenes.TexelSize;

    public static IEnumerable<object[]> SceneNames() => TerrainScenes.All.Select(s => new object[] { s.Name });

    private static (float[] Truth, byte[] Normal, float[] Quantized) Prepare(string sceneName)
    {
        var scene = TerrainScenes.All.Single(s => s.Name == sceneName);
        var (heights, gx, gy) = scene.Evaluate(W, H);
        byte[] normal = RoundTrip.Bc3RoundTrip(RoundTrip.PackNormalDxt5nm(gx, gy, W, H, TexelSize), W, H);
        return (heights.Select(h => (float)h).ToArray(), normal, RoundTrip.Bc6RoundTrip(heights, W, H));
    }

    /// <summary>Swaps the X and Y channels of a DXT5nm normal - the convention error that once shipped.</summary>
    private static byte[] SwapNormalChannels(byte[] normal)
    {
        var swapped = new byte[normal.Length];
        for (int i = 0; i < normal.Length; i += 4)
        {
            swapped[i] = normal[i];
            swapped[i + 1] = normal[i + 3];
            swapped[i + 2] = normal[i + 2];
            swapped[i + 3] = normal[i + 1];
        }
        return swapped;
    }

    /// <summary>
    /// The headline guarantee, measured the way the eye sees it. Terrain is shaded from the
    /// normal map, so RMS normal error is the metric that tracks whether the surface reads as
    /// smooth; BC6H's plateaus put large errors in the derivative while keeping the height
    /// itself close, which is exactly why height RMSE alone understates the artifact.
    /// </summary>
    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Reconstruction_HalvesNormalError_OnEveryScene(string sceneName)
    {
        var (truth, normal, quantized) = Prepare(sceneName);

        float[] corrected = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize);

        double baseErr = Metrics.NormalErrorDegrees(quantized, truth, W, H, TexelSize);
        double corrErr = Metrics.NormalErrorDegrees(corrected, truth, W, H, TexelSize);

        Assert.True(corrErr < baseErr * 0.5,
            $"[{sceneName}] normal error {corrErr:F2}deg should be less than half the raw quantized {baseErr:F2}deg");
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void Reconstruction_ImprovesHeightAccuracyAndSmoothness(string sceneName)
    {
        var (truth, normal, quantized) = Prepare(sceneName);

        float[] corrected = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize);

        var (baseRmse, _) = Metrics.Compare(truth, quantized);
        var (corrRmse, _) = Metrics.Compare(truth, corrected);

        Assert.True(corrRmse < baseRmse,
            $"[{sceneName}] corrected RMSE {corrRmse:F5} did not improve on raw quantized {baseRmse:F5}");
        Assert.True(Metrics.Roughness(corrected, W, H) < Metrics.Roughness(quantized, W, H),
            $"[{sceneName}] corrected surface is not smoother than the raw quantized one");
    }

    /// <summary>
    /// The smoothing term is what lets the solve stop pretending either input knows anything
    /// at the finest scales - below about 16 texels the height map is all BC6H staircase and
    /// the normal map's curl exceeds its own gradient variation. Disabling it should measurably
    /// hurt, otherwise the term is not doing its job.
    /// </summary>
    [Fact]
    public void SmoothingTerm_ImprovesOnPlainGradientIntegration()
    {
        var (truth, normal, quantized) = Prepare("terrain");

        float[] withSmoothing = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize);
        float[] without = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize,
            options: new HeightReconstructor.Options { SmoothingCutoffTexels = 0 });

        double withErr = Metrics.NormalErrorDegrees(withSmoothing, truth, W, H, TexelSize);
        double withoutErr = Metrics.NormalErrorDegrees(without, truth, W, H, TexelSize);

        Assert.True(withErr < withoutErr,
            $"smoothing term gave {withErr:F2}deg, disabling it gave {withoutErr:F2}deg - the term earns nothing");
    }

    /// <summary>
    /// Locks the DXT5nm channel convention (X in alpha, Y in green). Reading them the other
    /// way round is not a small error - on a real map it drops the correlation between the
    /// normal's slope and the height map's finite differences from 0.65 to 0.001.
    /// </summary>
    [Theory]
    [InlineData("terrain")]
    [InlineData("ramp")]
    public void CorrectChannelOrder_BeatsSwappedChannels(string sceneName)
    {
        var (truth, normal, quantized) = Prepare(sceneName);

        float[] correct = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize);
        float[] swapped = HeightReconstructor.Reconstruct(quantized, W, H, SwapNormalChannels(normal), TexelSize);

        double correctErr = Metrics.NormalErrorDegrees(correct, truth, W, H, TexelSize);
        double swappedErr = Metrics.NormalErrorDegrees(swapped, truth, W, H, TexelSize);

        Assert.True(correctErr < swappedErr * 0.9,
            $"[{sceneName}] reading X from alpha ({correctErr:F2}deg) should clearly beat reading it from " +
            $"green ({swappedErr:F2}deg); if these are close the channel convention is no longer being exercised");
    }

    /// <summary>
    /// Pins the channel semantics directly, without relying on any scene's statistics: a
    /// hand-built normal describing a pure +X tilt and a perfectly flat Y must produce a
    /// height field that rises along X and stays flat along Y. If X and Y are read from the
    /// wrong channels this comes out transposed.
    /// </summary>
    [Fact]
    public void NormalWithPureXTilt_ProducesHeightRisingAlongXOnly()
    {
        const int size = 64;
        const double slopeX = 0.5, texelSize = 0.024;

        var quantized = new float[size * size];
        Array.Fill(quantized, 10.0f);

        double nz = 1.0 / Math.Sqrt(1.0 + slopeX * slopeX);
        double nx = -slopeX * nz;
        var normal = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            normal[i * 4 + 0] = 255;
            normal[i * 4 + 1] = (byte)Math.Round((0.0 + 1.0) * 127.5); // Y in green: no tilt
            normal[i * 4 + 2] = 0;
            normal[i * 4 + 3] = (byte)Math.Round((nx + 1.0) * 127.5);  // X in alpha
        }

        float[] result = HeightReconstructor.Reconstruct(quantized, size, size, normal, texelSize,
            options: new HeightReconstructor.Options { TexelSize = texelSize });

        double ColumnMean(int x) => Enumerable.Range(0, size).Average(y => (double)result[y * size + x]);
        double RowMean(int y) => Enumerable.Range(0, size).Average(x => (double)result[y * size + x]);

        double xSpan = ColumnMean(size - 1) - ColumnMean(0);
        double ySpan = RowMean(size - 1) - RowMean(0);

        Assert.True(xSpan > 0, $"height should rise along +X, but the span was {xSpan:F5}");
        Assert.True(Math.Abs(ySpan) < xSpan * 0.05,
            $"height should be flat along Y, but the Y span was {ySpan:F5} against an X span of {xSpan:F5}");
    }

    /// <summary>
    /// The texel size is fitted from the two maps rather than taken from world_width, which
    /// the decompiler often only knows as a "height map width / 12" guess - on the combi map
    /// that guess is 3.5x the fitted value.
    /// </summary>
    [Fact]
    public void FittedTexelSize_BeatsABadWorldWidthGuess()
    {
        var (truth, normal, quantized) = Prepare("terrain");

        float[] fitted = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize);
        float[] guessed = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize,
            options: new HeightReconstructor.Options { TexelSize = TexelSize * 3.5 });

        double fittedErr = Metrics.NormalErrorDegrees(fitted, truth, W, H, TexelSize);
        double guessedErr = Metrics.NormalErrorDegrees(guessed, truth, W, H, TexelSize);

        Assert.True(fittedErr < guessedErr,
            $"fitted texel size gave {fittedErr:F2}deg, the 3.5x-off guess gave {guessedErr:F2}deg");
    }

    /// <summary>
    /// The safety clamp has to actually bind when asked to, so that a mismatched normal map
    /// cannot move the height field arbitrarily far.
    /// </summary>
    [Fact]
    public void SafetyClamp_BoundsHowFarTexelsCanMove()
    {
        var (_, normal, quantized) = Prepare("terrain");

        float[] clamped = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize,
            options: new HeightReconstructor.Options { SafetyClampSteps = 1.0 });
        float[] unclamped = HeightReconstructor.Reconstruct(quantized, W, H, normal, TexelSize,
            options: new HeightReconstructor.Options { SafetyClampSteps = double.PositiveInfinity });

        double maxClamped = quantized.Select((q, i) => Math.Abs(clamped[i] - q)).Max();
        double maxUnclamped = quantized.Select((q, i) => Math.Abs(unclamped[i] - q)).Max();

        Assert.True(maxClamped < maxUnclamped,
            $"clamped run moved a texel by {maxClamped:F5}, unclamped by {maxUnclamped:F5} - the clamp did nothing");
    }
}
