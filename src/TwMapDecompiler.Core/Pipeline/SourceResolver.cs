using System.Globalization;
using TwMapDecompiler.Core.Bmd;
using TwMapDecompiler.Core.Imaging;
using TwMapDecompiler.Core.Rpfm;
using TwMapDecompiler.Core.Terry;

namespace TwMapDecompiler.Core.Pipeline;

/// <summary>Names and local paths for one run: overrides as given, everything else from the packs.</summary>
public sealed class ResolvedSources : IDisposable
{
    // ---- names
    public required string VanillaMapName { get; init; }
    public required string VanillaCampaignName { get; init; }
    public required string MapName { get; init; }
    public required string CampaignName { get; init; }
    public required string BattleMapName { get; init; }

    /// <summary>global_lighting for the campaign .terry, e.g. Weather/default_battle_wh3_main_combi.environment.</summary>
    public required string GlobalLighting { get; init; }

    /// <summary>File name of the environment the campaign project step writes into working_data/Weather.</summary>
    public required string EnvironmentFileName { get; init; }

    /// <summary>
    /// Terrain folder of the campaign's devastated map (campaigns.terrain_location_devastate) when
    /// the Devastated project step runs; null otherwise.
    /// </summary>
    public string? DevastateMapName { get; init; }

    /// <summary>Name and folder of the devastated Terry project, when the Devastated project step runs.</summary>
    public string? DevastatedMapName { get; init; }
    public string? DevastatedOutputFolder { get; init; }

    /// <summary>
    /// These sources describe the devastated project (see <see cref="ForDevastated"/>): MapName and
    /// OutputFolder are its own, and BaseMapName / BaseOutputFolder the main project it is built on.
    /// </summary>
    public bool IsDevastated { get; init; }
    public string? BaseMapName { get; init; }
    public string? BaseOutputFolder { get; init; }

    // ---- inputs (null when not found)
    public string? CampaignFolder { get; init; }

    /// <summary>
    /// Local terrain/campaigns/&lt;terrain_location_devastate&gt;: its pieces, their event_tiles /
    /// event_trees lists and its own environment_collection.xml.
    /// </summary>
    public string? DevastateFolder { get; init; }

    /// <summary>
    /// Local copy of the .environment the compiled map's environment_collection.xml global_lighting
    /// names (a pack path, e.g. Weather/campaign/combi/default.environment), when it was extracted.
    /// </summary>
    public string? MapLightingPath { get; init; }
    public string? TreeListPath { get; init; }
    public string? TileDatabaseFolder { get; init; }

    /// <summary>terrain_textures_campaign.assetdb.xml, the blend map's texture list and colours.</summary>
    public string? BlendTexturesPath { get; init; }
    public string? BattlesFolder { get; init; }

    /// <summary>campaign_map_event_areas in key order, the event area mask's colour table; null when not read.</summary>
    public EventAreaPalette? EventAreas { get; init; }

    /// <summary>campaign_tree_ids colours, the tree map's colour table; null when not read.</summary>
    public TreePalette? TreePalette { get; init; }

    /// <summary>The culture mask's bits from prefab_types_tables; null when not read.</summary>
    public CultureMask? Cultures { get; init; }

    // ---- outputs
    public required string OutputFolder { get; init; }
    public required string BattleOutputFolder { get; init; }

    /// <summary>Assembly Kit root, when one was resolved (the Weather environment needs it).</summary>
    public string? AssemblyKitFolder { get; init; }

    /// <summary>World width from campaign_map_playable_areas (maxx - minx), when it was read.</summary>
    public double? DbWorldWidth { get; init; }

    /// <summary>region -> province from region_to_province_junctions; empty when the table was not read.</summary>
    public IReadOnlyDictionary<string, string> RegionToProvince { get; init; } = new Dictionary<string, string>();

    /// <summary>Scratch folder holding the extracted pack files; deleted on dispose.</summary>
    public string? TempFolder { get; init; }

    /// <summary>Open connection to the packs, when they were read; disposed with this.</summary>
    public PackFileSource? Packs { get; init; }

