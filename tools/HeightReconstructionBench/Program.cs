using HeightReconstructionBench;
using SyntheticTerrain;
using TwMapDecompiler.Core.Imaging;

// Manual tuning harness for HeightReconstructor. Runs every synthetic scene through the
// real BC6H/BC3 codecs and reports how the reconstruction compares to exactly-known ground
// truth, so a change to the algorithm or its constants can be justified by numbers before
// it lands.
//
// nrmErr (RMS normal error, degrees) is the primary metric: campaign terrain is shaded from
// the normal map, so normal-space error is what the eye sees. A height field can have fine
// RMSE and still read as visibly stepped. rgh is roughness relative to ground truth - 1.00
// is ideal, above is noisy, below is over-smoothed.
//
// The automated pass/fail regression tests live in tests/TwMapDecompiler.Tests instead;
// this tool is for exploration when tuning, not CI.

const int W = TerrainScenes.Width;
const int H = TerrainScenes.Height;
const double TexelSize = TerrainScenes.TexelSize;

// Built once per scene because the codec round-trips dominate runtime.
var cases = TerrainScenes.All.Select(scene =>
{
    var (heights, gx, gy) = scene.Evaluate(W, H);
    byte[] normal = RoundTrip.Bc3RoundTrip(RoundTrip.PackNormalDxt5nm(gx, gy, W, H, TexelSize), W, H);
    var truth = heights.Select(h => (float)h).ToArray();
    return (
        scene.Name,
        Truth: truth,
        Normal: normal,
        Quantized: RoundTrip.Bc6RoundTrip(heights, W, H),
        TruthRoughness: Metrics.Roughness(truth, W, H));
}).ToArray();

Console.WriteLine($"{"variant",-32} " + string.Join(" ", cases.Select(c => $"{c.Name,-20}")));
Console.WriteLine($"{"",-32} " + string.Join(" ", cases.Select(_ => $"{"nrmErr",7}{"rmse",8}{"rgh",6}")));
Console.WriteLine(new string('-', 32 + cases.Length * 22));

void Row(string label, Func<float[], byte[], float[]> reconstruct)
{
    var cells = new List<string>();
    double meanNormalError = 0;
    foreach (var c in cases)
    {
        float[] o = reconstruct(c.Quantized, c.Normal);
        double nrm = Metrics.NormalErrorDegrees(o, c.Truth, W, H, TexelSize);
        meanNormalError += nrm;
        cells.Add($"{nrm,7:F2}{Metrics.Compare(c.Truth, o).Rmse,8:F5}{Metrics.Roughness(o, W, H) / c.TruthRoughness,6:F2}");
    }
    Console.WriteLine($"{label,-32} " + string.Join(" ", cells) + $"  | mean nrmErr {meanNormalError / cases.Length,5:F2}");
}

float[] Current(float[] q, byte[] nrm, HeightReconstructor.Options? o = null) =>
    HeightReconstructor.Reconstruct(q, W, H, nrm, TexelSize, options: o);

Row("raw BC6H (baseline)", (q, _) => q);

Console.WriteLine("\n-- shipped configuration --");
Row("current", (q, n) => Current(q, n));

Console.WriteLine("\n-- smoothing cutoff: the term that replaced brute-force anchoring --");
foreach (double hi in new[] { 0.0, 2.0, 3.0, 4.0, 6.0 })
    Row($"SmoothingCutoffTexels={hi}", (q, n) => Current(q, n, new HeightReconstructor.Options { SmoothingCutoffTexels = hi }));

Console.WriteLine("\n-- anchor weight: trades broadband terrain against smooth terrain --");
foreach (double aw in new[] { 0.0, 0.5, 1.0, 2.0, 5.0 })
    Row($"AnchorWeight={aw}", (q, n) => Current(q, n, new HeightReconstructor.Options { AnchorWeight = aw }));

Console.WriteLine("\n-- height crossover: expected to be near-irrelevant, the anchors carry this --");
foreach (double lo in new[] { 32.0, 64.0, 128.0 })
    Row($"HeightCrossoverTexels={lo}", (q, n) => Current(q, n, new HeightReconstructor.Options { HeightCrossoverTexels = lo }));

Console.WriteLine("\n-- what the texel-size fit buys (vs the world_width/12 guess) --");
foreach (double ts in new[] { TexelSize, TexelSize * 3.5 })
    Row($"forced texelSize={ts:F4}", (q, n) => Current(q, n, new HeightReconstructor.Options { TexelSize = ts }));

Console.WriteLine("\n-- the previous algorithm, for reference --");
Row("legacy anchored (anchor=20), correct channels",
    (q, n) => LegacyAnchored.Reconstruct(q, W, H, n, TexelSize, swappedChannels: false));
Row("legacy anchored, AS ORIGINALLY SHIPPED (X/Y swapped)",
    (q, n) => LegacyAnchored.Reconstruct(q, W, H, n, TexelSize, swappedChannels: true));

// The analytic scenes are mostly symmetric in X and Y, so swapping the normal's channels
// barely shows up here even though it is catastrophic on a real map (correlation with the
// height map falls from 0.65 to 0.001 on wh3_main_combi_map_1). Kept as a standing reminder
// that this harness cannot be the thing that validates the channel convention.
Console.WriteLine("\n-- channel-swap sensitivity (anisotropic scenes register it most) --");
Row("X/Y channels swapped, current solver", (q, n) =>
{
    var swapped = new byte[n.Length];
    for (int i = 0; i < n.Length; i += 4)
    {
        swapped[i] = n[i];
        swapped[i + 1] = n[i + 3];
        swapped[i + 2] = n[i + 2];
        swapped[i + 3] = n[i + 1];
    }
    return Current(q, swapped);
});
