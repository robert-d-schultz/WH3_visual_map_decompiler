using TwMapDecompiler.Core.Bmd;
using TwMapDecompiler.Core.Pipeline;
using TwMapDecompiler.Core.Terry;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// Map scale and padding in hexes (MapScale, MapResizer), on the combi map's sizes: hex grid
/// 1440x970, tile map 2880x1941, height map 11520x7764, shroud 5760x3882, patch mask 125x84.
/// </summary>
public sealed class MapResizeTests
{
    private static readonly HexGrid CombiGrid = new(1440, 970);

    private static MapResizer Combi(int left, int right, int top, int bottom, double scale = 1) =>
        new(MapScale.Plan(CombiGrid, scale), new MapPadding(left, right, top, bottom));

    // ------------------------------------------------------------ padding

    [Fact]
    public void EachMapGrowsByItsOwnPixelsPerHex()
    {
        var resizer = Combi(1, 2, 3, 4);

        Assert.Equal(new MapResizer.Layout(2880, 1941, 2, 4, 6, 8), resizer.LayoutOf(2880, 1941));     // tile map, tree, masks (snow included)
        Assert.Equal(new MapResizer.Layout(11520, 7764, 8, 16, 24, 32), resizer.LayoutOf(11520, 7764)); // height, blend, overlays, normals
        Assert.Equal(new MapResizer.Layout(5760, 3882, 4, 8, 12, 16), resizer.LayoutOf(5760, 3882));    // shroud heights
        Assert.Equal((2886, 1955), resizer.Size(2880, 1941));
        Assert.Equal(new HexGrid(1443, 977), resizer.Final);
    }

    [Fact]
    public void PatchMaskPaddingIsRounded()
    {
        // 125 / 1440 pixels per hex across, 84 / 970 down.
        var layout = Combi(1, 12, 5, 6).LayoutOf(125, 84);
        Assert.Equal((0, 1, 0, 1), (layout.Left, layout.Right, layout.Top, layout.Bottom));
    }

    [Fact]
    public void WorldGrowsAndMovesByTheHexesOnTheLeftAndBottom()
    {
        var resizer = Combi(10, 30, 7, 20);
        const double worldWidth = 961.3;

        Assert.Equal(961.3 * 1480 / 1440, resizer.WorldWidth(worldWidth), 9);
        Assert.Equal(961.3 / 1440 * 10, resizer.OffsetX(worldWidth), 9);
        Assert.Equal(961.3 / 1440 * 20, resizer.OffsetZ(worldWidth), 9);
        // The height map's texels stay the same world size.
        var (width, _) = resizer.Size(11520, 7764);
        Assert.Equal(worldWidth / 11520, resizer.WorldWidth(worldWidth) / width, 12);
    }

    [Fact]
    public void NewPixelsRepeatTheEdge()
    {
        // One hex at 2 pixels per hex is 2x3 pixels (the staggered half hex); one hex on the left and the top.
        var resizer = new MapResizer(MapScale.None(new HexGrid(1, 1)), new MapPadding(1, 0, 1, 0));
        byte[] map = [1, 2, 3, 4, 5, 6];

        var (data, width, height) = resizer.Resize(map, 2, 3, Sampling.Nearest);

        Assert.Equal((4, 5), (width, height));
        Assert.Equal(new byte[]
        {
            1, 1, 1, 2,
            1, 1, 1, 2,
            1, 1, 1, 2,
            3, 3, 3, 4,
            5, 5, 5, 6,
        }, data);
    }

    [Fact]
    public void FillReplacesTheEdgeForEveryChannel()
    {
        var resizer = new MapResizer(MapScale.None(new HexGrid(1, 1)), new MapPadding(0, 1, 0, 1));
        byte[] rgba = [10, 20, 30, 255];

        var (data, width, height) = resizer.Resize(rgba, 1, 1, Sampling.Linear, channels: 4, fill: new byte[4]);

        Assert.Equal((2, 2), (width, height));
        Assert.Equal(new byte[]
        {
            10, 20, 30, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
        }, data);
    }

    [Fact]
    public void NothingToDoReturnsTheMapItself()
    {
        float[] map = [1f, 2f];
        Assert.Same(map, MapResizer.Identity.Resize(map, 2, 1, Sampling.Linear).Data);
        Assert.Same(map, Combi(0, 0, 0, 0, scale: 1).Resize(map, 2, 1, Sampling.Linear).Data);
    }