    /// <summary>
    /// The same sources for the devastated project, built on this (main) project. Shares this one's
    /// pack connection and scratch folder, so only this one is disposed.
    /// </summary>
    public ResolvedSources ForDevastated() => new()
    {
        VanillaMapName = VanillaMapName,
        VanillaCampaignName = VanillaCampaignName,
        MapName = DevastatedMapName!,
        CampaignName = CampaignName,
        BattleMapName = BattleMapName,
        GlobalLighting = GlobalLighting,
        EnvironmentFileName = EnvironmentFileName,
        DevastateMapName = DevastateMapName,
        DevastatedMapName = DevastatedMapName,
        DevastatedOutputFolder = DevastatedOutputFolder,
        IsDevastated = true,
        BaseMapName = MapName,
        BaseOutputFolder = OutputFolder,
        CampaignFolder = CampaignFolder,
        DevastateFolder = DevastateFolder,
        MapLightingPath = MapLightingPath,
        TreeListPath = TreeListPath,
        TileDatabaseFolder = TileDatabaseFolder,
        BlendTexturesPath = BlendTexturesPath,
        BattlesFolder = BattlesFolder,
        EventAreas = EventAreas,
        TreePalette = TreePalette,
        Cultures = Cultures,
        OutputFolder = DevastatedOutputFolder!,
        BattleOutputFolder = BattleOutputFolder,
        AssemblyKitFolder = AssemblyKitFolder,
        DbWorldWidth = DbWorldWidth,
        RegionToProvince = RegionToProvince,
        TempFolder = TempFolder,
        Packs = Packs,
    };

    public void Dispose()
    {
        Packs?.Dispose();
        if (TempFolder == null)
            return;
        try
        {
            Directory.Delete(TempFolder, recursive: true);
        }
        catch
        {
            // Leftovers in %TEMP% are harmless.
        }
    }
}

/// <summary>
/// Turns the selected campaign into concrete names, inputs and output folders.
///
/// From the packs, via RPFM:
///   db/campaigns                                            terrain_location, display_location, battle_path
///   db/campaign_map_playable_areas                          world width (maxx - minx)
///   db/region_to_province_junctions                         province each prop layer groups under
///   db/campaign_map_event_areas                             event area mask colours, in index (key) order
///   db/campaign_tree_ids                                    tree map colour of each tree type
///   db/prefab_types                                         culture of each culture mask bit
///   terrain/campaigns/&lt;terrain_location&gt;/                    compiled campaign terrain
///   terrain/campaigns/&lt;terrain_location_devastate&gt;/          devastated event-area pieces (Devastated project step)
///   campaign_maps/&lt;display_location&gt;/display/trees/trees.campaign_tree_list
///   terrain/tiles/campaign/_tile_database/                  tile shapes (tiles/) and tile set colours (_settings.bin)
///   warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml   blend textures, in blend map index order
///   terrain/battles/&lt;battle_path&gt;/blm_*.compressed_map       catchment maps
/// Outputs:
///   &lt;AK&gt;/raw_data/terrain/campaigns/&lt;map name&gt;/             campaign Terry project
///   &lt;AK&gt;/raw_data/terrain/battles/&lt;battle map name&gt;/        battle Terry project
///   &lt;AK&gt;/raw_data/terrain/campaigns/&lt;devastated name&gt;/      devastated Terry project (Devastated project step)
///   &lt;AK&gt;/working_data/Weather/&lt;campaign&gt;.environment        lighting for the campaign project
/// </summary>
public static class SourceResolver
{
    private const string TileDatabaseRoot = "terrain/tiles/campaign/_tile_database";
    private const string TileDatabasePath = TileDatabaseRoot + "/tiles";

    private static readonly StepId[] CampaignFolderSteps =
    [
        StepId.Layers, StepId.Height, StepId.HeightShroud, StepId.Blend, StepId.ColorOverlay,
        StepId.ColorOverlaySea, StepId.SnowMask, StepId.CorruptionMask, StepId.EventAreaMask,
        StepId.Trees,
        StepId.TileMap, StepId.NormalMap, StepId.EnvironmentVolumes,
        // The campaign project step reads the map's lighting for the Weather environment.
        StepId.TerryProject,
    ];

