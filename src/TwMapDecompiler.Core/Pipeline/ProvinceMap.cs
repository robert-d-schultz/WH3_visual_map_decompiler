using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Rpfm;

namespace TwMapDecompiler.Core.Pipeline;

/// <summary>
/// The region -> province lookup from the <c>region_to_province_junctions</c> DB table.
/// Prop layers are grouped by province, so the ~640 regions of a campaign map end up as
/// a couple of hundred layers. Regions with no row (seas, rivers, lakes, unknown terrain)
/// are not in the map and keep their own layer.
/// </summary>
public static class ProvinceMap
{
    private const string JunctionsTable = "region_to_province_junctions_tables";

    /// <summary>Reads the junction table using an already open pack session.</summary>
    public static async Task<Dictionary<string, string>> ReadAsync(PackFileSource packs, string scratchFolder, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in await packs.ReadTableAsync(JunctionsTable, scratchFolder, ct))
        {
            if (Str(row, "region") is { } region && Str(row, "province") is { } province)
                map[region] = province;
        }
        return map;
    }

    private static string? Str(Dictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var v) && v is JsonValue value && value.TryGetValue(out string? s) && s.Length > 0 ? s : null;
}
