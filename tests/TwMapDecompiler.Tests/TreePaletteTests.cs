using TwMapDecompiler.Core.Imaging;
using TwMapDecompiler.Core.Tiles;
using TwMapDecompiler.Core.Trees;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// The tree map's colour table is campaign_tree_ids' distinct colours, sorted by RGB. The real
/// table is covered separately: CA's wh3_main_combi_map_1 tree TIFF carries the current
/// table's 70 colours in exactly this order.
/// </summary>
public class TreePaletteTests
{
    private static TreeId Tree(string id, byte r, byte g, byte b) => new(id, new TileColor(r, g, b));

    [Fact]
    public void Sorts_distinct_colours_by_rgb_and_shares_them_between_variants()
    {
        var palette = TreePalette.FromRows([
            Tree("tree_large_burnt_01", 255, 255, 255),
            Tree("grass_tze_01", 0, 195, 190),
            Tree("tree_cwb_burn_01", 0, 127, 1),
            Tree("tree_cwb_burn_02", 0, 127, 1),
            Tree("tree_large_evil_01", 25, 25, 25),
        ]);

        Assert.Equal([new(0, 127, 1), new(0, 195, 190), new(25, 25, 25), new TileColor(255, 255, 255)], palette.Colors);
        Assert.Equal(5, palette.TreeIdCount);
        Assert.Equal((byte)0, palette.IndexOf("tree_cwb_burn_01"));
        Assert.Equal((byte)0, palette.IndexOf("tree_cwb_burn_02"));
        Assert.Equal((byte)3, palette.IndexOf("tree_large_burnt_01"));
        Assert.Null(palette.IndexOf("tree_cwb_burn"));

        byte[] table = palette.ToRgbTable();
        Assert.Equal(768, table.Length);
        Assert.Equal(new byte[] { 0, 127, 1, 0, 195, 190, 25, 25, 25, 255, 255, 255 }, table[..12]);
        Assert.All(table[12..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_tree_id_in_two_table_files_keeps_its_first_row()
    {
        var palette = TreePalette.FromRows([Tree("a_01", 9, 9, 9), Tree("b_01", 1, 1, 1), Tree("a_01", 5, 5, 5)]);
        Assert.Equal([new(1, 1, 1), new TileColor(9, 9, 9)], palette.Colors);
        Assert.Equal((byte)1, palette.IndexOf("a_01"));
    }

    [Fact]
    public void Rejects_more_colours_than_fit_below_the_empty_value()
    {
        var rows = Enumerable.Range(0, 256).Select(i => Tree($"t_{i:000}", (byte)i, 0, 0));
        Assert.Throws<InvalidDataException>(() => TreePalette.FromRows(rows));
    }

    [Fact]
    public void Renderer_paints_each_instance_with_its_tree_ids_colour_and_skips_unknown_ones()
    {
        var palette = TreePalette.FromRows([Tree("grass_01", 40, 80, 100), Tree("shrubs_01", 0, 0, 1)]);
        var list = new TreeListRenderer.TreeList(10, 10, [
            new("grass_01", 0, 0, 0),
            new("shrubs_01", 10, 0, 0),
            new("unknown_01", 5, 0, 0),
        ]);
        var log = new List<string>();

        var result = TreeListRenderer.Render(list, palette, 4, 4, log.Add);

        // Cell (0, 0) is the south-west 2x2 block, cell (1, 0) the south-east one; +Z is north.
        Assert.Equal(new byte[]
        {
            255, 255, 255, 255,
            255, 255, 255, 255,
            1, 1, 0, 0,
            1, 1, 0, 0,
        }, result.Pixels);
        Assert.Single(log, l => l.Contains("unknown_01"));
    }
}
