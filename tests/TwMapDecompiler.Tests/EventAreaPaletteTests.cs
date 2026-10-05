using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Imaging;
using TwMapDecompiler.Core.Tiles;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// The event area mask's colour table is every campaign_map_event_areas row, sorted by key.
/// The real table is covered separately: CA's wh3_main_combi_map_1 event_area_mask TIFF carries
/// the AK's 211 rows in exactly this order, starting with the black _empty row.
/// </summary>
public class EventAreaPaletteTests
{
    private static EventArea Area(string key, byte r, byte g, byte b) => new(key, new TileColor(r, g, b));

    [Fact]
    public void Sorts_rows_by_key()
    {
        var palette = EventAreaPalette.FromRows([
            Area("wh3_main_combi_province_albion_crater", 47, 255, 47),
            Area("_empty", 0, 0, 0),
            Area("wh3_dlc20_combi_province_frigid_wasteland_crater", 184, 253, 166),
        ]);

        Assert.Equal(["_empty", "wh3_dlc20_combi_province_frigid_wasteland_crater", "wh3_main_combi_province_albion_crater"],
            palette.Areas.Select(a => a.Key));

        byte[] table = palette.ToRgbTable();
        Assert.Equal(768, table.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 184, 253, 166, 47, 255, 47 }, table[..9]);
        Assert.All(table[9..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_key_in_two_table_files_keeps_its_first_row()
    {
        var palette = EventAreaPalette.FromRows([Area("a", 1, 2, 3), Area("b", 4, 5, 6), Area("a", 9, 9, 9)]);
        Assert.Equal(["a", "b"], palette.Areas.Select(a => a.Key));
        Assert.Equal(new TileColor(1, 2, 3), palette.Areas[0].Color);
    }

    [Fact]
    public void Rejects_more_areas_than_an_8_bit_mask_can_index()
    {
        var rows = Enumerable.Range(0, 257).Select(i => Area($"area_{i:000}", 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => EventAreaPalette.FromRows(rows));
    }

    [Fact]
    public void Reads_rpfms_merged_hex_colour_and_the_separate_columns()
    {
        var hex = new Dictionary<string, JsonNode?> { ["key"] = "k", ["colour_hex"] = "B8FDA6" };
        Assert.Equal(new TileColor(184, 253, 166), EventAreaPalette.ReadColour(hex, "k"));

        var split = new Dictionary<string, JsonNode?> { ["key"] = "k", ["colour_red"] = 184, ["colour_green"] = 253, ["colour_blue"] = 166 };
        Assert.Equal(new TileColor(184, 253, 166), EventAreaPalette.ReadColour(split, "k"));

        var bad = new Dictionary<string, JsonNode?> { ["key"] = "k", ["colour_hex"] = "12345" };
        Assert.Throws<InvalidDataException>(() => EventAreaPalette.ReadColour(bad, "k"));
    }
}