    // ------------------------------------------------------------ scale

    [Fact]
    public void ScaleSnapsToAnEvenWidthAndTheRowsFollow()
    {
        var scale = MapScale.Plan(CombiGrid, 1.25);
        Assert.Equal(new HexGrid(1800, 1213), scale.Scaled);
        Assert.Equal(1.25, scale.Effective);
        Assert.False(scale.Capped);
        // 1.25 x 970.5 = 1213.125 hexes of map on a grid 1213.5 high.
        Assert.Equal(0.375, scale.NorthResidual, 9);

        // 1440 x 1.001 = 1441.44: the nearest even width is 1442.
        var small = MapScale.Plan(CombiGrid, 1.001);
        Assert.Equal(1442, small.Scaled.Width);
        Assert.Equal(1442.0 / 1440, small.Effective);
        Assert.Equal(971, small.Scaled.Height);

        // Too small a change to move the width by two hexes.
        Assert.True(MapScale.Plan(CombiGrid, 1.0005).IsNone);
    }

    [Fact]
    public void ScaleIsCappedAt2048Hexes()
    {
        var wide = MapScale.Plan(CombiGrid, 2);
        Assert.True(wide.Capped);
        Assert.Equal(new HexGrid(2048, 1380), wide.Scaled);
        Assert.Null(wide.Scaled.Problem);

        var tall = MapScale.Plan(new HexGrid(500, 1500), 2);
        Assert.True(tall.Capped);
        Assert.True(tall.Scaled.Height <= HexGrid.MaxSize);
        Assert.Null(tall.Scaled.Problem);
    }

    [Fact]
    public void ShrinkingWorksToo()
    {
        var scale = MapScale.Plan(CombiGrid, 0.5);
        Assert.Equal(new HexGrid(720, 485), scale.Scaled);
        Assert.Equal(0.5, scale.Effective);
    }

    [Fact]
    public void HexGridProblems()
    {
        Assert.Null(new HexGrid(1440, 970).Problem);
        Assert.NotNull(new HexGrid(1441, 970).Problem);
        Assert.NotNull(new HexGrid(2050, 970).Problem);
        Assert.NotNull(new HexGrid(1440, 2049).Problem);
        // Odd padding across makes the width odd.
        Assert.NotNull(Combi(1, 0, 0, 0).Final.Problem);
    }

    [Fact]
    public void ScaledMapsKeepCombisLayout()
    {
        // Every map stays its pixels per hex times the grid, plus the half hex of the staggered columns.
        var resizer = Combi(0, 0, 0, 0, scale: 1.25);
        Assert.Equal((2 * 1800, 2 * 1213 + 1), resizer.Size(2880, 1941));
        Assert.Equal((8 * 1800, 8 * 1213 + 4), resizer.Size(11520, 7764));
        Assert.Equal((4 * 1800, 4 * 1213 + 2), resizer.Size(5760, 3882));
    }

    [Fact]
    public void WorldScalesAboutTheSouthWestCornerThenPads()
    {
        var resizer = Combi(10, 30, 7, 20, scale: 1.25);
        const double worldWidth = 961.3;

        Assert.Equal(1.25, resizer.Factor);
        Assert.Equal(961.3 * (1800 + 40) / 1440, resizer.WorldWidth(worldWidth), 9);
        // Padding is counted in hexes, which keep their world size.
        Assert.Equal(961.3 / 1440 * 10, resizer.OffsetX(worldWidth), 9);
        Assert.Equal(961.3 / 1440 * 20, resizer.OffsetZ(worldWidth), 9);
        var (width, _) = resizer.Size(11520, 7764);
        Assert.Equal(worldWidth / 11520, resizer.WorldWidth(worldWidth) / width, 12);
    }

    /// <summary>A 2x4 hex grid at 2 pixels per hex: a 4x9 map (the extra row is the staggered half hex).</summary>
    private static readonly MapResizer Doubled = new(MapScale.Plan(new HexGrid(2, 4), 2), MapPadding.None);