    private static readonly StepId[] WorldWidthSteps = [StepId.Height, StepId.TerryProject, StepId.BattleProject];

    public static ResolvedSources Resolve(DecompileSettings s, IReadOnlyCollection<StepId> steps, Action<string> log, CancellationToken ct)
        => ResolveAsync(s, steps, log, ct).GetAwaiter().GetResult();

    private static async Task<ResolvedSources> ResolveAsync(DecompileSettings s, IReadOnlyCollection<StepId> selected, Action<string> log, CancellationToken ct)
    {
        // The Devastated project step runs the terrain steps again, for the devastated project.
        bool devastated = selected.Contains(StepId.DevastatedProject);
        IReadOnlyCollection<StepId> steps = devastated
            ? selected.Concat(Decompiler.DevastatedSteps).Append(StepId.TerryProject).ToHashSet()
            : selected;

        // Map scale and padding count hexes, and the hex grid size is in tile_list.bin; the battle project's world width is resized too.
        bool resized = !s.Padding.IsNone || s.Scale != 1;
        bool needCampaign = s.InputFolder == null
                            && (steps.Any(CampaignFolderSteps.Contains) || (resized && steps.Contains(StepId.BattleProject)));
        bool needTreeList = s.TreeListPath == null && steps.Contains(StepId.Trees);
        bool needTileDb = s.TileDatabaseFolder == null && steps.Contains(StepId.TileMap);
        bool needBattles = s.BattlesFolder == null && steps.Contains(StepId.Catchment);
        bool needBlendTextures = s.BlendTexturesPath == null && steps.Contains(StepId.Blend);
        bool needAk = s.AssemblyKitFolder == null;
        // The devastated project's layers are one per event area, not per province.
        bool needProvinces = selected.Contains(StepId.Layers);
        bool needEventAreas = steps.Contains(StepId.EventAreaMask) || devastated;
        bool needCultures = steps.Contains(StepId.Layers);
        bool needTreePalette = steps.Contains(StepId.Trees);
        bool needDevastate = devastated;

        // The campaigns table supplies every name that was not given explicitly.
        bool needCatalog = s.VanillaMapName == null || devastated
                           || (s.VanillaCampaignMapName == null && needTreeList)
                           || (s.VanillaBattlePath == null && (needBattles || s.BattleMapName == null))
                           || (s.WorldWidthOverride == null && steps.Any(WorldWidthSteps.Contains));

        if (!(needCampaign || needTreeList || needTileDb || needBattles || needBlendTextures || needAk || needCatalog || needProvinces
              || needEventAreas || needCultures || needTreePalette || needDevastate))
            return Build(s, null, null, null, null, null, null, null, null, false, log);

        string temp = Path.Combine(Path.GetTempPath(), "TwMapDecompiler", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(temp);
        PackFileSource? packs = null;
        try
        {
            // Kept open for the run: a cloned campaign extracts materials once the steps find them.
            packs = await PackFileSource.OpenAsync(s.RpfmFolder, s.RpfmGameKey, log, ct);

            CampaignEntry? entry = null;
            if (needCatalog)
            {
                var campaigns = await CampaignCatalog.ReadAsync(packs, temp, ct);
                entry = campaigns.FirstOrDefault(c => string.Equals(c.CampaignName, s.VanillaCampaignName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidOperationException(
                        $"No campaigns row for '{s.VanillaCampaignName}'. Available: {string.Join(", ", campaigns.Select(c => c.CampaignName))}");
                log($"campaigns: terrain_location={entry.TerrainLocation}, display_location={entry.DisplayLocation}, battle_path={entry.BattlePath}, " +
                    $"world width={entry.WorldWidth?.ToString("0.####", CultureInfo.InvariantCulture) ?? "?"}");
            }

            Dictionary<string, string>? provinces = null;
            if (needProvinces)
            {
                provinces = await ProvinceMap.ReadAsync(packs, temp, ct);
                // The table covers every campaign in the game, not just this map; the parser
                // logs how many of these rows the map actually uses.
                log($"region_to_province_junctions: {provinces.Count} rows across all campaigns");
            }

            EventAreaPalette? eventAreas = null;
            if (needEventAreas)
                eventAreas = await EventAreaPalette.ReadAsync(packs, temp, ct);

            TreePalette? treePalette = null;
            if (needTreePalette)
                treePalette = await TreePalette.ReadAsync(packs, temp, ct);

            CultureMask? cultures = null;
            if (needCultures && (cultures = await CultureMask.ReadAsync(packs, temp, ct)) != null)
                log($"{CultureMask.TableName}: {cultures.Cultures.Count(c => c != null)} culture mask bits");

            string? akRoot = s.AssemblyKitFolder;
            if (akRoot == null)
            {
                akRoot = await packs.GetAssemblyKitRootAsync(ct)
                    ?? throw new InvalidOperationException(
                        "RPFM has no Assembly Kit path configured for this game. Set it in RPFM's settings, " +
                        "or pass the Assembly Kit folder explicitly.");
                log($"Assembly Kit: {akRoot}");
            }

            string vanillaMapName = s.VanillaMapName ?? entry?.TerrainLocation ?? s.VanillaCampaignName;
            string campaignMapName = s.VanillaCampaignMapName ?? entry?.DisplayLocation ?? vanillaMapName;
            string battlePath = s.VanillaBattlePath ?? entry?.BattlePath ?? DecompileSettings.DeriveBattleMapName(vanillaMapName);
            string? devastateMapName = devastated ? entry?.TerrainLocationDevastate : null;
            if (devastated)
                log(devastateMapName == null
                    ? $"Warning: campaign '{s.VanillaCampaignName}' has no terrain_location_devastate, so it has no devastated map"
                    : $"Devastated map: terrain/campaigns/{devastateMapName} (its pieces are swapped into terrain/campaigns/{vanillaMapName})");

            // ---- extract what the selected steps need
            string extracted = Path.Combine(temp, "packs");
            string campaignPath = $"terrain/campaigns/{vanillaMapName}";
            string treeListPath = $"campaign_maps/{campaignMapName}/display/trees/trees.campaign_tree_list";
            string battlesPath = battlePath.Contains('/') ? battlePath.Trim('/') : $"terrain/battles/{battlePath}";
            string devastatePath = $"terrain/campaigns/{devastateMapName}";

            var wanted = new List<(string Path, bool IsFolder)>();
            if (needCampaign) wanted.Add((campaignPath, true));
            if (needTreeList) wanted.Add((treeListPath, false));
            if (needTileDb) wanted.Add((TileDatabaseRoot, true));
            if (needBattles) wanted.Add((battlesPath, true));
            if (needDevastate && devastateMapName != null) wanted.Add((devastatePath, true));
            if (wanted.Count > 0)
            {
                log($"Extracting from packs: {string.Join(", ", wanted.Select(w => w.Path))}");
                await packs.ExtractAsync(wanted, extracted, ct);
            }

            string? Local(bool needed, string packPath, bool isFolder, string? overridePath)
            {
                if (!needed)
                    return overridePath;
                string local = Path.Combine(extracted, packPath.Replace('/', Path.DirectorySeparatorChar));
                if (isFolder ? Directory.Exists(local) : File.Exists(local))
                    return local;
                log($"Warning: {packPath} was not found in the packs.");
                return null;
            }

            string? campaignFolder = Local(needCampaign, campaignPath, true, s.InputFolder);

            // Extracted on its own: a missing file would fail the whole batch above.
            string? blendTextures = s.BlendTexturesPath;
            if (needBlendTextures)
            {
                blendTextures = await packs.ExtractFileAsync(BlendPalette.PackPath, extracted, ct);
                if (blendTextures == null)
                    log($"Warning: {BlendPalette.PackPath} was not found in the packs.");
            }

            // The Weather environment copies post_processes from the map's own lighting, which
            // global_lighting names by pack path - one that need not be inside the map folder.
            // A cloned campaign's environment volumes also need it, for the snow material to clone.
            string? mapLighting = null;
            if ((steps.Contains(StepId.TerryProject) || steps.Contains(StepId.EnvironmentVolumes)) && campaignFolder != null
                && EnvironmentWriter.ReadGlobalLighting(campaignFolder) is { } lighting)
            {
                mapLighting = await packs.ExtractFileAsync(lighting, Path.Combine(temp, "lighting"), ct);
                if (mapLighting == null)
                    log($"Warning: global_lighting {lighting} was not found in the packs.");
            }

            return Build(s, entry, akRoot, temp, provinces, eventAreas, treePalette, cultures, new Inputs(
                campaignFolder,
                Local(needTreeList, treeListPath, false, s.TreeListPath),
                Local(needTileDb, TileDatabasePath, true, s.TileDatabaseFolder),
                Local(needBattles, battlesPath, true, s.BattlesFolder),
                blendTextures,
                mapLighting,
                Local(needDevastate && devastateMapName != null, devastatePath, true, null)), devastated, log, packs);
        }
        catch
        {
            packs?.Dispose();
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    private sealed record Inputs(string? CampaignFolder, string? TreeList, string? TileDb, string? Battles, string? BlendTextures,
        string? MapLighting, string? DevastateFolder);

    private static ResolvedSources Build(DecompileSettings s, CampaignEntry? entry, string? akRoot, string? temp,
        IReadOnlyDictionary<string, string>? provinces, EventAreaPalette? eventAreas, TreePalette? treePalette, CultureMask? cultures, Inputs? inputs,
        bool devastated, Action<string> log, PackFileSource? packs = null)
    {
        string vanillaMapName = s.VanillaMapName ?? entry?.TerrainLocation ?? s.VanillaCampaignName;
        string vanillaBattlePath = s.VanillaBattlePath ?? entry?.BattlePath ?? DecompileSettings.DeriveBattleMapName(vanillaMapName);
        string vanillaBattleName = Path.GetFileName(vanillaBattlePath.Replace('\\', '/').TrimEnd('/'));

        string mapName = s.MapName ?? vanillaMapName;
        string? devastateMapName = devastated ? entry?.TerrainLocationDevastate : null;
        string? devastatedMapName = devastateMapName == null ? null
            : s.DevastatedMapName ?? (s.MapName == null ? devastateMapName : DecompileSettings.DeriveDevastatedMapName(mapName));
        string campaignName = s.CampaignName ?? s.VanillaCampaignName;
        string battleMapName = s.BattleMapName
            ?? (s.MapName == null ? vanillaBattleName : DecompileSettings.DeriveBattleMapName(mapName));
        string environmentFile = $"default_battle_{campaignName}.environment";

        akRoot ??= s.AssemblyKitFolder;
        return new ResolvedSources
        {
            VanillaMapName = vanillaMapName,
            VanillaCampaignName = s.VanillaCampaignName,
            MapName = mapName,
            DevastateMapName = devastateMapName,
            DevastatedMapName = devastatedMapName,
            DevastatedOutputFolder = devastatedMapName == null ? null : OutputUnder(akRoot!, "campaigns", devastatedMapName),
            CampaignName = campaignName,
            BattleMapName = battleMapName,
            GlobalLighting = $"Weather/{environmentFile}",
            EnvironmentFileName = environmentFile,
            CampaignFolder = inputs?.CampaignFolder ?? s.InputFolder,
            DevastateFolder = inputs?.DevastateFolder,
            MapLightingPath = inputs?.MapLighting,
            TreeListPath = inputs?.TreeList ?? s.TreeListPath,
            TileDatabaseFolder = inputs?.TileDb ?? s.TileDatabaseFolder,
            BattlesFolder = inputs?.Battles ?? s.BattlesFolder,
            BlendTexturesPath = inputs?.BlendTextures ?? s.BlendTexturesPath,
            EventAreas = eventAreas,
            TreePalette = treePalette,
            Cultures = cultures,
            OutputFolder = OutputUnder(akRoot!, "campaigns", mapName),
            BattleOutputFolder = OutputUnder(akRoot!, "battles", battleMapName),
            AssemblyKitFolder = akRoot,
            DbWorldWidth = entry?.WorldWidth,
            RegionToProvince = provinces ?? new Dictionary<string, string>(),
            TempFolder = temp,
            Packs = packs,
        };
    }

    private static string OutputUnder(string akRoot, string kind, string name) =>
        Path.Combine(akRoot, "raw_data", "terrain", kind, name);
}
