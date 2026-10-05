using TwMapDecompiler.Core.IO;
using TwMapDecompiler.Core.Pieces;
using TwMapDecompiler.Core.Terry;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// The devastated project's layers over the main project's (DevastationLayers), checked by
/// compositing them the way Terry does for each map type (qttoolutility TerrainMapLayer::composite).
/// </summary>
public sealed class DevastationLayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TwMapDecompilerTests", Guid.NewGuid().ToString("N"));

    public DevastationLayerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void HeightDeltaAddsBackToTheDevastatedHeights()
    {
        float[] vanilla = [1.5f, 20f, -3f, 7f];
        float[] devastated = [1.5f, 12.25f, -3f, 9f];
        float[] layer = (float[])devastated.Clone();

        long changed = DevastationLayers.Delta(vanilla, layer);

        Assert.Equal(2, changed);
        Assert.Equal(0f, layer[0]);
        for (int i = 0; i < vanilla.Length; i++)
            Assert.Equal(devastated[i], vanilla[i] + layer[i]); // mode 1: dst += opacity * src
    }

    [Fact]
    public void KeyedLayerReplacesOnlyChangedPixels()
    {
        byte[] vanilla = [3, 4, 5, 6];
        byte[] devastated = [3, 9, 5, 0];

        byte[]? layer = DevastationLayers.Keyed(vanilla, devastated, 255, out long changed);

        Assert.NotNull(layer);
        Assert.Equal(2, changed);
        Assert.Equal([255, 9, 255, 0], layer);
        Assert.Equal(devastated, Composite(vanilla, layer, (d, s) => s != 255 ? s : d)); // mode 5
    }

    [Fact]
    public void KeyedLayerCannotHoldTheTransparentValue()
    {
        Assert.Null(DevastationLayers.Keyed([3, 4], [3, 255], 255, out _));
    }

    [Fact]
    public void UnionLayerIsExactOnlyOverZero()
    {
        byte[]? layer = DevastationLayers.Union([0, 10, 0], [0, 10, 200], out long changed);
        Assert.NotNull(layer);
        Assert.Equal(1, changed);
        Assert.Equal([0, 0, 200], layer);

        // Lowering a pixel (what devastation does to corruption and snow) cannot be expressed.
        Assert.Null(DevastationLayers.Union([0, 90], [0, 40], out _));
        // Neither can raising one that is not 0: dst + src * (1 - dst) needs a fractional src.
        Assert.Null(DevastationLayers.Union([0, 40], [0, 90], out _));
    }

    [Fact]
    public void AlphaOverLayerIsOpaqueWhereChangedAndClearElsewhere()
    {
        byte[] vanilla = [10, 20, 30, 255, 40, 50, 60, 255];
        byte[] devastated = [10, 20, 30, 255, 1, 2, 3, 255];

        byte[]? layer = DevastationLayers.AlphaOver(vanilla, devastated, out long changed);

        Assert.NotNull(layer);
        Assert.Equal(1, changed);
        Assert.Equal([0, 0, 0, 0, 1, 2, 3, 255], layer);
        Assert.Null(DevastationLayers.AlphaOver(vanilla, [10, 20, 30, 255, 1, 2, 3, 0], out _));
    }

    [Fact]
    public void StackKeepsTheMainLayersAsHiddenReferencesUnderAWholeMap()
    {
        var one = new TerryProjectWriter.TerrainLayerRef[] { new("aaaaaaaaaaaaaaa") };
        var two = new TerryProjectWriter.TerrainLayerRef[] { new("aaaaaaaaaaaaaaa"), new("bbbbbbbbbbbbbbb", "painted", Opacity: "0.5") };

        Assert.Equal(one, DevastationLayers.Stack(one, DevastationMapMode.Base, null));
        Assert.Equal([one[0], new("ccccccccccccccc", "devastation")], DevastationLayers.Stack(one, DevastationMapMode.Overlay, "ccccccccccccccc"));
        Assert.Equal(
            [new("aaaaaaaaaaaaaaa", "reference", Visible: "0"), new("ccccccccccccccc", "devastation")],
            DevastationLayers.Stack(one, DevastationMapMode.Own, "ccccccccccccccc"));
        Assert.Equal(
        [
            new("aaaaaaaaaaaaaaa", "reference base", Visible: "0"),
            new("bbbbbbbbbbbbbbb", "reference painted", Visible: "0", Opacity: "0.5"),
            new("ccccccccccccccc", "devastation"),
        ], DevastationLayers.Stack(two, DevastationMapMode.Own, "ccccccccccccccc"));
    }

    [Fact]
    public void ProjectRoundTripsLayerStacks()
    {
        string path = Path.Combine(_root, "test.terry");
        TerryProjectWriter.Write(path, new TerryProjectWriter.ProjectInfo
        {
            TerrainSetup = "terrain/campaigns/test/",
            WorldWidth = 100,
            GlobalLighting = "",
            WaterPlaneMaterial = "",
            Layers = [],
            TerrainMaps =
            [
                new TerryProjectWriter.TerrainMapRef("Height", 8, 4,
                [
                    new TerryProjectWriter.TerrainLayerRef("aaaaaaaaaaaaaaa"),
                    new TerryProjectWriter.TerrainLayerRef("bbbbbbbbbbbbbbb", "painted", Visible: "0", Opacity: "0.5"),
                    new TerryProjectWriter.TerrainLayerRef("ccccccccccccccc", "devastation"),
                ]),
                new TerryProjectWriter.TerrainMapRef("ColorOverlay", 8, 4, "ddddddddddddddd"),
            ],
        }, new TerryIdGenerator(1));

        var maps = TerryProjectReader.ReadTerrainMaps(path);

        Assert.Equal("terrain/campaigns/test/", TerryProjectReader.ReadTerrainSetup(path));
        Assert.Equal(["Height", "ColorOverlay"], maps.Select(m => m.Type));
        Assert.Equal((8, 4), (maps[0].Width, maps[0].Height));
        Assert.Equal(["aaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbb", "ccccccccccccccc"], maps[0].Layers.Select(l => l.Id));
        Assert.Equal(new TerryProjectWriter.TerrainLayerRef("bbbbbbbbbbbbbbb", "painted", "0", "1", "0.5"), maps[0].Layers[1]);
        Assert.Equal("1", maps[1].Layers[0].Visible);
    }

    [Fact]
    public void HardLinkSharesTheDataAndReplacesAnOldName()
    {
        string main = Path.Combine(_root, "main.tif");
        string link = Path.Combine(_root, "devastated.tif");
        File.WriteAllText(main, "one");
        File.WriteAllText(link, "stale");

        string? copied = HardLink.CreateOrCopy(main, link);

        Assert.Equal("one", File.ReadAllText(link));
        if (copied == null)
        {
            using (var stream = new FileStream(main, FileMode.Open, FileAccess.Write))
                stream.Write("two"u8);
            Assert.Equal("two", File.ReadAllText(link));
        }
        File.Delete(link);
        Assert.Equal(copied == null ? "two" : "one", File.ReadAllText(main));
    }

    private static byte[] Composite(byte[] dst, byte[] src, Func<byte, byte, byte> rule) =>
        dst.Zip(src, rule).ToArray();
}
