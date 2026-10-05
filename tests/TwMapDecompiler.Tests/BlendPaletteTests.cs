using System.Text;
using System.Xml.Linq;
using TwMapDecompiler.Core.Imaging;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// The blend map's colour table comes from terrain_textures_campaign.assetdb.xml: the
/// campaign_base_colour entries, in file order. These fix that order (not uid, not the key's
/// sort order) and that the file's other namespaces are ignored.
///
/// The real file is covered separately: the WH3 packs' copy (2026 update, 172 entries) reproduces
/// the old table recorded from cr_combi_expanded_map_1's blend TIFF exactly once its 22 cwb_* and
/// 4 nagash_creep* entries are left out - which is what shifted every index from 40 on.
/// </summary>
public class BlendPaletteTests
{
    private static XDocument Db(params (string Namespace, string Key, int R, int G, int B, int Uid)[] entries)
    {
        var sb = new StringBuilder("<warscape_asset_variation_db serialise_version='3'><meta_data_entries/><entries>");
        foreach (var e in entries)
            sb.Append($"<entry><key serialise_version='1' namespace='{e.Namespace}' key='{e.Key}'/><value serialise_version='1'><variations>" +
                      $"<variation serialise_version='6' filename='terrain/{e.Key}.dds' display_name='{e.Key}' " +
                      $"display_r='{e.R}' display_g='{e.G}' display_b='{e.B}' uid='{e.Uid}'>" +
                      "<flags serialise_version='1' flag0='0' flag1='0'/></variation></variations></value></entry>");
        sb.Append("</entries></warscape_asset_variation_db>");
        return XDocument.Parse(sb.ToString());
    }

    [Fact]
    public void Takes_base_colour_entries_in_file_order()
    {
        var palette = BlendPalette.Parse(Db(
            ("campaign_base_colour", "chaos_creep0", 233, 49, 49, 1),
            ("campaign_base_colour", "cwb_cracks_1", 35, 76, 116, 158),
            ("campaign_normal", "chaos_creep0", 1, 2, 3, 500),
            ("campaign_base_colour", "farm_rice0", 220, 223, 221, 128)));

        Assert.Equal(["chaos_creep0", "cwb_cracks_1", "farm_rice0"], palette.Textures.Select(t => t.Key));
        Assert.Equal("terrain/cwb_cracks_1.dds", palette.Textures[1].Filename);

        byte[] table = palette.ToRgbTable();
        Assert.Equal(768, table.Length);
        Assert.Equal(new byte[] { 233, 49, 49, 35, 76, 116, 220, 223, 221 }, table[..9]);
        Assert.All(table[9..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Rejects_more_textures_than_an_8_bit_map_can_index()
    {
        var entries = Enumerable.Range(0, 257).Select(i => ("campaign_base_colour", $"t{i:000}", 1, 2, 3, i)).ToArray();
        Assert.Throws<InvalidDataException>(() => BlendPalette.Parse(Db(entries)));
    }

    [Fact]
    public void Rejects_a_file_without_base_colours()
    {
        Assert.Throws<InvalidDataException>(() => BlendPalette.Parse(Db(("campaign_normal", "x", 1, 2, 3, 1))));
    }

    [Fact]
    public void Rejects_a_missing_colour_channel()
    {
        var doc = Db(("campaign_base_colour", "x", 1, 2, 3, 1));
        doc.Descendants("variation").Single().Attribute("display_g")!.Remove();
        Assert.Throws<InvalidDataException>(() => BlendPalette.Parse(doc));
    }
}
