using System.Globalization;
using System.Xml.Linq;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.Core.Imaging;

/// <summary>One blend texture: its assetdb key and the colour Terry shows it in.</summary>
public sealed record BlendTexture(string Key, string? Filename, TileColor Color);

/// <summary>
/// The blend map's colour table, read from the game's list of campaign terrain textures,
/// warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml. A global_blend.dds
/// pixel is an index into the <c>campaign_base_colour</c> entries of that file, in file order:
///
///   &lt;entries&gt;
///     &lt;entry&gt;
///       &lt;key namespace='campaign_base_colour' key='chaos_creep0'/&gt;
///       &lt;value&gt;&lt;variations&gt;
///         &lt;variation filename='terrain/.../chaos_creep0_base_colour.dds' display_r='233' display_g='49' display_b='49' .../&gt;
///
/// The entries are sorted by key, so a texture added by a game update lands in the middle of
/// the list and moves every index after it - a table recorded from one Terry project goes stale
/// (the 2026 update's cwb_* and nagash_creep* textures shifted everything from index 40 on).
/// The file's other namespaces (campaign_material, campaign_normal, ...) are not indexed by the
/// blend map.
/// </summary>
public sealed class BlendPalette
{
    public const string PackPath = "warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml";

    private const string BaseColourNamespace = "campaign_base_colour";

    public IReadOnlyList<BlendTexture> Textures { get; }

    private BlendPalette(IReadOnlyList<BlendTexture> textures) => Textures = textures;

    public static BlendPalette Load(string path, Action<string>? log = null)
    {
        var palette = Parse(XDocument.Load(path), log);
        log?.Invoke($"{Path.GetFileName(path)}: {palette.Textures.Count} blend textures");
        return palette;
    }

    public static BlendPalette Parse(XDocument doc, Action<string>? log = null)
    {
        var entries = doc.Root?.Element("entries")
            ?? throw new InvalidDataException("Not a warscape_asset_variation_db file: no <entries> element.");

        var textures = new List<BlendTexture>();
        foreach (var entry in entries.Elements("entry"))
        {
            var key = entry.Element("key");
            if ((string?)key?.Attribute("namespace") != BaseColourNamespace)
                continue;

            string name = (string?)key!.Attribute("key") ?? "";
            var variations = entry.Element("value")?.Element("variations")?.Elements("variation").ToList() ?? [];
            if (variations.Count == 0)
                throw new InvalidDataException($"Blend texture '{name}' has no variation, so no display colour.");
            // Every WH3 entry has exactly one; the colour is per texture, so the first one stands for all.
            if (variations.Count > 1)
                log?.Invoke($"Note: blend texture '{name}' has {variations.Count} variations; using the first one's colour.");

            var v = variations[0];
            textures.Add(new BlendTexture(name, (string?)v.Attribute("filename"),
                new TileColor(Channel(v, "display_r", name), Channel(v, "display_g", name), Channel(v, "display_b", name))));
        }

        if (textures.Count == 0)
            throw new InvalidDataException($"No '{BaseColourNamespace}' entries found.");
        if (textures.Count > 256)
            throw new InvalidDataException($"{textures.Count} blend textures do not fit an 8-bit blend map (at most 256).");
        return new BlendPalette(textures);
    }

    /// <summary>256 RGB triplets for an indexed TIFF; indices past the last texture are black.</summary>
    public byte[] ToRgbTable()
    {
        var table = new byte[256 * 3];
        for (int i = 0; i < Textures.Count; i++)
        {
            var c = Textures[i].Color;
            table[i * 3] = c.R;
            table[i * 3 + 1] = c.G;
            table[i * 3 + 2] = c.B;
        }
        return table;
    }

    private static byte Channel(XElement variation, string attribute, string key)
    {
        string? text = (string?)variation.Attribute(attribute);
        if (text == null || !byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value))
            throw new InvalidDataException($"Blend texture '{key}' has no valid {attribute} (got '{text}').");
        return value;
    }
}
