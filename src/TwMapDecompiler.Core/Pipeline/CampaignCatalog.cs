using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Rpfm;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.Core.Pipeline;

/// <summary>
/// One row of the <c>campaigns</c> DB table: everything needed to locate a campaign's
/// files. Picking a campaign picks all of these.
/// </summary>
/// <param name="CampaignName">campaigns.campaign_name, e.g. wh3_main_combi.</param>
/// <param name="MapName">campaigns.map_name - the campaign map key (wh3_main_combi_map_5).</param>
/// <param name="DisplayLocation">campaigns.display_location - the campaign_maps/&lt;name&gt; folder,
/// which holds display/trees/trees.campaign_tree_list (wh3_main_combi_map_5).</param>
/// <param name="BattlePath">campaigns.battle_path - terrain/battles/&lt;name&gt;, the battle
/// catchment Terry project (wh3_main_combi_map).</param>
/// <param name="TerrainLocation">campaigns.terrain_location - terrain/campaigns/&lt;name&gt;,
/// the compiled campaign terrain (wh3_main_combi_map_1).</param>
/// <param name="WorldWidth">maxx - minx from campaign_map_playable_areas, when a row matches.</param>
/// <param name="TerrainLocationDevastate">campaigns.terrain_location_devastate - terrain/campaigns/&lt;name&gt;,
/// the devastated map's event-area pieces (wh3_main_combi_map_devastate_1); null when the campaign has none.</param>
/// <param name="Hexes">The campaign hex grid (tile_list.bin's header, 1440x970 on combi), which the map scale and
/// padding are counted on; null when the compiled map has no tile_list.bin.</param>
public sealed record CampaignEntry(
    string CampaignName,
    string MapName,
    string DisplayLocation,
    string BattlePath,
    string TerrainLocation,
    double? WorldWidth,
    string? TerrainLocationDevastate = null,
    HexGrid? Hexes = null)
{
    public override string ToString() => CampaignName;
}

/// <summary>Reads the campaign list from the game's packs.</summary>
public static class CampaignCatalog
{
    private const string CampaignsTable = "campaigns_tables";
    private const string PlayableAreasTable = "campaign_map_playable_areas_tables";

    /// <summary>Opens its own short-lived RPFM session, reads the tables and disconnects.</summary>
    public static async Task<List<CampaignEntry>> LoadAsync(string? rpfmFolder, string game, Action<string> log, CancellationToken ct = default)
    {
        using var packs = await PackFileSource.OpenAsync(rpfmFolder, game, log, ct);
        string scratch = Path.Combine(Path.GetTempPath(), "TwMapDecompiler", "catalog_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            return await ReadAsync(packs, scratch, ct);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Reads the campaign list using an already open pack session.</summary>
    public static async Task<List<CampaignEntry>> ReadAsync(PackFileSource packs, string scratchFolder, CancellationToken ct = default)
    {
        var campaigns = await packs.ReadTableAsync(CampaignsTable, scratchFolder, ct);
        var areas = await packs.ReadTableAsync(PlayableAreasTable, scratchFolder, ct);

        var entries = new List<CampaignEntry>();
        foreach (var row in campaigns)
        {
            string? campaignName = Str(row, "campaign_name");
            if (campaignName == null)
                continue;
            string mapName = Str(row, "map_name") ?? campaignName;

            // The playable area row carries the map's extent; it keys on the campaign map name,
            // with the campaign key as a fallback (several campaigns can share one map).
            var area = areas.FirstOrDefault(a => Str(a, "mapname") == mapName)
                       ?? areas.FirstOrDefault(a => Str(a, "campaign_key") == campaignName);
            double? width = area != null && Num(area, "maxx") is { } maxX ? maxX - (Num(area, "minx") ?? 0) : null;

            entries.Add(new CampaignEntry(
                campaignName,
                mapName,
                Str(row, "display_location") ?? mapName,
                Str(row, "battle_path") ?? DecompileSettings.DeriveBattleMapName(mapName),
                Str(row, "terrain_location") ?? mapName,
                width,
                Str(row, "terrain_location_devastate")));
        }

        // Each map's hex grid, from the header of its tile_list.bin (several campaigns can share a map).
        var grids = new Dictionary<string, HexGrid?>(StringComparer.OrdinalIgnoreCase);
        foreach (string terrain in entries.Select(e => e.TerrainLocation).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string? local = await packs.ExtractFileAsync($"terrain/campaigns/{terrain}/tile_list.bin", Path.Combine(scratchFolder, "hexes"), ct);
            grids[terrain] = local == null ? null : ReadHexGrid(local);
        }

        return entries.Select(e => e with { Hexes = grids[e.TerrainLocation] })
            .OrderBy(e => e.CampaignName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The hex grid in a tile_list.bin's header, or null when it cannot be read.</summary>
    public static HexGrid? ReadHexGrid(string tileListPath)
    {
        try
        {
            var (width, height) = TileMapRenderer.ReadHexGrid(tileListPath);
            return width > 0 && height > 0 ? new HexGrid(width, height) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Str(Dictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var v) && v is JsonValue value && value.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private static double? Num(Dictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var v) && v is JsonValue value && value.TryGetValue(out double d) ? d : null;
}