    [Fact]
    public void NearestDoublesPixelsAndAnchorsTheSouth()
    {
        // 4x9 hexes: 8 x 19 pixels. 4.5 hexes of map become 9, on a grid 9.5 high: the north row repeats the edge.
        Assert.Equal(new HexGrid(4, 9), Doubled.Final);
        byte[] map = [.. Enumerable.Range(0, 36).Select(i => (byte)i)];

        var (data, width, height) = Doubled.Resize(map, 4, 9, Sampling.Nearest);

        Assert.Equal((8, 19), (width, height));
        byte[] Row(int r) => [.. Enumerable.Range(0, 8).Select(x => map[r * 4 + x / 2])];
        var rows = new[] { 0 }.Concat(Enumerable.Range(0, 18).Select(y => y / 2));
        Assert.Equal(rows.SelectMany(Row).ToArray(), data);
    }

    [Fact]
    public void NorthResidualTakesTheFill()
    {
        float[] map = Enumerable.Repeat(5f, 36).ToArray();
        var (data, width, _) = Doubled.Resize(map, 4, 9, Sampling.Nearest, fill: [-1f]);
        Assert.All(data.Take(width), v => Assert.Equal(-1f, v));
        Assert.All(data.Skip(width), v => Assert.Equal(5f, v));
    }

    [Fact]
    public void LinearIsBilinearWhenEnlarging()
    {
        float[] map = [.. Enumerable.Range(0, 36).Select(i => (float)(i % 4))];

        var (data, width, _) = Doubled.Resize(map, 4, 9, Sampling.Linear);

        Assert.Equal(new[] { 0f, 0.25f, 0.75f, 1.25f, 1.75f, 2.25f, 2.75f, 3f }, data.Skip(5 * width).Take(width).ToArray());
    }

    [Fact]
    public void LinearAveragesWhenShrinking()
    {
        // 4x4 hexes at 2 pixels per hex (8x9) halved to 2x2 hexes (4x5): each pixel covers two each way,
        // so a checkerboard of 0 and 200 averages out.
        var halved = new MapResizer(MapScale.Plan(new HexGrid(4, 4), 0.5), MapPadding.None);
        Assert.Equal(new HexGrid(2, 2), halved.Scale.Scaled);
        byte[] map = [.. Enumerable.Range(0, 72).Select(i => (byte)((i + i / 8) % 2 == 0 ? 0 : 200))];

        var (data, width, height) = halved.Resize(map, 8, 9, Sampling.Linear);

        Assert.Equal((4, 5), (width, height));
        Assert.All(data.Skip(width).Take(3 * width), v => Assert.InRange(v, 70, 130));
    }

    // ------------------------------------------------------------ entities

    [Fact]
    public void LayerEntitiesScaleWithTheMap()
    {
        var cultures = CultureMask.FromRows([new("NONE", "*", 0), new("EMPIRE", "wh_main_emp_empire", 1)]);
        var writer = new TerryLayerWriter(new TerryLayerWriter.Options
        {
            MapName = "m", VanillaMapName = "v", CampaignName = "c", VanillaCampaignName = "c",
            Cultures = cultures, Scale = 2, OffsetX = 10, OffsetZ = 20,
        }, new TerryIdGenerator(1));
        var layer = new CompiledBmd("p");
        layer.Props.Add(new BmdProp { Path = "a.rigid_model_v2", Transform = new BmdTransform(1, 2, 3, ScaleX: 0.5, ScaleY: 0.5, ScaleZ: 0.5) });
        layer.LightProbes.Add(new BmdLightProbe { Transform = new BmdTransform(4, 0, 4), InnerRadius = 1, OuterRadius = 3 });
        layer.PointLights.Add(new BmdPointLight { Transform = new BmdTransform(0, 0, 0), AnimType = "LAT_NONE", FalloffType = "", Radius = 1.5f });
        layer.PolyMeshes.Add(new BmdPolyMesh { Transform = new BmdTransform(0, 0, 0), Material = "m", Vertices = [(0, 0, 0), (1, 0, 0), (1, 0, 2)] });

        string folder = Directory.CreateTempSubdirectory("resize_test").FullName;
        try
        {
            string text = File.ReadAllText(writer.WriteLayers([layer], folder)[0].FilePath);

            Assert.Contains("position=\"12 4 26\" rotation=\"0 0 0\" scale=\"1 1 1\"", text); // prop: moved and enlarged
            Assert.Contains("position=\"18 0 28\" rotation=\"0 0 0\" scale=\"1 1 1\"", text); // probe: moved, transform scale untouched
            Assert.Contains("inner_radius=\"2\" outer_radius=\"6\"", text);
            Assert.Contains("radius=\"3\"", text);
            Assert.Contains("<point x=\"2\" y=\"4\"/>", text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
