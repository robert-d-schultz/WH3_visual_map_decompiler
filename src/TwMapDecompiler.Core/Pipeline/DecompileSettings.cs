using System.Text.RegularExpressions;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.Core.Pipeline;

/// <summary>
/// Picking a campaign is the whole input: the <c>campaigns</c> DB table gives the
/// terrain folder, the display location holding the tree list and the battle path, and
/// <c>campaign_map_playable_areas</c> gives the world width. Everything else is an
/// optional override for renaming the output or working from local files.
/// </summary>
public sealed record DecompileSettings
{
    /// <summary>RPFM install folder from the RPFM_FOLDER environment variable; the CLI and GUI default to it.</summary>
    public static readonly string? DefaultRpfmFolder = Environment.GetEnvironmentVariable("RPFM_FOLDER");

    /// <summary>campaigns.campaign_name of the map to decompile (e.g. wh3_main_combi).</summary>
    public required string VanillaCampaignName { get; init; }

    // ---------------------------------------------------------------- source overrides (null = from the campaigns table)

    /// <summary>campaigns.terrain_location - terrain/campaigns/&lt;name&gt;.</summary>
    public string? VanillaMapName { get; init; }

    /// <summary>campaigns.display_location - campaign_maps/&lt;name&gt;, which holds the tree list.</summary>
    public string? VanillaCampaignMapName { get; init; }

    /// <summary>campaigns.battle_path - terrain/battles/&lt;name&gt;.</summary>
    public string? VanillaBattlePath { get; init; }

    // ---------------------------------------------------------------- new names (null = keep the vanilla ones)

    /// <summary>Name of the new map / campaign Terry project. Default: the source terrain folder name.</summary>
    public string? MapName { get; init; }

    /// <summary>Campaign name of the new map, used for water materials. Default: the source campaign.</summary>
    public string? CampaignName { get; init; }

    /// <summary>Name of the new battle terrain Terry project. Default: the source battle path.</summary>
    public string? BattleMapName { get; init; }

    /// <summary>
    /// Name of the devastated Terry project (the Devastated project step). Default: the campaign's
    /// terrain_location_devastate, or for a renamed map its name with _map_devastate for _map.
    /// </summary>
    public string? DevastatedMapName { get; init; }

    // ---------------------------------------------------------------- pack access

    /// <summary>RPFM install folder (containing rpfm_server.exe), used to start the server when none is running.</summary>
    public string? RpfmFolder { get; init; } = DefaultRpfmFolder;

    // ---------------------------------------------------------------- input overrides (null = read from the packs)

    /// <summary>Local compiled campaign terrain folder (global_props.bin, full_height_map.dds, ...).</summary>
    public string? InputFolder { get; init; }

    /// <summary>Local trees.campaign_tree_list.</summary>
    public string? TreeListPath { get; init; }

    /// <summary>Local tile shape database folder (terrain/tiles/campaign/_tile_database/tiles).</summary>
    public string? TileDatabaseFolder { get; init; }

    /// <summary>
    /// Local warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml, the blend map's
    /// texture list and colours.
    /// </summary>
    public string? BlendTexturesPath { get; init; }

    /// <summary>Local battle terrain folder with blm_*.compressed_map catchment maps.</summary>
    public string? BattlesFolder { get; init; }

    // ---------------------------------------------------------------- output overrides (null = Assembly Kit folders)

    /// <summary>
    /// Assembly Kit root (the folder containing raw_data and working_data); the projects and the
    /// lighting environment go into its standard subfolders. Default: the path RPFM has configured.
    /// </summary>
    public string? AssemblyKitFolder { get; init; }

    // ---------------------------------------------------------------- misc

    public TileGame Game { get; init; } = TileGame.Warhammer3;

    /// <summary>
    /// Recovers the height detail BC6H quantized away by solving the height field against
    /// lf_normal.dds (see the README). Turning it off writes the decoded heights as they
    /// are - quick, but with the compressor's 4x4 block stepping left in.
    /// </summary>
    public bool ReconstructHeightFromNormals { get; init; } = true;

    /// <summary>
    /// Adds the hardcoded modded cultures (<see cref="Bmd.CultureMask.CustomCultures"/>) to the
    /// culture_mask of every layer entity whose mask names the vanilla culture they follow.
    /// </summary>
    public bool AddCustomCultures { get; init; } = true;

    /// <summary>Overrides the world width (otherwise read from campaign_map_playable_areas).</summary>
    public double? WorldWidthOverride { get; init; }

    /// <summary>
    /// Uniform scale of the whole map, snapped to the nearest hex grid Terry can hold (see
    /// <see cref="MapScale.Plan"/>): the terrain maps are resampled, and positions, heights and
    /// sizes in the world scale with them. 1 leaves the map as it is.
    /// </summary>
    public double Scale { get; init; } = 1;

    /// <summary>
    /// Hexes of the scaled hex grid added to each side of the map: every terrain map grows by its
    /// pixels per hex, the world width with it, and padding the left or bottom moves the props and
    /// environment volumes.
    /// </summary>
    public MapPadding Padding { get; init; } = MapPadding.None;

    /// <summary>Fixed random seed for entity id generation (reproducible output, mainly for testing).</summary>
    public int? RandomSeed { get; init; }

    /// <summary>RPFM's key for <see cref="Game"/>.</summary>
    public string RpfmGameKey => Game == TileGame.ThreeKingdoms ? "three_kingdoms" : "warhammer_3";

    /// <summary>wh3_main_combi_map_1 -> wh3_main_combi.</summary>
    public static string DeriveCampaignName(string mapName)
    {
        var match = Regex.Match(mapName, @"^(.*)_map(_\d+)?$");
        return match.Success ? match.Groups[1].Value : mapName;
    }

    /// <summary>my_map_1 -> my_map_devastate_1, CA's naming (wh3_main_combi_map_devastate_1); otherwise &lt;name&gt;_devastate.</summary>
    public static string DeriveDevastatedMapName(string mapName)
    {
        var match = Regex.Match(mapName, @"^(.*_map)(_\d+)?$");
        return match.Success ? $"{match.Groups[1].Value}_devastate{match.Groups[2].Value}" : $"{mapName}_devastate";
    }

    /// <summary>wh3_main_combi_map_1 -> wh3_main_combi_map (CA's battle terrain folder naming).</summary>
    public static string DeriveBattleMapName(string mapName) => Regex.Replace(mapName, @"_\d+$", "");
}
