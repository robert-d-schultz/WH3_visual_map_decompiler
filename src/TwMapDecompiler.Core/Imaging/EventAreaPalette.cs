using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Rpfm;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.Core.Imaging;

/// <summary>One campaign_map_event_areas row: the area's key and the colour Terry shows it in.</summary>
public sealed record EventArea(string Key, TileColor Color);

/// <summary>
/// The event area mask's colour table, read from the <c>campaign_map_event_areas</c> DB table.
/// An event_area_mask.dds pixel is an index into every row of that table - not just the map's
/// own - sorted by key, so the first entry is <c>_empty</c> (black) and 255 is "no area".
/// Checked against CA's wh3_main_combi_map_1 event_area_mask TIFF: its 211-entry table is the
/// AK's 211 rows in key order, colour for colour. As with the blend map, a game update that adds
/// an area moves every index after it, so the table is read, not recorded.
/// </summary>
public sealed class EventAreaPalette
{
    public const string TableName = "campaign_map_event_areas_tables";

    public IReadOnlyList<EventArea> Areas { get; }

    private EventAreaPalette(IReadOnlyList<EventArea> areas) => Areas = areas;

    /// <summary>Reads the table using an already open pack session.</summary>
    public static async Task<EventAreaPalette> ReadAsync(PackFileSource packs, string scratchFolder, CancellationToken ct = default)
    {
        var rows = new List<EventArea>();
        foreach (var row in await packs.ReadTableAsync(TableName, scratchFolder, ct))
        {
            if (Str(row, "key") is not { } key)
                continue;
            rows.Add(new EventArea(key, ReadColour(row, key)));
        }
        return FromRows(rows);
    }

    /// <summary>
    /// Sorts the rows by key. A key in more than one table file keeps its first row, the one
    /// the game loads (table files are read in name order).
    /// </summary>
    public static EventAreaPalette FromRows(IEnumerable<EventArea> rows)
    {
        var areas = rows.DistinctBy(a => a.Key, StringComparer.Ordinal).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        if (areas.Count > 256)
            throw new InvalidDataException($"{areas.Count} event areas do not fit an 8-bit event area mask (at most 256).");
        return new EventAreaPalette(areas);
    }

    /// <summary>256 RGB triplets for an indexed TIFF; indices past the last area are black.</summary>
    public byte[] ToRgbTable()
    {
        var table = new byte[256 * 3];
        for (int i = 0; i < Areas.Count; i++)
        {
            var c = Areas[i].Color;
            table[i * 3] = c.R;
            table[i * 3 + 1] = c.G;
            table[i * 3 + 2] = c.B;
        }
        return table;
    }

    private static string? Str(Dictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var v) && v is JsonValue value && value.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    /// <summary>
    /// RPFM's schema merges the table's colour_red/green/blue columns into one "RRGGBB" colour_hex
    /// cell; the separate columns (as the Assembly Kit's XML has them) are the fallback.
    /// </summary>
    /// <param name="what">What a row is, for error messages ("Event area", "Tree id").</param>
    public static TileColor ReadColour(Dictionary<string, JsonNode?> row, string key, string what = "Event area")
    {
        if (!row.ContainsKey("colour_hex"))
            return new TileColor(Channel(row, "colour_red", key, what), Channel(row, "colour_green", key, what), Channel(row, "colour_blue", key, what));

        string hex = Str(row, "colour_hex") ?? "";
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            throw new InvalidDataException($"{what} '{key}' has no valid colour_hex (got '{hex}').");
        return new TileColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static byte Channel(Dictionary<string, JsonNode?> row, string column, string key, string what) =>
        row.TryGetValue(column, out var v) && v is JsonValue value
        && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n)
        && n is >= 0 and <= 255
            ? (byte)n
            : throw new InvalidDataException($"{what} '{key}' has no valid {column} (got '{v?.ToJsonString()}').");
}
