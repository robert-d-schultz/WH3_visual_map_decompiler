using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TwMapDecompiler.Core.Bmd;
using TwMapDecompiler.Core.Formats.CompressedMap;
using TwMapDecompiler.Core.Formats.Dds;
using TwMapDecompiler.Core.Imaging;
using TwMapDecompiler.Core.IO;
using TwMapDecompiler.Core.Pieces;
using TwMapDecompiler.Core.Rivers;
using TwMapDecompiler.Core.Terry;
using TwMapDecompiler.Core.Tiles;
using TwMapDecompiler.Core.Trees;

namespace TwMapDecompiler.Core.Pipeline;

public enum StepId
{
    Layers,
    Height,
    HeightShroud,
    Blend,
    ColorOverlay,
    ColorOverlaySea,
    SnowMask,
    CorruptionMask,
    EventAreaMask,
    Trees,
    TileMap,
    NormalMap,
    EnvironmentVolumes,
    Catchment,
    TerryProject,
    BattleProject,
    DevastatedProject,
}

public sealed record StepInfo(StepId Id, string Name, string Description, bool Available, string? UnavailableReason, bool DefaultEnabled);

/// <summary>
/// Orchestrates the individual decompile steps: source map in (from the game's
/// packs via RPFM, or local overrides), two Terry projects out - the campaign
/// project and the battle terrain project CA keeps next to it.
/// </summary>
public sealed class Decompiler(DecompileSettings settings)
{
    public delegate void ProgressHandler(StepId step, double fraction);

    private readonly DecompileSettings _s = settings;
    private ResolvedSources _src = null!;

    /// <summary>Campaign Terry project folder of the last run (resolved defaults included).</summary>
    public string? OutputFolder { get; private set; }

    /// <summary>Battle Terry project folder of the last run.</summary>
    public string? BattleOutputFolder { get; private set; }

    /// <summary>
    /// A compiled campaign file. For the devastated project, the files with the devastate pieces
    /// pasted in (see <see cref="PrepareDevastation"/>) stand in for the vanilla ones.
    /// </summary>
    private string In(string relative) =>
        _devastated.TryGetValue(relative, out string? patched) ? patched : Path.Combine(_src.CampaignFolder!, relative);

    /// <summary>Devastated map: compiled file (relative, as <see cref="In"/> takes it) -> its devastated stand-in.</summary>
    private readonly Dictionary<string, string> _devastated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Devastated map: the vanilla map's pieces and the devastate map's.</summary>
    private PieceSet? _normalPieces, _devastatePieces;

    /// <summary>Devastated project: the main project's terrain maps, from its .terry.</summary>
    private List<TerryProjectWriter.TerrainMapRef>? _baseMaps;

    /// <summary>Devastated project: Terry map type -> how the terrain steps made it up (see <see cref="Record"/>).</summary>
    private readonly Dictionary<string, (DevastationMapMode Mode, string? LayerId)> _devastatedMaps = new(StringComparer.Ordinal);

    /// <summary>A compiled campaign file as the vanilla map has it, even for the devastated map.</summary>
    private string VanillaIn(string relative) => Path.Combine(_src.CampaignFolder!, relative);
    private string Out(string file) => Path.Combine(_src.OutputFolder, file);
    private string BattleOut(string file) => Path.Combine(_src.BattleOutputFolder, file);

    /// <summary>Compiled campaign files each step reads (relative to terrain/campaigns/&lt;map&gt;).</summary>
    private static readonly Dictionary<StepId, string[]> CampaignInputs = new()
    {
        [StepId.Layers] = ["global_props.bin"],
        [StepId.Height] = ["full_height_map.dds"],
        [StepId.HeightShroud] = ["shroud_heights.dds"],
        [StepId.Blend] = [Path.Combine("global_map", "global_blend.dds")],
        [StepId.ColorOverlay] = ["colour_overlay.dds"],
        [StepId.ColorOverlaySea] = ["lf_sea_colour.dds"],
        [StepId.SnowMask] = ["snow_mask.dds", "full_height_map.dds"], // the height map sizes the snow mask
        [StepId.CorruptionMask] = ["corruption_mask.dds"],
        [StepId.EventAreaMask] = ["event_area_mask.dds"],
        [StepId.Trees] = ["full_height_map.dds"], // sizes the tree map
        [StepId.TileMap] = ["tile_list.bin"],
        [StepId.NormalMap] = ["lf_normal.dds"],
        [StepId.EnvironmentVolumes] = ["environment_collection.xml"],
    };

    public static IReadOnlyList<StepInfo> DescribeSteps(DecompileSettings s)
    {
        var steps = new List<StepInfo>();

        void Add(StepId id, string name, string description, bool enabledByDefault = true)
        {
            // Inputs coming from the packs can only be checked once RPFM is queried at run
            // time; steps whose files turn out to be missing are skipped then.
            string? missing = MissingInput(id, s.InputFolder, s.TreeListPath, s.TileDatabaseFolder, s.BattlesFolder, s.BlendTexturesPath,
                checkPackInputs: false);
            steps.Add(new StepInfo(id, name, description, missing == null, missing, missing == null && enabledByDefault));
        }

        Add(StepId.Layers, "Prop layers",
            "global_props.bin + global_props_sound.bin -> per-province .layer files, sounds in a 'sound' logical layer (nested in 'uses_culture_mask' when culture-masked)" +
            "; river props become river splines again, fitted to their baked meshes");
        Add(StepId.Height, "Height map",
            "full_logic_map.compressed_map (land) + full_height_map.dds (BC6H, sea bed) -> .height + .sea_height 32-bit float TIFFs");
        Add(StepId.HeightShroud, "Shroud height map", "shroud_heights.dds -> 32-bit float .height_shroud TIFF");
        Add(StepId.Blend, "Blend map", "global_map/global_blend.dds -> indexed .blend TIFF");
        Add(StepId.ColorOverlay, "Colour overlay", "colour_overlay.dds -> RGBA .color_overlay TIFF");
        Add(StepId.ColorOverlaySea, "Sea colour overlay", "lf_sea_colour.dds -> RGBA .color_overlay_sea TIFF");
        Add(StepId.SnowMask, "Snow mask", "snow_mask.dds -> .snow_mask TIFF, shrunk back from BOB's stretch to a quarter of the height map");
        Add(StepId.CorruptionMask, "Corruption mask", "corruption_mask.dds -> .corruption_mask TIFF");
        Add(StepId.EventAreaMask, "Event area mask",
            "event_area_mask.dds + campaign_map_event_areas -> indexed .event_area_mask TIFF");
        Add(StepId.Trees, "Tree map", "trees.campaign_tree_list -> indexed .tree TIFF");
        Add(StepId.TileMap, "Tile map", "tile_list.bin + tile database -> tile_map.png + climate_map.png");
        Add(StepId.NormalMap, "Normal map", "lf_normal.dds (DXT5nm) -> RGB lf_normal.png");
        Add(StepId.EnvironmentVolumes, "Environment volumes",
            "environment_collection.xml -> <map>.<id>.layer of the lighting spheres and cylinders");
        Add(StepId.Catchment, "Catchment maps", "Battle blm_*.compressed_map -> PNGs in the battle project");
        Add(StepId.TerryProject, "Campaign Terry project",
            "Collect layers and TIFFs into <map>.terry with a blank PatchVisibilityMask sized to Terry's patch grid, " +
            "and write the working_data/Weather/<campaign>.environment it points at");
        Add(StepId.BattleProject, "Battle Terry project", "<battle map>.terry with placeholder height / tile / climate maps");
        Add(StepId.DevastatedProject, "Devastated project",
            "Needs the main project on disk. The campaign's devastated map (terrain_location_devastate) as its own project: the main " +
            "project's terrain maps hard-linked with the devastation as a layer on top, and one prop layer per event area", enabledByDefault: false);

        return steps;
    }

    /// <summary>
    /// Why a step cannot run, or null. Null input paths mean "from the packs": those
    /// only count as missing when <paramref name="checkPackInputs"/> (after extraction).
    /// </summary>
    private static string? MissingInput(StepId id, string? campaignFolder, string? treeList, string? tileDb, string? battles,
        string? blendTextures, bool checkPackInputs, EventAreaPalette? eventAreas = null, TreePalette? treePalette = null, CultureMask? cultures = null,
        string? devastateFolder = null)
    {
        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        if (CampaignInputs.TryGetValue(id, out var files))
        {
            if (campaignFolder == null)
            {
                if (checkPackInputs)
                    return "Campaign terrain folder not found in the packs";
            }
            else if (files.Select(f => Path.Combine(campaignFolder, f)).FirstOrDefault(p => !Exists(p)) is { } missing)
            {
                return $"Missing: {missing}";
            }
        }

        string? Check(string? path, string what) =>
            path == null ? (checkPackInputs ? $"{what} not found in the packs" : null)
            : Exists(path) ? null
            : $"Missing: {path}";

        return id switch
        {
            StepId.Trees when Check(treeList, "Tree list") is { } missing => missing,
            StepId.Trees when checkPackInputs && treePalette is not { Colors.Count: > 0 } =>
                $"{TreePalette.TableName} (tree colours) not found in the packs",
            StepId.TileMap => Check(tileDb, "Tile database"),
            StepId.Blend => Check(blendTextures, "Blend texture list (terrain_textures_campaign.assetdb.xml)"),
            StepId.Catchment => Check(battles, "Battle terrain folder"),
            StepId.EventAreaMask when checkPackInputs && eventAreas is not { Areas.Count: > 0 } =>
                "campaign_map_event_areas table not found in the packs",
            StepId.Layers when checkPackInputs && cultures == null =>
                $"{CultureMask.TableName} (culture mask bits) not found in the packs",
            StepId.DevastatedProject when checkPackInputs && devastateFolder == null =>
                "The campaign has no devastated map (campaigns.terrain_location_devastate), or it is not in the packs",
            _ => null,
        };
    }

    public void Run(IReadOnlyCollection<StepId> steps, Action<string> log, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        using var sources = SourceResolver.Resolve(_s, steps, log, ct);
        _src = sources;
        OutputFolder = sources.OutputFolder;
        BattleOutputFolder = sources.BattleOutputFolder;
        log($"Campaign project: {sources.OutputFolder}");
        log($"Battle project:   {sources.BattleOutputFolder}");
        _resizer = CreateResizer(steps, log);
        var ids = new TerryIdGenerator(_s.RandomSeed);

        // Up front rather than after the other steps: without the main project there is nothing to build on.
        if (steps.Contains(StepId.DevastatedProject) && !steps.Contains(StepId.TerryProject))
            RequireMainProject();

        // The project steps must run last: they scan the output of the other steps. The devastated
        // project comes after them all, since it is built on the main project.
        StepId[] last = [StepId.TerryProject, StepId.BattleProject, StepId.DevastatedProject];
        var ordered = steps.Where(s => !last.Contains(s)).Concat(last.Where(steps.Contains)).ToList();

        foreach (var step in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var started = DateTime.Now;
            log($"--- {step} ---");
            void StepProgress(double f) => progress?.Invoke(step, f);

            if (MissingInput(step) is { } reason)
            {
                log($"    skipped: {reason}");
                progress?.Invoke(step, 1.0);
                continue;
            }

            if (step != StepId.DevastatedProject)
                Directory.CreateDirectory(step is StepId.Catchment or StepId.BattleProject ? sources.BattleOutputFolder : sources.OutputFolder);
            RunStep(step, log, ids, StepProgress, ct);

            progress?.Invoke(step, 1.0);
            log($"    done in {(DateTime.Now - started).TotalSeconds:0.0}s");
        }
    }

    /// <summary>Why a step cannot run with this run's sources, or null.</summary>
    private string? MissingInput(StepId step) =>
        MissingInput(step, _src.CampaignFolder, _src.TreeListPath, _src.TileDatabaseFolder, _src.BattlesFolder,
            _src.BlendTexturesPath, checkPackInputs: true, _src.EventAreas, _src.TreePalette, _src.Cultures, _src.DevastateFolder);

    // ---------------------------------------------------------------- map scale and padding

    private MapResizer _resizer = MapResizer.Identity;

    /// <summary>
    /// The run's <see cref="MapResizer"/>. Scale and padding are both counted on the campaign hex
    /// grid, whose size tile_list.bin's header holds (the vanilla map's: the devastate pieces carry none).
    /// </summary>
    private MapResizer CreateResizer(IReadOnlyCollection<StepId> steps, Action<string> log)
    {
        if ((_s.Padding.IsNone && _s.Scale == 1) || steps.All(s => s == StepId.Catchment))
            return MapResizer.Identity;

        string? tileList = _src.CampaignFolder == null ? null : VanillaIn("tile_list.bin");
        if (tileList == null || !File.Exists(tileList))
            throw new InvalidOperationException(
                "Map scale and padding are counted on the campaign hex grid, whose size is read from the compiled map's " +
                $"tile_list.bin, and there is none{(tileList == null ? "" : $" at {tileList}")}.");
        var list = TileMapRenderer.Read(tileList);
        var source = new HexGrid(list.HexWidth, list.HexHeight);
        var scale = MapScale.Plan(source, _s.Scale);
        var resizer = new MapResizer(scale, _s.Padding);
        if (resizer.Final.Problem is { } problem)
            throw new InvalidOperationException($"The resized hex map would be {resizer.Final}, and {problem}.");

        var tile = resizer.Size(list.Width, list.Height);
        log($"Hex map {source} -> {resizer.Final} (tile map {list.Width}x{list.Height} -> {tile.Width}x{tile.Height})");
        if (resizer.IsScaled)
        {
            log($"Map scale: {scale.Effective:0.######} (asked for {scale.Requested:0.######}{(scale.Capped ? $", capped to fit {HexGrid.MaxSize} hexes" : "")}): " +
                $"hex map {scale.Scaled}");
            log($"    the scaled map is {Math.Abs(scale.NorthResidual):0.###} hexes {(scale.NorthResidual >= 0 ? "short of" : "past")} the grid's north edge, " +
                $"which {(scale.NorthResidual >= 0 ? "repeats the map's edge" : "is cropped")}");
            log("    positions, heights and sizes in the world scale with it; hexes and texels keep their world size");
        }
        if (!_s.Padding.IsNone)
        {
            log($"Map padding: {_s.Padding} hexes");
            if (_s.Padding.Left > 0 || _s.Padding.Bottom > 0)
                log("    padding the left or bottom moves the world's origin: every prop and environment volume moves with the map");
            log("    the sea water planes and full-shroud meshes do not grow over the padding: extend them over the new ground in Terry");
        }
        return resizer;
    }

    /// <summary>
    /// Scales and pads a map by the run's resize (pixels off the source map repeat its edge, or are
    /// <paramref name="fill"/>), logging the new size.
    /// </summary>
    private (T[] Data, int Width, int Height) Resize<T>(T[] data, int width, int height, Sampling sampling, Action<string> log,
        int channels = 1, T[]? fill = null)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        if (_resizer.IsNone)
            return (data, width, height);
        var resized = _resizer.Resize(data, width, height, sampling, channels, fill);
        log($"    resized {width}x{height} -> {resized.Width}x{resized.Height}");
        return resized;
    }

    /// <summary>Heights scale with the world (in place).</summary>
    private void ScaleHeights(float[] heights)
    {
        if (!_resizer.IsScaled)
            return;
        float factor = (float)_resizer.Factor;
        Parallel.For(0, (heights.Length + 65535) / 65536, block =>
        {
            int end = Math.Min(heights.Length, (block + 1) * 65536);
            for (int i = block * 65536; i < end; i++)
                heights[i] *= factor;
        });
    }

    /// <summary>World width of the resized map, for the .terry.</summary>
    private double ResizedWorldWidth(Action<string> log)
    {
        double worldWidth = ResolveWorldWidth(log);
        if (_resizer.IsNone)
            return worldWidth;
        double resized = _resizer.WorldWidth(worldWidth);
        log($"world_width {worldWidth:0.####} resized to {resized:0.####}");
        return resized;
    }

    /// <summary>How the world's positions move: scaled about the origin, then offset by the padding on the left and bottom.</summary>
    private (double Scale, double X, double Z) WorldTransform(Action<string> log)
    {
        if (_resizer.IsNone)
            return (1, 0, 0);
        double worldWidth = ResolveWorldWidth(log);
        var transform = (_resizer.Factor, _resizer.OffsetX(worldWidth), _resizer.OffsetZ(worldWidth));
        log($"Every position is scaled by {transform.Item1:0.######} and moved by x +{transform.Item2:0.####}, z +{transform.Item3:0.####}" +
            (_resizer.IsScaled ? "; sizes (prop scale, radii, lengths, outlines) scale with it" : ""));
        return transform;
    }

    private void RunStep(StepId step, Action<string> log, TerryIdGenerator ids, Action<double> progress, CancellationToken ct)
    {
        switch (step)
        {
            case StepId.Layers when _src.IsDevastated: RunPieceLayers(log, ids, ct); break;
            case StepId.Layers: RunLayers(log, ids, ct); break;
            case StepId.Height: RunHeight(log, ids, progress); break;
            case StepId.HeightShroud: RunHeightShroud(log, ids, progress); break;
            case StepId.Blend: RunBlend(log, ids, progress); break;
            case StepId.ColorOverlay: RunColorOverlay(log, ids, "colour_overlay.dds", "color_overlay", progress); break;
            case StepId.ColorOverlaySea: RunColorOverlay(log, ids, "lf_sea_colour.dds", "color_overlay_sea", progress); break;
            case StepId.SnowMask: RunGrayMask(log, ids, "snow_mask.dds", "snow_mask", progress); break;
            case StepId.CorruptionMask: RunGrayMask(log, ids, "corruption_mask.dds", "corruption_mask", progress); break;
            case StepId.EventAreaMask: RunEventAreaMask(log, ids, progress); break;
            case StepId.Trees: RunTrees(log, ids); break;
            case StepId.TileMap: RunTileMap(log); break;
            case StepId.NormalMap: RunNormalMap(log, progress); break;
            case StepId.EnvironmentVolumes: RunEnvironmentVolumes(log, ids, ct); break;
            case StepId.Catchment: RunCatchment(log, ct); break;
            case StepId.TerryProject: RunTerryProject(log, ids, ct); break;
            case StepId.BattleProject: RunBattleProject(log, ids); break;
            case StepId.DevastatedProject: RunDevastatedProject(log, ids, progress, ct); break;
        }
    }

    // ---------------------------------------------------------------- layers

    private void RunLayers(Action<string> log, TerryIdGenerator ids, CancellationToken ct)
    {
        // Older maps have no separate sound file; its emitters are then in global_props.bin.
        string? soundPath = In("global_props_sound.bin");
        if (!File.Exists(soundPath))
        {
            log("    no global_props_sound.bin: layers get only the sound emitters in global_props.bin");
            soundPath = null;
        }

        var result = GlobalPropsParser.Parse(In("global_props.bin"), _src.VanillaMapName, _src.Cultures!, _src.RegionToProvince, log, soundPath);
        ct.ThrowIfCancellationRequested();

        var world = WorldTransform(log);
        var rivers = ReadRivers(log);
        var writer = new TerryLayerWriter(new TerryLayerWriter.Options
        {
            MapName = _src.MapName,
            VanillaMapName = _src.VanillaMapName,
            CampaignName = _src.CampaignName,
            VanillaCampaignName = _src.VanillaCampaignName,
            Cultures = _s.AddCustomCultures ? _src.Cultures!.WithCustomCultures() : _src.Cultures!,
            SeaWaterPlaneMaterial = IsClonedCampaign ? CloneSeaWaterMaterial(result, log, ct) : null,
            Scale = world.Scale,
            OffsetX = world.X,
            OffsetZ = world.Z,
            Rivers = rivers,
        }, ids);
        var layers = writer.WriteLayers(result.Layers, _src.OutputFolder, log);
        log($"{layers.Count} layers, {layers.Sum(l => l.EntityCount)} entities total");
        LogRivers(rivers, log);
    }

    /// <summary>An overridden map name: paths into the source map's terrain folder no longer resolve.</summary>
    private bool IsRenamedMap => !string.Equals(_src.MapName, SourceMapName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The terrain folder the layers come from: the devastate map's for the devastated map.</summary>
    private string SourceMapName => _src.DevastateMapName ?? _src.VanillaMapName;

    /// <summary>
    /// Reads river props back into Terry river splines, from the baked meshes in the models/ folders
    /// of the maps the props point into: the vanilla map, and for the devastated map the devastate map.
    /// </summary>
    private RiverModels ReadRivers(Action<string> log)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_src.CampaignFolder != null)
            folders[_src.VanillaMapName] = _src.CampaignFolder;
        if (_src.DevastateMapName != null && _src.DevastateFolder != null)
            folders[_src.DevastateMapName] = _src.DevastateFolder;
        return new RiverModels(folders, log);
    }

    private void LogRivers(RiverModels rivers, Action<string> log)
    {
        if (rivers.Converted > 0)
            log($"{rivers.Converted} rivers written as river splines ({rivers.PointCount} points), fitted to their baked meshes");
        if (rivers.Failed == 0)
            return;
        log($"{rivers.Failed} rivers stay props pointing at their baked meshes (see the warnings above)");
        if (IsRenamedMap)
            CopyRiverModels(log);
    }

    /// <summary>
    /// The layers reference the baked meshes of the rivers that stay props under the new map's terrain
    /// folder. Under the vanilla name the game and Terry resolve them straight out of the packs; under a
    /// new name nothing would, so a renamed map gets them copied to the Assembly Kit's working_data,
    /// where Terry looks before the packs.
    /// </summary>
    private void CopyRiverModels(Action<string> log)
    {
        string source = _src.IsDevastated ? Path.Combine(_src.DevastateFolder!, "models") : In("models");
        if (!Directory.Exists(source))
        {
            log("Renamed map: no models/ folder in the source map, so no river meshes to copy");
            return;
        }
        if (_src.AssemblyKitFolder == null)
        {
            log("Renamed map: river meshes not copied: no Assembly Kit folder resolved (set it, or the output folders, explicitly)");
            return;
        }

        string target = Path.Combine(_src.AssemblyKitFolder, "working_data", "terrain", "campaigns", _src.MapName, "models");
        Directory.CreateDirectory(target);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        log($"Renamed map: copied {copied} river model files to {target}");
        log($"Note: pack these under terrain/campaigns/{_src.MapName}/models/ in your mod.");
    }

    // ------------------------------------------------------ devastated map

    private static readonly PieceSet EmptyPieces = new() { MapFolder = "", Pieces = [], EventTiles = [], EventTrees = [] };

    /// <summary>Compiled textures the pieces carry: path relative to the map folder, name in texture_info.</summary>
    private static readonly (string Relative, string Texture)[] PieceTextures =
    [
        ("full_height_map.dds", "full_height_map.dds"),
        ("lf_normal.dds", "lf_normal.dds"),
        ("shroud_heights.dds", "shroud_heights.dds"),
        (Path.Combine("global_map", "global_blend.dds"), "global_blend.dds"),
        ("colour_overlay.dds", "colour_overlay.dds"),
        ("lf_sea_colour.dds", "lf_sea_colour.dds"),
        ("snow_mask.dds", "snow_mask.dds"),
        ("corruption_mask.dds", "corruption_mask.dds"),
    ];

    /// <summary>What the Devastated project step runs for the devastated project, in order, before writing its .terry.</summary>
    public static readonly StepId[] DevastatedSteps =
    [
        StepId.Layers, StepId.Height, StepId.HeightShroud, StepId.Blend, StepId.ColorOverlay, StepId.ColorOverlaySea,
        StepId.SnowMask, StepId.CorruptionMask, StepId.EventAreaMask, StepId.Trees,
        StepId.TileMap, StepId.NormalMap, StepId.EnvironmentVolumes,
    ];

    /// <summary>
    /// The campaign's devastated map as a project of its own, built on the main project already on
    /// disk (its .terry and terrain map TIFFs): the vanilla map with every event area swapped for the
    /// devastate map's piece of it. Each terrain map is the main project's layers, hard-linked, with a
    /// devastation layer on top (see <see cref="DevastationLayers"/>); the props are one layer per
    /// event area. All of it is written in one go, since the stacks and the .terry go together.
    /// </summary>
    private void RunDevastatedProject(Action<string> log, TerryIdGenerator ids, Action<double> progress, CancellationToken ct)
    {
        string mainTerry = RequireMainProject();
        var main = _src;
        _src = main.ForDevastated();
        try
        {
            log($"Devastated project: {_src.OutputFolder}, built on {mainTerry}");
            Directory.CreateDirectory(_src.OutputFolder);
            PrepareDevastation(log, ct);

            int parts = DevastatedSteps.Length + 1;
            for (int i = 0; i < DevastatedSteps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = DevastatedSteps[i];
                log($"--- Devastated project: {step} ---");
                if (MissingInput(step) is { } reason)
                {
                    log($"    skipped: {reason}");
                    continue;
                }
                double from = (double)i / parts;
                RunStep(step, log, ids, f => progress(from + f / parts), ct);
            }
            log("--- Devastated project: TerryProject ---");
            RunTerryProject(log, ids, ct);
        }
        finally
        {
            _src = main;
            _devastated.Clear();
            _devastatedMaps.Clear();
            _normalPieces = _devastatePieces = null;
            _baseMaps = null;
        }
    }

    /// <summary>The main project's .terry, which the devastated project is built on; an error when there is none.</summary>
    private string RequireMainProject()
    {
        string mainTerry = Out($"{_src.MapName}.terry");
        if (!File.Exists(mainTerry))
            throw new InvalidOperationException(
                $"The devastated project is built on the main project, and there is none at {mainTerry}. " +
                "Decompile the main map first (its terrain maps and the Campaign Terry project step), then run the Devastated project step.");
        return mainTerry;
    }

    /// <summary>
    /// Pastes the devastate map's pieces (terrain/campaigns/&lt;terrain_location_devastate&gt;/pieces)
    /// into copies of the vanilla textures, which then stand in for the vanilla ones; the trees,
    /// roads and layers are swapped in their own steps. The event area mask has no pieces and stays
    /// vanilla; the patch visibility mask is blank, as in the main project.
    /// </summary>
    private void PrepareDevastation(Action<string> log, CancellationToken ct)
    {
        if (_src.DevastateFolder == null || !Directory.Exists(_src.DevastateFolder))
            throw new InvalidOperationException($"terrain/campaigns/{_src.DevastateMapName} was not found in the packs.");
        _devastatePieces = PieceSet.Load(_src.DevastateFolder)
            ?? throw new InvalidOperationException($"terrain/campaigns/{_src.DevastateMapName} has no pieces folder.");
        log($"Devastated map: {_devastatePieces.Pieces.Count} pieces in terrain/campaigns/{_src.DevastateMapName}");
        LoadBaseProject(log);

        if (_src.CampaignFolder != null)
        {
            _normalPieces = PieceSet.Load(_src.CampaignFolder);
            log(_normalPieces == null
                ? $"Warning: terrain/campaigns/{_src.VanillaMapName} has no pieces; the trees and roads the areas held cannot all be told apart"
                : $"Vanilla map: {_normalPieces.Pieces.Count} pieces in terrain/campaigns/{_src.VanillaMapName}");
        }

        // Its own environment_collection.xml (no devastation lighting of its own, unlike the vanilla map's).
        string environment = Path.Combine(_src.DevastateFolder, "environment_collection.xml");
        if (File.Exists(environment))
            _devastated["environment_collection.xml"] = environment;

        if (_src.CampaignFolder == null)
            return;
        string patchedFolder = Path.Combine(_src.TempFolder ?? Path.GetTempPath(), "devastated");
        foreach (var (relative, texture) in PieceTextures)
        {
            ct.ThrowIfCancellationRequested();
            string source = Path.Combine(_src.CampaignFolder, relative);
            if (!File.Exists(source))
                continue;
            string target = Path.Combine(patchedFolder, relative);
            DdsPiecePatcher.Patch(source, target, texture, _devastatePieces.Pieces, log);
            _devastated[relative] = target;
        }
    }

    private string BaseTerryPath => Path.Combine(_src.BaseOutputFolder!, $"{_src.BaseMapName}.terry");

    private string BaseFile(string suffix, string layerId) =>
        Path.Combine(_src.BaseOutputFolder!, $"{_src.BaseMapName}.{suffix}.{layerId}.tif");

    private static string TypeOf(string suffix) => SuffixToType.First(s => s.Suffix == suffix).Type;

    private void LoadBaseProject(Action<string> log)
    {
        if (string.Equals(Path.GetFullPath(_src.BaseOutputFolder!).TrimEnd('\\', '/'), Path.GetFullPath(_src.OutputFolder).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The devastated project would be written into its own main project ({_src.OutputFolder}). Give one of them another name.");

        _baseMaps = TerryProjectReader.ReadTerrainMaps(BaseTerryPath);
        log($"Devastated project: its terrain maps are layered over those of {BaseTerryPath}");
    }

    /// <summary>
    /// The main project's map the devastation is layered over: listed in its .terry, the same size,
    /// and with every layer file present. Null otherwise, and the devastated project goes without
    /// that map (the log says why): it is never written whole just because the main one is missing.
    /// </summary>
    private TerryProjectWriter.TerrainMapRef? BaseMap(string suffix, int width, int height, Action<string> log)
    {
        string type = TypeOf(suffix);
        (width, height) = _resizer.Size(width, height);
        var map = _baseMaps!.FirstOrDefault(m => m.Type == type);
        string? problem =
            map == null || map.Layers.Count == 0 ? "the main project has no such map"
            : map.Width != width || map.Height != height ? $"the main project's is {map.Width}x{map.Height}, this one {width}x{height}"
            : map.Layers.Select(l => BaseFile(suffix, l.Id)).FirstOrDefault(p => !File.Exists(p)) is { } missing ? $"{missing} is missing"
            : null;
        if (problem == null)
            return map;
        log($"Warning: {type} left out of the devastated project: {problem}. Run that map's step for the main project first.");
        return null;
    }

    /// <summary>Devastated project: notes what a terrain step wrote, for the .terry.</summary>
    private void Record(string suffix, DevastationMapMode mode, string? layerId)
    {
        if (_src.IsDevastated)
            _devastatedMaps[TypeOf(suffix)] = (mode, layerId);
    }

    /// <summary>
    /// Devastated map: writes a map's devastation layer, or nothing when the pieces leave the map
    /// unchanged. False when the layer cannot express the change (<paramref name="cannot"/> says
    /// why) and the caller writes the whole devastated map instead, over the main project's layers
    /// as hidden references.
    /// </summary>
    private bool TryWriteDevastation(TerryIdGenerator ids, string suffix, string? cannot, long changed, Action<string> write, Action<string> log)
    {
        string type = TypeOf(suffix);
        if (cannot != null)
        {
            log($"{type}: written whole, the main project's layers kept under it as hidden references: {cannot}");
            return false;
        }
        if (changed == 0)
        {
            log($"{type}: the pieces leave it unchanged, so the project gets the main project's layers only");
            Record(suffix, DevastationMapMode.Base, null);
            return true;
        }
        string id = ids.Next();
        string path = Out($"{_src.MapName}.{suffix}.{id}.tif");
        write(path);
        log($"Wrote {Path.GetFileName(path)}: the devastation layer over the main project's {type} ({changed:N0} pixels changed)");
        Record(suffix, DevastationMapMode.Overlay, id);
        return true;
    }

    private static bool FilesEqual(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length)
            return false;
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        int n;
        while ((n = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false)) > 0)
        {
            fb.ReadExactly(bb, 0, n);
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The devastated map's layers: one per event area, holding its devastate piece's objects.bin and
    /// bmd_objects_sound.bin, with each devastation type's objects_&lt;type&gt;.bin and
    /// bmd_objects_sound_&lt;type&gt;.bin in a logical layer whose bmd_export_type is that type - which is
    /// how Terry marks what goes into those BMDs when the pieces are processed. A type's sound emitters
    /// sit in a sound logical layer nested inside the type's. Every .bin's culture
    /// masks come from the .culture file beside it. Only what the pieces hold is written: outside the
    /// event areas the game keeps the vanilla map's props. The one exception is the map-wide
    /// global_props_sound_&lt;type&gt;.bin files: the few sound emitters they hold that no piece does
    /// (placed outside every event area) go into a _global layer, see <see cref="WriteUnpiecedSounds"/>.
    /// </summary>
    private void RunPieceLayers(Action<string> log, TerryIdGenerator ids, CancellationToken ct)
    {
        var pieces = _devastatePieces!;
        var areaNames = (_src.EventAreas?.Areas ?? [])
            .GroupBy(a => $"{a.Color.R:x2}{a.Color.G:x2}{a.Color.B:x2}")
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        if (areaNames.Count == 0)
            log("Warning: campaign_map_event_areas was not read; layers are named after their piece folders");

        var cultures = _s.AddCustomCultures ? _src.Cultures!.WithCustomCultures() : _src.Cultures!;
        var world = WorldTransform(log);
        var rivers = ReadRivers(log);
        var writer = new TerryLayerWriter(new TerryLayerWriter.Options
        {
            MapName = _src.MapName,
            VanillaMapName = SourceMapName,
            CampaignName = _src.CampaignName,
            VanillaCampaignName = _src.VanillaCampaignName,
            Cultures = cultures,
            Scale = world.Scale,
            OffsetX = world.X,
            OffsetZ = world.Z,
            Rivers = rivers,
        }, ids);

        int entities = 0;
        var holes = new HoleCounts();
        var exportTotals = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var pieceSounds = new Dictionary<string, List<BmdSoundEmitter>>(StringComparer.Ordinal);
        foreach (var piece in pieces.Pieces)
        {
            ct.ThrowIfCancellationRequested();
            if (!areaNames.TryGetValue(piece.ColourHex, out string? layerName))
            {
                layerName = Path.GetFileName(piece.Folder);
                if (areaNames.Count > 0)
                    log($"Warning: no event area has colour {piece.ColourHex}; its layer is named {layerName}");
            }

            var baseObjects = new CompiledBmd(layerName);
            var groups = new SortedDictionary<string, CompiledBmd>(StringComparer.Ordinal);
            foreach (string bin in Directory.EnumerateFiles(piece.Folder, "*.bin").Order(StringComparer.OrdinalIgnoreCase))
            {
                // objects.bin, bmd_objects_sound.bin, objects_<type>.bin, bmd_objects_sound_<type>.bin
                string name = Path.GetFileNameWithoutExtension(bin);
                string? exportType = name switch
                {
                    "objects" or "bmd_objects_sound" => null,
                    _ when name.StartsWith("bmd_objects_sound_", StringComparison.Ordinal) => name["bmd_objects_sound_".Length..],
                    _ when name.StartsWith("objects_", StringComparison.Ordinal) => name["objects_".Length..],
                    _ => "",
                };
                if (exportType == "")
                {
                    log($"Warning: {Path.GetFileName(piece.Folder)}/{Path.GetFileName(bin)} is not a piece BMD; skipped");
                    continue;
                }

                var bmd = ParsePieceBmd(bin, layerName, holes, log);
                string soundKey = exportType ?? "";
                if (!pieceSounds.TryGetValue(soundKey, out var sounds))
                    pieceSounds[soundKey] = sounds = [];
                sounds.AddRange(bmd.SoundEmitters);
                if (exportType == null)
                {
                    baseObjects.Add(bmd);
                }
                else
                {
                    if (!groups.TryGetValue(exportType, out var group))
                        groups[exportType] = group = new CompiledBmd(layerName);
                    group.Add(bmd);
                }
            }

            var written = writer.WriteLayer(baseObjects, groups.Select(g => (g.Key, g.Value)).ToList(), _src.OutputFolder, log);
            entities += written.EntityCount;
            foreach (var (type, group) in groups)
                exportTotals[type] = exportTotals.GetValueOrDefault(type) + group.EntityCount;
        }

        entities += WriteUnpiecedSounds(writer, pieceSounds, log);

        log($"{pieces.Pieces.Count} event area layers, {entities} entities total" +
            (exportTotals.Count > 0 ? $" ({string.Join(", ", exportTotals.Select(t => $"{t.Value} {t.Key}"))})" : ""));
        int holeTotal = holes.FromCulture + holes.FromVanilla + holes.Unmatched;
        if (holeTotal > 0)
            log($"Terrain hole triangles: {holeTotal}, culture masks from the .culture files for {holes.FromCulture}, " +
                $"from their vanilla twins for {holes.FromVanilla}, and for the {holes.Unmatched} with no vanilla twin " +
                "the OR of their file's hole masks (see PieceCulture.HoleMasks)");
        LogRivers(rivers, log);
    }

    /// <summary>
    /// The devastate map also ships map-wide global_props_sound[_&lt;type&gt;].bin files. Nearly every
    /// emitter in them is one a piece already holds; the rest (5 on the combi map) lie outside every
    /// event area, so no piece carries them. Those go into a _global layer, each in its export type's
    /// logical layer as in the pieces' layers. Returns how many were written.
    /// </summary>
    private int WriteUnpiecedSounds(TerryLayerWriter writer, Dictionary<string, List<BmdSoundEmitter>> pieceSounds, Action<string> log)
    {
        const string prefix = "global_props_sound";
        var baseObjects = new CompiledBmd(GlobalPropsParser.GlobalLayerName) { IsGlobal = true };
        var groups = new List<(string ExportType, CompiledBmd Objects)>();
        foreach (string file in Directory.EnumerateFiles(_src.DevastateFolder!, $"{prefix}*.bin").Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string? exportType = name == prefix ? null
                : name.StartsWith(prefix + "_", StringComparison.Ordinal) ? name[(prefix.Length + 1)..]
                : "";
            if (exportType == "")
            {
                log($"Warning: {Path.GetFileName(file)} is not a sound BMD container; skipped");
                continue;
            }

            var all = GlobalPropsParser.ParseSoundTiles(file, GlobalPropsParser.GlobalLayerName, _src.Cultures!, log);
            var inPieces = pieceSounds.GetValueOrDefault(exportType ?? "", []).ToLookup(e => e.SoundName, StringComparer.Ordinal);
            var used = new HashSet<BmdSoundEmitter>(ReferenceEqualityComparer.Instance);
            var unpieced = new CompiledBmd(GlobalPropsParser.GlobalLayerName);
            foreach (var emitter in all.SoundEmitters)
            {
                var twin = inPieces[emitter.SoundName].FirstOrDefault(p => !used.Contains(p) && SamePosition(p.Transform, emitter.Transform));
                if (twin != null)
                    used.Add(twin);
                else
                    unpieced.SoundEmitters.Add(emitter);
            }

            log($"{Path.GetFileName(file)}: {all.SoundEmitters.Count} sound emitters, {unpieced.SoundEmitters.Count} of them in no piece" +
                (unpieced.SoundEmitters.Count > 0
                    ? $" ({string.Join(", ", unpieced.SoundEmitters.Select(e => $"{e.SoundName} at {e.Transform.PosX:0.#},{e.Transform.PosZ:0.#}"))})"
                    : ""));
            if (unpieced.SoundEmitters.Count == 0)
                continue;
            if (exportType == null)
                baseObjects.Add(unpieced);
            else
                groups.Add((exportType, unpieced));
        }

        if (baseObjects.EntityCount == 0 && groups.Count == 0)
            return 0;
        return writer.WriteLayer(baseObjects, groups, _src.OutputFolder, log).EntityCount;

        // The piece and map-wide copies of an emitter are compiled separately; allow for float noise.
        static bool SamePosition(BmdTransform a, BmdTransform b) =>
            Math.Abs(a.PosX - b.PosX) < 0.01 && Math.Abs(a.PosY - b.PosY) < 0.01 && Math.Abs(a.PosZ - b.PosZ) < 0.01;
    }

    private CompiledBmd ParsePieceBmd(string binPath, string layerName, HoleCounts holes, Action<string> log)
    {
        string source = $"{Path.GetFileName(Path.GetDirectoryName(binPath))}/{Path.GetFileName(binPath)}";
        var bmd = GlobalPropsParser.ParsePiece(binPath, layerName, _src.Cultures!, log);
        string culturePath = Path.ChangeExtension(binPath, ".culture");
        var sections = File.Exists(culturePath) ? PieceCulture.Read(culturePath) : new Dictionary<string, ulong[]>();
        if (PieceCulture.Apply(bmd, sections, source, log))
        {
            holes.FromCulture += bmd.TerrainHoleTris.Count;
        }
        else
        {
            int unmatched = VanillaHoleMasks(log).Apply(bmd.TerrainHoleTris, PieceCulture.HoleMasks.Combined(sections));
            holes.FromVanilla += bmd.TerrainHoleTris.Count - unmatched;
            holes.Unmatched += unmatched;
        }
        bmd.CombineDuplicates();
        return bmd;
    }

    private sealed class HoleCounts
    {
        public int FromCulture, FromVanilla, Unmatched;
    }

    private PieceCulture.HoleMasks? _vanillaHoleMasks;

    /// <summary>The vanilla map's terrain hole triangles and their masks, read from global_props.bin once.</summary>
    private PieceCulture.HoleMasks VanillaHoleMasks(Action<string> log)
    {
        if (_vanillaHoleMasks != null)
            return _vanillaHoleMasks;
        string globalProps = Path.Combine(_src.CampaignFolder ?? "", "global_props.bin");
        if (_src.CampaignFolder == null || !File.Exists(globalProps))
        {
            log("Warning: no vanilla global_props.bin to take terrain hole culture masks from");
            return _vanillaHoleMasks = new PieceCulture.HoleMasks([]);
        }
        log("Terrain hole culture masks: reading the vanilla global_props.bin (a piece's .culture has one mask per hole, " +
            "its .bin one entry per triangle, so they cannot be paired up)");
        var vanilla = GlobalPropsParser.Parse(globalProps, _src.VanillaMapName, _src.Cultures!, log: _ => { });
        return _vanillaHoleMasks = new PieceCulture.HoleMasks(vanilla.Layers.SelectMany(l => l.TerrainHoleTris));
    }

    // ------------------------------------------------------ cloned campaign

    /// <summary>
    /// An overridden campaign name means a new campaign cloned from the source: it gets its own
    /// sea material, snow material and lighting in working_data rather than sharing vanilla's.
    /// </summary>
    private bool IsClonedCampaign => !string.Equals(_src.CampaignName, _src.VanillaCampaignName, StringComparison.OrdinalIgnoreCase);

    private string WorkingData(string packPath) =>
        Path.Combine(_src.AssemblyKitFolder!, "working_data", packPath.Replace('/', Path.DirectorySeparatorChar));

    private string CloneScratch => Path.Combine(_src.TempFolder!, "clone");

    /// <summary>Why the cloned campaign's files cannot be written, or null.</summary>
    private string? CloneUnavailable() =>
        _src.AssemblyKitFolder == null ? "no Assembly Kit folder resolved"
        : _src.Packs == null ? "the packs were not read (the materials come from them)"
        : null;

    /// <summary>
    /// Copies the material the sea water planes use to the cloned campaign's own
    /// materials/environment/campaign_sea/&lt;campaign&gt;_campaign_water_plane.xml.material -
    /// the path the .terry's water plane material already names - and returns that path.
    /// </summary>
    private string? CloneSeaWaterMaterial(GlobalPropsParser.Result result, Action<string> log, CancellationToken ct)
    {
        var materials = result.Layers.SelectMany(l => l.PolyMeshes).Where(m => m.IsSeaWaterPlane)
            .GroupBy(m => m.Material.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ToList();
        if (materials.Count == 0)
        {
            log("Cloned campaign: no sea water planes, so no sea material to clone");
            return null;
        }
        if (materials.Count > 1)
            log($"Warning: the sea water planes use {materials.Count} materials " +
                $"({string.Join(", ", materials.Select(g => $"{g.Key} x{g.Count()}"))}); cloning the most used one");

        string source = materials[0].Key;
        if (CloneUnavailable() is { } reason)
        {
            log($"Cloned campaign: sea material {source} not cloned: {reason}");
            return null;
        }

        string? local = _src.Packs!.ExtractFileAsync(source, CloneScratch, ct).GetAwaiter().GetResult();
        if (local == null)
        {
            log($"Cloned campaign: sea material {source} was not found in the packs");
            return null;
        }

        string target = $"materials/environment/campaign_sea/{_src.CampaignName}_campaign_water_plane.xml.material";
        Directory.CreateDirectory(Path.GetDirectoryName(WorkingData(target))!);
        File.Copy(local, WorkingData(target), overwrite: true);
        log($"Cloned campaign: sea material {source} -> {WorkingData(target)} ({materials[0].Count()} sea water planes use it)");
        return target;
    }

    private bool _snowCloneAttempted;
    private string? _clonedSnowMaterial;

    /// <summary>
    /// Clones the snow material the map's global lighting names in its post_processes, with
    /// t_xml_snow_mask pointed at the new map's snow_mask.dds; returns the clone's pack path.
    /// Done once per run - the environment volumes and the Weather environment both need it.
    /// </summary>
    private string? CloneSnowMaterial(Action<string> log, CancellationToken ct)
    {
        if (_snowCloneAttempted)
            return _clonedSnowMaterial;
        _snowCloneAttempted = true;

        if (CloneUnavailable() is { } reason)
        {
            log($"Cloned campaign: snow material not cloned: {reason}");
            return null;
        }

        string? lightingPath = MapLightingPath();
        string? postProcesses = lightingPath == null ? null : EnvironmentWriter.Attribute(File.ReadAllText(lightingPath), "post_processes");
        string? source = postProcesses == null ? null : CampaignClone.FindSnowMaterial(postProcesses);
        if (source == null)
        {
            log($"Cloned campaign: snow material not cloned: the map's global lighting names none " +
                $"(post_processes: {postProcesses ?? "not found"})");
            return null;
        }

        string? local = _src.Packs!.ExtractFileAsync(source, CloneScratch, ct).GetAwaiter().GetResult();
        if (local == null)
        {
            log($"Cloned campaign: snow material {source} was not found in the packs");
            return null;
        }

        string snowMask = $"terrain/campaigns/{_src.MapName}/snow_mask.dds";
        string? text = EnvironmentWriter.ReplaceAttribute(File.ReadAllText(local), "t_xml_snow_mask", snowMask);
        if (text == null)
        {
            log($"Cloned campaign: snow material {source} has no t_xml_snow_mask; not cloned");
            return null;
        }

        string folder = Path.GetDirectoryName(source.Replace('\\', '/'))!.Replace('\\', '/');
        string target = $"{folder}/{_src.CampaignName}_campaign_snow.xml.material";
        Directory.CreateDirectory(Path.GetDirectoryName(WorkingData(target))!);
        File.WriteAllText(WorkingData(target), text);
        log($"Cloned campaign: snow material {source} -> {WorkingData(target)} (t_xml_snow_mask {snowMask})");
        return _clonedSnowMaterial = target;
    }

    /// <summary>Local copy of the .environment the map's global_lighting names, or null.</summary>
    private string? MapLightingPath()
    {
        if (_src.MapLightingPath != null)
            return _src.MapLightingPath;
        if (_src.CampaignFolder == null || EnvironmentWriter.ReadGlobalLighting(_src.CampaignFolder) is not { } lighting)
            return null;
        return EnvironmentWriter.FindInCampaignFolder(_src.CampaignFolder, lighting);
    }

    /// <summary>
    /// Clones every .environment the map uses into working_data/Weather/campaign/&lt;map&gt;,
    /// each with the cloned snow material, and points the volumes at the clones. What is used is
    /// what environment_collection.xml names - its global_lighting, every volume's lighting and
    /// every devastation's - extracted by those paths, since the lighting has moved between game
    /// updates. Devastation clones are renamed &lt;name&gt;_&lt;map&gt;.environment (et_nagash_&lt;map&gt;);
    /// the rest keep their names.
    /// </summary>
    /// <summary>Where a cloned campaign's copy of a devastation .environment goes: Weather/campaign/&lt;map&gt;/et_nagash_&lt;map&gt;.environment.</summary>
    private string DevastationClonePath(string lighting)
    {
        string path = lighting.Replace('\\', '/').Trim('/');
        return $"Weather/campaign/{_src.MapName}/{Path.GetFileNameWithoutExtension(path)}_{_src.MapName}{Path.GetExtension(path)}";
    }

    /// <summary>
    /// The .terry's devastation_light_environments: every DEVASTATION in environment_collection.xml
    /// as name -> lighting, sorted by name. A cloned campaign gets its clones when they exist
    /// (the environment volumes step writes them), otherwise vanilla's.
    /// </summary>
    private List<(string Name, string Lighting)> DevastationLightEnvironments(Action<string> log)
    {
        if (_src.CampaignFolder == null || !File.Exists(In("environment_collection.xml")))
            return [];

        var result = new List<(string Name, string Lighting)>();
        foreach (var d in EnvironmentVolumeWriter.ReadDevastations(In("environment_collection.xml"))
                     .Where(d => d.Name.Length > 0 && d.LightingFile.Length > 0)
                     .OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            string lighting = d.LightingFile.Replace('\\', '/').Trim('/');
            if (IsClonedCampaign)
            {
                string clone = DevastationClonePath(lighting);
                if (_src.AssemblyKitFolder != null && File.Exists(WorkingData(clone)))
                    lighting = clone;
                else
                    log($"Warning: no clone of {lighting} in working_data (run the environment volumes step); {d.Name} keeps the vanilla lighting");
            }
            result.Add((d.Name, lighting));
        }
        if (result.Count > 0)
            log($"devastation_light_environments: {string.Join(",", result.Select(d => $"{d.Name}={d.Lighting}"))}");
        return result;
    }

    private List<EnvironmentVolumeWriter.Volume> CloneEnvironmentLighting(
        List<EnvironmentVolumeWriter.Volume> volumes, Action<string> log, CancellationToken ct)
    {
        string? snow = CloneSnowMaterial(log, ct);
        if (snow == null)
        {
            log("Cloned campaign: lighting not cloned without a snow material; the volumes keep the vanilla lighting");
            return volumes;
        }

        static string Normalise(string packPath) => packPath.Replace('\\', '/').Trim('/');

        // The census: every .environment environment_collection.xml names, with its clone's file name.
        string collection = In("environment_collection.xml");
        var used = new List<(string Path, string CloneName)>();
        if (EnvironmentWriter.ReadGlobalLighting(_src.CampaignFolder!) is { } global)
            used.Add((Normalise(global), Path.GetFileName(Normalise(global))));
        used.AddRange(volumes.Select(v => Normalise(v.LightingFile)).Select(p => (p, Path.GetFileName(p))));
        var devastations = EnvironmentVolumeWriter.ReadDevastations(collection);
        used.AddRange(devastations.Select(d => Normalise(d.LightingFile))
            .Select(p => (p, Path.GetFileName(DevastationClonePath(p)))));
        if (devastations.Count > 0)
            log($"Devastations: {string.Join(", ", devastations.Select(d => $"{d.Name} ({d.LightingFile})"))}");

        var sources = new List<(string Path, string CloneName, string Local)>();
        string scratch = Path.Combine(CloneScratch, "lighting");
        foreach (var (path, cloneName) in used.Where(u => u.Path.Length > 0).DistinctBy(u => u.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (_src.Packs!.ExtractFileAsync(path, scratch, ct).GetAwaiter().GetResult() is { } local)
                sources.Add((path, cloneName, local));
            else
                log($"Warning: lighting {path} was not found in the packs; not cloned, and what uses it keeps pointing at it");
        }

        string newFolder = $"Weather/campaign/{_src.MapName}";
        var clones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file name -> source
        foreach (var (packPath, name, local) in sources)
        {
            if (written.TryGetValue(name, out string? other))
            {
                log($"Warning: {packPath} and {other} would both be cloned as {name}; keeping {other}");
                continue;
            }
            written[name] = packPath;

            string target = $"{newFolder}/{name}";
            string? oldSnow = CampaignClone.CloneEnvironment(local, WorkingData(target), snow);
            if (oldSnow == null)
                log($"Note: {packPath} has no snow material; cloned unchanged");
            clones[packPath] = target;
        }
        log($"Cloned campaign: {clones.Count} lighting files -> {WorkingData(newFolder)} (snow material {snow})");
        foreach (var d in devastations)
            if (clones.TryGetValue(Normalise(d.LightingFile), out string? clone))
                log($"Cloned campaign: {d.Name} lighting -> {clone}");

        int retargeted = 0;
        var result = volumes.Select(v =>
        {
            if (!clones.TryGetValue(Normalise(v.LightingFile), out string? clone))
                return v;
            retargeted++;
            return v with { LightingFile = clone };
        }).ToList();
        log($"Cloned campaign: {retargeted} of {volumes.Count} volumes point at the cloned lighting");
        return result;
    }

    // ---------------------------------------------------------------- images

    /// <summary>
    /// The land height comes from full_logic_map.compressed_map, a 16-bit copy of the same
    /// Height map at full resolution (see <see cref="LogicMapHeights"/>); full_height_map.dds
    /// carries the sea bed in green (red is the land again, BC6H-quantised; blue is unused).
    /// Without a logic map of the same size the land is the BC6H decode, block stepping included.
    /// </summary>
    private void RunHeight(Action<string> log, TerryIdGenerator ids, Action<double> progress)
    {
        var dds = DdsTexture.Load(In("full_height_map.dds"));
        log($"full_height_map.dds: {dds.Width}x{dds.Height} {dds.Format}");

        // Devastated project: the craters as height differences, which add to the main project's
        // heights. The devastate pieces carry no logic map: the land craters are the devastated BC6H
        // over vanilla's logic-map heights (what the main project has), the noise masked out.
        if (_src.IsDevastated)
        {
            var decoded = dds.DecodeChannelsFloat([0, 1], f => progress(f * 0.3));
            bool land = BaseMap("height", dds.Width, dds.Height, log) != null;
            bool sea = BaseMap("sea_height", dds.Width, dds.Height, log) != null;
            if (!land && !sea)
                return;
            var vanilla = DdsTexture.Load(VanillaIn("full_height_map.dds")).DecodeChannelsFloat([0, 1], f => progress(0.3 + f * 0.3));
            float[]? logicBase = null;
            if (land && File.Exists(VanillaIn("full_logic_map.compressed_map")))
            {
                logicBase = LogicMapHeights.Read(VanillaIn("full_logic_map.compressed_map"), out int lw, out int lh);
                if (lw != dds.Width || lh != dds.Height)
                    logicBase = null;
            }
            if (land && logicBase == null)
                log("Warning: no vanilla full_logic_map.compressed_map of the height map's size: the land craters are BC6H differences.");
            foreach (var (channel, suffix, wanted) in new[] { (1, "sea_height", sea), (0, "height", land) })
            {
                if (!wanted)
                    continue;
                float[] delta = decoded[channel];
                long changed = channel == 0 && logicBase != null
                    ? DevastationLayers.MaskedDelta(vanilla[0], delta, logicBase, dds.Width, dds.Height)
                    : DevastationLayers.Delta(vanilla[channel], delta);
                TryWriteDevastation(ids, suffix, null, changed, p =>
                {
                    var (data, w, h) = Resize(delta, dds.Width, dds.Height, Sampling.Linear, log, fill: [0f]);
                    ScaleHeights(data);
                    TiffWriter.WriteFloat32(p, w, h, data);
                }, log);
            }
            return;
        }

        string logicPath = In("full_logic_map.compressed_map");
        float[]? heights = null;
        if (!File.Exists(logicPath))
        {
            log("Warning: no full_logic_map.compressed_map: the land height is the BC6H decode, block stepping included.");
        }
        else
        {
            var logic = LogicMapHeights.Load(logicPath);
            if (logic.Width != dds.Width || logic.Height != dds.Height)
            {
                log($"Warning: full_logic_map.compressed_map is {logic.Width}x{logic.Height}, the height map {dds.Width}x{dds.Height}: " +
                    "the land height is the BC6H decode, block stepping included.");
            }
            else if (_s.RefineHeightWithNormals && File.Exists(In("lf_normal.dds")))
            {
                var normalDds = DdsTexture.Load(In("lf_normal.dds"));
                if (normalDds.Width == dds.Width && normalDds.Height == dds.Height)
                {
                    log("Land height from full_logic_map.compressed_map, refined against lf_normal.dds");
                    heights = LogicMapHeights.Refine(logic, normalDds.DecodeRgba8(), log, f => progress(f * 0.3));
                }
                else
                {
                    log($"Warning: lf_normal.dds is {normalDds.Width}x{normalDds.Height}, the height map {dds.Width}x{dds.Height}: not refined.");
                    heights = LogicMapHeights.Decode(logic);
                }
            }
            else
            {
                if (_s.RefineHeightWithNormals)
                    log("Warning: no lf_normal.dds: the land height is not refined.");
                log("Land height from full_logic_map.compressed_map");
                heights = LogicMapHeights.Decode(logic);
            }
        }
        progress(0.3);

        var channels = dds.DecodeChannelsFloat(heights == null ? [0, 1] : [1], f => progress(0.3 + f * 0.3));
        heights ??= channels[0];
        float[] seaBed = channels[^1];
        channels = null!;

        string seaPath = Out($"{_src.MapName}.sea_height.{ids.Next()}.tif");
        var seaMap = Resize(seaBed, dds.Width, dds.Height, Sampling.Linear, log);
        seaBed = null!;
        ScaleHeights(seaMap.Data);
        TiffWriter.WriteFloat32(seaPath, seaMap.Width, seaMap.Height, seaMap.Data, f => progress(0.6 + f * 0.2));
        seaMap = default;
        log($"Wrote {Path.GetFileName(seaPath)} (sea bed, green channel)");

        string path = Out($"{_src.MapName}.height.{ids.Next()}.tif");
        var landMap = Resize(heights, dds.Width, dds.Height, Sampling.Linear, log);
        heights = null!;
        ScaleHeights(landMap.Data);
        TiffWriter.WriteFloat32(path, landMap.Width, landMap.Height, landMap.Data, f => progress(0.8 + f * 0.2));
        log($"Wrote {Path.GetFileName(path)}");
    }

    private void RunHeightShroud(Action<string> log, TerryIdGenerator ids, Action<double> progress)
    {
        var dds = DdsTexture.Load(In("shroud_heights.dds"));
        log($"shroud_heights.dds: {dds.Width}x{dds.Height} {dds.Format}");
        float[] heights = dds.DecodeRedFloat(f => progress(f * 0.5));

        if (_src.IsDevastated)
        {
            if (BaseMap("height_shroud", dds.Width, dds.Height, log) == null)
                return;
            long changed = DevastationLayers.Delta(DdsTexture.Load(VanillaIn("shroud_heights.dds")).DecodeRedFloat(), heights);
            TryWriteDevastation(ids, "height_shroud", null, changed, p =>
            {
                var (data, w, h) = Resize(heights, dds.Width, dds.Height, Sampling.Linear, log, fill: [0f]);
                ScaleHeights(data);
                TiffWriter.WriteFloat32(p, w, h, data, f => progress(0.5 + f * 0.5));
            }, log);
            return;
        }

        string path = Out($"{_src.MapName}.height_shroud.{ids.Next()}.tif");
        var shroud = Resize(heights, dds.Width, dds.Height, Sampling.Linear, log);
        ScaleHeights(shroud.Data);
        TiffWriter.WriteFloat32(path, shroud.Width, shroud.Height, shroud.Data, f => progress(0.5 + f * 0.5));
        log($"Wrote {Path.GetFileName(path)}");
    }

    private void RunBlend(Action<string> log, TerryIdGenerator ids, Action<double> progress)
    {
        var dds = DdsTexture.Load(In(Path.Combine("global_map", "global_blend.dds")));
        log($"global_blend.dds: {dds.Width}x{dds.Height} {dds.Format}");
        byte[] indices = dds.DecodeGray8();

        var palette = BlendPalette.Load(_src.BlendTexturesPath!, log);
        int highest = indices.Max();
        if (highest >= palette.Textures.Count)
            log($"Warning: the blend map uses index {highest}, but the texture list has only {palette.Textures.Count} entries " +
                "- it may be from a different game version than the packs.");

        if (_src.IsDevastated)
        {
            if (BaseMap("blend", dds.Width, dds.Height, log) == null)
                return;
            byte[]? layer = DevastationLayers.Keyed(DdsTexture.Load(VanillaIn(Path.Combine("global_map", "global_blend.dds"))).DecodeGray8(),
                indices, 255, out long changed);
            if (TryWriteDevastation(ids, "blend", layer == null ? "a changed pixel is blend index 255, which a layer cannot hold (it is transparent)" : null,
                    changed, p =>
                    {
                        var (data, w, h) = Resize(layer!, dds.Width, dds.Height, Sampling.Nearest, log, fill: [(byte)255]);
                        TiffWriter.WritePalette8(p, w, h, data, palette.ToRgbTable(), progress);
                    }, log))
                return;
        }

        string id = ids.Next();
        string path = Out($"{_src.MapName}.blend.{id}.tif");
        var blend = Resize(indices, dds.Width, dds.Height, Sampling.Nearest, log);
        TiffWriter.WritePalette8(path, blend.Width, blend.Height, blend.Data, palette.ToRgbTable(), progress);
        log($"Wrote {Path.GetFileName(path)}");
        Record("blend", DevastationMapMode.Own, id);
    }

    /// <summary>
    /// event_area_mask.dds is indexed like the blend map: each value is a row of the
    /// campaign_map_event_areas table, in key order (see <see cref="EventAreaPalette"/>).
    /// </summary>
    private void RunEventAreaMask(Action<string> log, TerryIdGenerator ids, Action<double> progress)
    {
        var dds = DdsTexture.Load(In("event_area_mask.dds"));
        log($"event_area_mask.dds: {dds.Width}x{dds.Height} {dds.Format}");
        byte[] indices = dds.DecodeGray8(f => progress(f * 0.5));

        var palette = _src.EventAreas!;
        log($"campaign_map_event_areas: {palette.Areas.Count} event areas");
        // 255 is what an unpainted Terry mask holds; the shipped masks fill with 0 (_empty).
        var counts = new long[256];
        foreach (byte i in indices)
            counts[i]++;
        var used = Enumerable.Range(0, 255).Where(i => counts[i] > 0).ToList();
        log($"The mask uses {used.Count} event areas" +
            (used.Count == 0 ? "" : $": {string.Join(", ", used.Select(i => i < palette.Areas.Count ? palette.Areas[i].Key : $"#{i}"))}"));
        var unknown = used.Where(i => i >= palette.Areas.Count).ToList();
        if (unknown.Count > 0)
            log($"Warning: the event area mask uses {string.Join(", ", unknown.Select(i => $"index {i} ({counts[i]} px)"))}, " +
                $"but the table has only {palette.Areas.Count} rows - they are kept as they are and show black.");

        if (_src.IsDevastated)
        {
            if (BaseMap("event_area_mask", dds.Width, dds.Height, log) == null)
                return;
            byte[]? layer = DevastationLayers.Keyed(DdsTexture.Load(VanillaIn("event_area_mask.dds")).DecodeGray8(), indices, 255, out long changed);
            if (TryWriteDevastation(ids, "event_area_mask", layer == null ? "a changed pixel is index 255, which a layer cannot hold (it is transparent)" : null,
                    changed, p =>
                    {
                        var (data, w, h) = Resize(layer!, dds.Width, dds.Height, Sampling.Nearest, log, fill: [(byte)255]);
                        TiffWriter.WritePalette8(p, w, h, data, palette.ToRgbTable(), f => progress(0.5 + f * 0.5));
                    }, log))
                return;
        }

        string id = ids.Next();
        string path = Out($"{_src.MapName}.event_area_mask.{id}.tif");
        var mask = Resize(indices, dds.Width, dds.Height, Sampling.Nearest, log);
        TiffWriter.WritePalette8(path, mask.Width, mask.Height, mask.Data, palette.ToRgbTable(), f => progress(0.5 + f * 0.5));
        log($"Wrote {Path.GetFileName(path)}");
        Record("event_area_mask", DevastationMapMode.Own, id);
    }

    private void RunColorOverlay(Action<string> log, TerryIdGenerator ids, string sourceFile, string suffix, Action<double> progress)
    {
        var dds = DdsTexture.Load(In(sourceFile));
        log($"{sourceFile}: {dds.Width}x{dds.Height} {dds.Format}");
        byte[] rgba = dds.DecodeRgba8(f => progress(f * 0.5));

        if (_src.IsDevastated)
        {
            if (BaseMap(suffix, dds.Width, dds.Height, log) == null)
                return;
            byte[]? layer = DevastationLayers.AlphaOver(DdsTexture.Load(VanillaIn(sourceFile)).DecodeRgba8(), rgba, out long changed);
            if (TryWriteDevastation(ids, suffix, layer == null ? "a changed pixel is not opaque, so a layer would blend it with the main project's" : null,
                    changed, p =>
                    {
                        var (data, w, h) = Resize(layer!, dds.Width, dds.Height, Sampling.Nearest, log, channels: 4, fill: [(byte)0, (byte)0, (byte)0, (byte)0]);
                        TiffWriter.WriteRgba8(p, w, h, data, f => progress(0.5 + f * 0.5));
                    }, log))
                return;
        }

        string id = ids.Next();
        string path = Out($"{_src.MapName}.{suffix}.{id}.tif");
        var overlay = Resize(rgba, dds.Width, dds.Height, Sampling.Linear, log, channels: 4);
        TiffWriter.WriteRgba8(path, overlay.Width, overlay.Height, overlay.Data, f => progress(0.5 + f * 0.5));
        log($"Wrote {Path.GetFileName(path)}");
        Record(suffix, DevastationMapMode.Own, id);
    }

    private void RunGrayMask(Action<string> log, TerryIdGenerator ids, string sourceFile, string suffix, Action<double> progress)
    {
        var dds = DdsTexture.Load(In(sourceFile));
        log($"{sourceFile}: {dds.Width}x{dds.Height} {dds.Format}");
        byte[] gray = dds.DecodeGray8(f => progress(f * 0.5));
        int width = dds.Width, height = dds.Height;
        bool snow = suffix == "snow_mask";
        if (snow)
        {
            (width, height) = SnowMaskSize(dds.Width, dds.Height, log);
            gray = SnowMaskStretch.Unstretch(gray, dds.Width, dds.Height, width, height);
        }

        if (_src.IsDevastated)
        {
            if (BaseMap(suffix, width, height, log) == null)
                return;
            var vanillaDds = DdsTexture.Load(VanillaIn(sourceFile));
            byte[] vanilla = vanillaDds.DecodeGray8();
            if (snow)
                vanilla = SnowMaskStretch.Unstretch(vanilla, vanillaDds.Width, vanillaDds.Height, width, height);
            // Corruption and snow layers add (dst + src * (1 - dst)).
            byte[]? layer = DevastationLayers.Union(vanilla, gray, out long changed);
            string? cannot = layer == null
                ? "it changes pixels that are not 0 in the vanilla map (mostly lowering them), and a mask layer can only raise a pixel"
                : null;
            if (TryWriteDevastation(ids, suffix, cannot, changed,
                    p =>
                    {
                        var (data, w, h) = Resize(layer!, width, height, Sampling.Linear, log, fill: [(byte)0]);
                        TiffWriter.WriteGray8(p, w, h, data, f => progress(0.5 + f * 0.5));
                    }, log))
                return;
        }

        string id = ids.Next();
        string path = Out($"{_src.MapName}.{suffix}.{id}.tif");
        var resized = Resize(gray, width, height, Sampling.Linear, log);
        TiffWriter.WriteGray8(path, resized.Width, resized.Height, resized.Data, f => progress(0.5 + f * 0.5));
        log($"Wrote {Path.GetFileName(path)}");
        Record(suffix, DevastationMapMode.Own, id);
    }

    /// <summary>
    /// Terry's snow mask is a quarter of the height map, like the tree map. snow_mask.dds is BOB's
    /// stretch of it to a slightly bigger size (see <see cref="SnowMaskStretch"/>), so decompiling
    /// shrinks it back. Otherwise recompiling would leave it where BOB puts a TIFF of that size,
    /// up to 4 pixels north-west of where the game has it.
    /// </summary>
    private (int Width, int Height) SnowMaskSize(int ddsWidth, int ddsHeight, Action<string> log)
    {
        var (heightWidth, heightHeight) = DdsTexture.ReadSize(In("full_height_map.dds"));
        int width = heightWidth / 4, height = heightHeight / 4;
        var compiled = SnowMaskStretch.CompiledSize(width, height);
        if (compiled != (ddsWidth, ddsHeight))
            log($"Warning: snow_mask.dds is {ddsWidth}x{ddsHeight}, but BOB compiles a {width}x{height} snow mask " +
                $"(a quarter of the height map) to {compiled.Width}x{compiled.Height}; resampled to {width}x{height} anyway");
        else
            log($"Snow mask: un-stretched to {width}x{height} (BOB stretches it to {ddsWidth}x{ddsHeight})");
        return (width, height);
    }

    private void RunTrees(Action<string> log, TerryIdGenerator ids)
    {
        // The CampaignTree map is quarter the height map resolution in CA projects.
        var heightDds = DdsTexture.Load(In("full_height_map.dds"));
        int width = heightDds.Width / 4, height = heightDds.Height / 4;
        var trees = TreeListRenderer.Read(_src.TreeListPath!);
        if (_src.IsDevastated)
        {
            if (BaseMap("tree", width, height, log) == null)
                return;
            var maskDds = DdsTexture.Load(Path.Combine(_src.CampaignFolder!, "event_area_mask.dds"));
            trees = DevastatedMap.MergeTrees(trees, _normalPieces ?? EmptyPieces, _devastatePieces!,
                maskDds.DecodeGray8(), maskDds.Width, maskDds.Height, log);
            log($"{TypeOf("tree")}: written whole, the main project's layers kept under it as hidden references: " +
                $"a tree layer cannot take away the main project's trees ({TreePalette.Empty}, no tree, is its transparent value)");
        }
        var palette = _src.TreePalette!;
        log($"campaign_tree_ids: {palette.TreeIdCount} tree ids, {palette.Colors.Count} colours");
        var result = TreeListRenderer.Render(trees, palette, width, height, log);
        log($"Tree map {width}x{height}, world size {result.WorldWidth:0.##}x{result.WorldHeight:0.##}");

        string id = ids.Next();
        string path = Out($"{_src.MapName}.tree.{id}.tif");
        // The new ground starts out bare: repeating the edge would draw stripes of whatever trees stand there.
        // Scaled by pixels, so a forest keeps its density (the trees themselves do not scale).
        var treeMap = Resize(result.Pixels, width, height, Sampling.Nearest, log, fill: [TreePalette.Empty]);
        TiffWriter.WritePalette8(path, treeMap.Width, treeMap.Height, treeMap.Data, palette.ToRgbTable());
        log($"Wrote {Path.GetFileName(path)}");
        Record("tree", DevastationMapMode.Own, id);
    }

    private void RunTileMap(Action<string> log)
    {
        var database = TileDatabase.Load(_src.TileDatabaseFolder!, _s.Game, log);
        string settingsPath = TileSettings.FindBeside(_src.TileDatabaseFolder!)
            ?? throw new FileNotFoundException(
                "_settings.bin was not found in the tile database. It holds the tile set colours and is " +
                $"expected beside the tile shapes, in the _tile_database folder above '{_src.TileDatabaseFolder}'.");
        var settings = TileSettings.Load(settingsPath, log);
        var tiles = TileMapRenderer.Read(In("tile_list.bin"));
        if (_src.IsDevastated)
            tiles = DevastatedMap.MergeRoads(tiles, _normalPieces ?? EmptyPieces, _devastatePieces!, log);
        var result = TileMapRenderer.Render(tiles, database, settings, log);
        log($"Tile map {result.Width}x{result.Height}, {result.TileCount} tiles placed");

        var tileMap = Resize(result.TileMapRgb, result.Width, result.Height, Sampling.Nearest, log, channels: 3);
        SavePngRgb(Out("tile_map.png"), tileMap.Width, tileMap.Height, tileMap.Data);
        var climateMap = Resize(result.ClimateMapRgb, result.Width, result.Height, Sampling.Nearest, log, channels: 3);
        SavePngRgb(Out("climate_map.png"), climateMap.Width, climateMap.Height, climateMap.Data);
        log("Wrote tile_map.png and climate_map.png");
    }

    /// <summary>
    /// lf_normal.dds is DXT5nm (X in alpha, Y in green); Terry wants the ordinary RGB normal
    /// map it compiles from ("..._normal.*" -> dxt5n in rules.bob). Checked against CA's own
    /// cr_oldworld_map_1 lf_normal.png: X and Y carry the same signs and orientation as the
    /// DDS channels, Z is rebuilt, alpha is opaque.
    /// </summary>
    private void RunNormalMap(Action<string> log, Action<double> progress)
    {
        if (_src.IsDevastated)
        {
            // The pieces carry lf_normal.dds unchanged, so the main project's can serve for both.
            string baseNormal = Path.Combine(_src.BaseOutputFolder!, "lf_normal.png");
            if (!File.Exists(baseNormal))
            {
                log($"Warning: lf_normal.png left out of the devastated project: the main project has none ({baseNormal}). " +
                    "Run the Normal map step for the main project first.");
                return;
            }
            if (FilesEqual(In("lf_normal.dds"), VanillaIn("lf_normal.dds")))
            {
                string? copied = HardLink.CreateOrCopy(baseNormal, Out("lf_normal.png"));
                log(copied == null
                    ? $"lf_normal.png: the pieces leave lf_normal.dds unchanged; hard-linked to {baseNormal}"
                    : $"Warning: lf_normal.png copied from {baseNormal}, not hard-linked ({copied})");
                return;
            }
            // It may be a link from an earlier run: writing through it would change the main project's.
            File.Delete(Out("lf_normal.png"));
        }

        var dds = DdsTexture.Load(In("lf_normal.dds"));
        log($"lf_normal.dds: {dds.Width}x{dds.Height} {dds.Format}");
        byte[] decoded = dds.DecodeRgba8(f => progress(f * 0.5));
        // Resized as X and Y; Z is rebuilt from them below, which makes the blended normals unit length again.
        var (rgba, width, height) = Resize(decoded, dds.Width, dds.Height, Sampling.Linear, log, channels: 4);
        decoded = null!;

        Parallel.For(0, height, y =>
        {
            long row = (long)y * width * 4;
            for (int x = 0; x < width; x++)
            {
                long o = row + x * 4L;
                byte xByte = rgba[o + 3];
                double nx = xByte / 127.5 - 1.0;
                double ny = rgba[o + 1] / 127.5 - 1.0;
                double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                rgba[o] = xByte;
                rgba[o + 2] = (byte)Math.Clamp(Math.Round((nz + 1.0) * 127.5), 0, 255);
                rgba[o + 3] = 255;
            }
        });
        progress(0.6);

        SavePngRgba(Out("lf_normal.png"), width, height, rgba);
        log("Wrote lf_normal.png");
    }

    // ------------------------------------------------- environment volumes

    /// <summary>
    /// Writes the layer of environment volumes: the spheres and cylinders that fade the
    /// map's lighting over to a local .environment. Their lighting paths are left pointing
    /// at the source map's lighting, which resolves out of the packs whatever the new map is
    /// called - except for a cloned campaign, which gets its own copies.
    /// </summary>
    private void RunEnvironmentVolumes(Action<string> log, TerryIdGenerator ids, CancellationToken ct)
    {
        var (scale, offsetX, offsetZ) = WorldTransform(log);
        var volumes = EnvironmentVolumeWriter.Read(In("environment_collection.xml"))
            .Select(v => v with
            {
                X = v.X * scale + offsetX, Y = v.Y * scale, Z = v.Z * scale + offsetZ,
                InnerRadius = v.InnerRadius * scale, OuterRadius = v.OuterRadius * scale,
            }).ToList();
        // Before the empty check: the global and devastation lighting need cloning with no volumes too.
        if (IsClonedCampaign)
            volumes = CloneEnvironmentLighting(volumes, log, ct);
        if (volumes.Count == 0)
        {
            log($"    no {EnvironmentVolumeWriter.LayerName} layer: environment_collection.xml has no spheres or cylinders");
            return;
        }

        string path = Out($"{_src.MapName}.{ids.Next()}.layer");
        EnvironmentVolumeWriter.Write(path, volumes, ids);

        int spheres = volumes.Count(v => v.IsSphere);
        var environments = volumes.Select(v => v.EnvironmentName)
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        log($"Wrote {Path.GetFileName(path)} ({EnvironmentVolumeWriter.LayerName}, {spheres} spheres, " +
            $"{volumes.Count - spheres} cylinders, grouped into {environments.Count} environments)");
        log($"Environments: {string.Join(", ", environments)}");
    }

    private void RunCatchment(Action<string> log, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(_src.BattlesFolder!, "blm_*.compressed_map").ToList();
        if (files.Count == 0)
        {
            log($"No blm_*.compressed_map files found in {_src.BattlesFolder}");
            return;
        }
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            var map = CompressedMapReader.ReadRleCard32(file);
            string output = BattleOut(Path.GetFileNameWithoutExtension(file) + ".png");
            SavePngRgba(output, map.Width, map.Height, map.Pixels);
            log($"Wrote {Path.GetFileName(output)} ({map.Width}x{map.Height})");
        }
    }

    // ---------------------------------------------------------------- .terry

    private static readonly (string Suffix, string Type)[] SuffixToType =
    [
        ("height", "Height"),
        ("sea_height", "HeightSea"),
        ("height_shroud", "HeightShroud"),
        ("blend", "BlendCampaign"),
        ("color_overlay", "ColorOverlay"),
        ("color_overlay_sea", "ColorOverlaySea"),
        ("tree", "CampaignTree"),
        ("corruption_mask", "CorruptionMask"),
        ("snow_mask", "SnowMask"),
        ("event_area_mask", "EventAreaMask"),
    ];

    /// <summary>
    /// Builds the .terry file from whatever this and previous runs produced in the
    /// output folder (newest file wins per terrain map type / layer).
    /// </summary>
    private void RunTerryProject(Action<string> log, TerryIdGenerator ids, CancellationToken ct)
    {
        var terrainMaps = _src.IsDevastated ? DevastatedTerrainMaps(log) : CollectTerrainMaps(_src.OutputFolder, _src.MapName, log);
        AddBlankPatchVisibilityMask(terrainMaps, log);

        // Layer files: <map>.<id>.layer, layer name in the comment on line 2.
        var layerPattern = new Regex($"^{Regex.Escape(_src.MapName)}\\.([0-9a-f]{{15}})\\.layer$");
        var layers = new Dictionary<string, (string Id, DateTime Written)>();
        foreach (string file in Directory.EnumerateFiles(_src.OutputFolder, $"{_src.MapName}.*.layer"))
        {
            var match = layerPattern.Match(Path.GetFileName(file));
            if (!match.Success)
                continue;
            string name = ReadLayerName(file) ?? Path.GetFileName(file);
            var written = File.GetLastWriteTimeUtc(file);
            if (!layers.TryGetValue(name, out var existing) || written > existing.Written)
                layers[name] = (match.Groups[1].Value, written);
        }

        var duplicateIds = terrainMaps.SelectMany(m => m.Layers.Select(l => l.Id))
            .Concat(layers.Values.Select(l => l.Id))
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
            log($"Warning: duplicate layer ids across files ({string.Join(", ", duplicateIds)}). " +
                "Terry needs unique ids - regenerate the affected files (this only happens with a fixed --seed).");

        double worldWidth = ResizedWorldWidth(log);

        string path = Out($"{_src.MapName}.terry");
        TerryProjectWriter.Write(path, new TerryProjectWriter.ProjectInfo
        {
            // The devastated project uses the main project's: the devastate map exists only as pieces,
            // so Terry finds none of its terrain (the tile map) under its own folder and cannot open it.
            TerrainSetup = _src.IsDevastated
                ? TerryProjectReader.ReadTerrainSetup(BaseTerryPath) ?? $"terrain/campaigns/{_src.BaseMapName}/"
                : $"terrain/campaigns/{_src.MapName}/",
            WorldWidth = worldWidth,
            GlobalLighting = _src.GlobalLighting,
            DevastationLightEnvironments = DevastationLightEnvironments(log),
            WaterPlaneMaterial = $"materials/environment/campaign_sea/{_src.CampaignName}_campaign_water_plane.xml.material",
            Layers = layers.Select(kv => new TerryProjectWriter.LayerRef(kv.Key, kv.Value.Id)).ToList(),
            TerrainMaps = terrainMaps,
        }, ids);

        log($"Wrote {Path.GetFileName(path)}: {layers.Count} layers, {terrainMaps.Count} terrain maps, world_width={worldWidth:0.####}");
        if (terrainMaps.All(m => m.Type != "HeightSea"))
            log("Note: no .sea_height TIFF in the output folder; run the Height map step to write one.");

        // The devastated project points at the main project's Weather environment.
        if (!_src.IsDevastated)
            WriteWeatherEnvironment(log, ct);
    }

    /// <summary>
    /// Writes the Weather/.environment the project's global_lighting points at, which is
    /// part of setting the project up rather than a step of its own.
    ///
    /// A hand-made project has one per map next to the templates (The Old World has
    /// default_battle_oldworld.environment, an IEE project has default_battle_iee.environment).
    /// This makes the same thing: a copy of working_data/Weather/terry_default_battle.environment
    /// with its empty post_processes attribute filled in from the compiled map's own lighting -
    /// the snow and corruption overlay materials, which is what makes those effects show up in Terry.
    /// </summary>
    private void WriteWeatherEnvironment(Action<string> log, CancellationToken ct)
    {
        log($"The project's global_lighting points at {_src.GlobalLighting}");

        if (_src.AssemblyKitFolder == null)
        {
            log($"    {_src.EnvironmentFileName} not written: no Assembly Kit folder resolved (set it, or the output folders, explicitly)");
            return;
        }
        if (_src.CampaignFolder == null)
        {
            log($"    {_src.EnvironmentFileName} not written: the compiled campaign folder was not found");
            return;
        }

        string weather = Path.Combine(_src.AssemblyKitFolder, "working_data", "Weather");
        string template = Path.Combine(weather, "terry_default_battle.environment");
        if (!File.Exists(template))
        {
            log($"    {_src.EnvironmentFileName} not written: no template at {template}");
            return;
        }

        string? lighting = EnvironmentWriter.ReadGlobalLighting(_src.CampaignFolder);
        if (lighting == null)
        {
            log($"    {_src.EnvironmentFileName} not written: the map's environment_collection.xml has no global_lighting");
            return;
        }
        log($"The map's global_lighting: {lighting}");

        // Out of the packs when they were read; otherwise only a path inside the map folder resolves.
        string? lightingPath = _src.MapLightingPath ?? EnvironmentWriter.FindInCampaignFolder(_src.CampaignFolder, lighting);
        if (lightingPath == null)
        {
            log($"    {_src.EnvironmentFileName} not written: {lighting} was found neither in the packs nor in {_src.CampaignFolder}");
            return;
        }

        string? postProcesses = EnvironmentWriter.ReadPostProcesses(lightingPath, log);
        if (postProcesses == null)
        {
            log($"    {_src.EnvironmentFileName} not written: could not read post_processes from the map's lighting");
            return;
        }

        // The corruption material stays shared; only the snow one is the clone's own.
        if (IsClonedCampaign && CloneSnowMaterial(log, ct) is { } snow)
        {
            postProcesses = CampaignClone.ReplaceSnowMaterial(postProcesses, snow);
            log($"post_processes for the cloned campaign: {postProcesses}");
        }

        string target = Path.Combine(weather, _src.EnvironmentFileName);
        if (File.Exists(target))
            log($"Overwriting existing {_src.EnvironmentFileName}");
        EnvironmentWriter.Write(template, target, postProcesses);
        log($"Wrote {target}");
    }

    /// <summary>
    /// The battle terrain project CA keeps beside each campaign map (terrain/battles/&lt;map&gt;):
    /// the catchment override PNGs plus a skeleton .terry whose 2x2 zero height / sea height
    /// TIFFs and 4x4 single-colour tile / climate maps are placeholders, exactly as in The Old World's
    /// cr_oldworld_map battle project. Existing placeholders are reused, never overwritten.
    /// </summary>
    private void RunBattleProject(Action<string> log, TerryIdGenerator ids)
    {
        string folder = _src.BattleOutputFolder;
        string name = _src.BattleMapName;

        foreach (string suffix in (string[])["height", "sea_height"])
        {
            if (Directory.EnumerateFiles(folder, $"{name}.{suffix}.*.tif").Any())
                continue;
            string path = BattleOut($"{name}.{suffix}.{ids.Next()}.tif");
            TiffWriter.WriteFloat32(path, 2, 2, new float[4]);
            log($"Wrote placeholder {Path.GetFileName(path)}");
        }

        foreach (var (file, colour) in (ValueTuple<string, Rgb24>[])[("tile_map.png", new Rgb24(255, 120, 200)), ("climate_map.png", new Rgb24(115, 83, 34))])
        {
            string path = BattleOut(file);
            if (File.Exists(path))
                continue;
            using var image = new Image<Rgb24>(4, 4, colour);
            image.SaveAsPng(path);
            log($"Wrote placeholder {file}");
        }

        var terrainMaps = CollectTerrainMaps(folder, name, log);
        double worldWidth = ResizedWorldWidth(log);
        string terry = BattleOut($"{name}.terry");
        TerryProjectWriter.Write(terry, new TerryProjectWriter.ProjectInfo
        {
            TerrainSetup = $"terrain/battles/{name}/",
            WorldWidth = worldWidth,
            GlobalLighting = "",
            WaterPlaneMaterial = "",
            Layers = [],
            TerrainMaps = terrainMaps,
        }, ids);
        log($"Wrote {Path.GetFileName(terry)}: {terrainMaps.Count} terrain maps, world_width={worldWidth:0.####}");
    }

    /// <summary>
    /// The devastated project's terrain maps, as its terrain steps made them up this run. Every map
    /// gets the main project's layers as they are now (its .terry, its files, hard-linked under this
    /// project's names) with the devastation layer on top; under a map written whole they are hidden
    /// "reference" layers (<see cref="DevastationLayers.Stack"/>). Links left from an earlier run are
    /// removed first; a name with another name beside it holds nothing that is lost with it.
    /// </summary>
    private List<TerryProjectWriter.TerrainMapRef> DevastatedTerrainMaps(Action<string> log)
    {
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(_src.OutputFolder, $"{_src.MapName}.*.tif"))
            if (HardLink.LinkCount(file) > 1)
            {
                File.Delete(file);
                removed++;
            }
        // Written by an earlier version of this step.
        File.Delete(Out("devastation_layers.json"));

        var maps = new List<TerryProjectWriter.TerrainMapRef>();
        int linked = 0, copies = 0;
        string? copyReason = null;
        foreach (var (suffix, type) in SuffixToType)
        {
            if (!_devastatedMaps.TryGetValue(type, out var entry))
                continue;

            var baseMap = _baseMaps!.First(m => m.Type == type);
            foreach (var layer in baseMap.Layers)
            {
                if (HardLink.CreateOrCopy(BaseFile(suffix, layer.Id), Out($"{_src.MapName}.{suffix}.{layer.Id}.tif")) is { } reason)
                {
                    copies++;
                    copyReason = reason;
                }
                linked++;
            }
            maps.Add(baseMap with { Layers = DevastationLayers.Stack(baseMap.Layers, entry.Mode, entry.LayerId) });
            string main = string.Join(" + ", baseMap.Layers.Select(l => $"'{l.Name}'"));
            log($"    {type}: " + entry.Mode switch
            {
                DevastationMapMode.Base => $"the main project's {main} (linked), unchanged by the pieces",
                DevastationMapMode.Overlay => $"the main project's {main} (linked) + 'devastation'",
                _ => $"the main project's {main} (linked, hidden as 'reference') + the whole devastated map as 'devastation'",
            });
        }

        log($"{linked} of the main project's terrain map files hard-linked" +
            (removed > 0 ? $", replacing {removed} links from an earlier run" : "") +
            (copies > 0 ? $"; {copies} could not be and were copied ({copyReason}), so edits to them are not shared" : ""));
        return maps;
    }

    /// <summary>
    /// Adds the PatchVisibilityMask, with no layers, at the size Terry needs: its patch grid for the
    /// project's tile map, a quarter of the height map (see <see cref="PatchVisibilityGrid"/>). Only
    /// Realms of Chaos uses patch visibility (compiled to terrain_visibility_mask.dds); patch_mask.dds
    /// is a tile-map file and has nothing to do with it.
    /// </summary>
    private void AddBlankPatchVisibilityMask(List<TerryProjectWriter.TerrainMapRef> maps, Action<string> log)
    {
        int ignored = Directory.EnumerateFiles(_src.OutputFolder, $"{_src.MapName}.patch_visibility_mask.*.tif").Count();
        if (ignored > 0)
            log($"Note: ignoring {ignored} .patch_visibility_mask TIFF(s): earlier versions filled the mask from patch_mask.dds, " +
                "which is not patch visibility");

        if (maps.FirstOrDefault(m => m.Type == "Height") is not { } heightMap)
        {
            log("Warning: no PatchVisibilityMask: it is sized from the height map, and there is none. Run the Height map step.");
            return;
        }
        int tileWidth = heightMap.Width / 4, tileHeight = heightMap.Height / 4;
        var (width, height) = PatchVisibilityGrid.Size(tileWidth, tileHeight);
        maps.Add(new TerryProjectWriter.TerrainMapRef("PatchVisibilityMask", width, height, Layers: []));
        log($"PatchVisibilityMask: blank, {width}x{height} (Terry's patch grid for the {tileWidth}x{tileHeight} tile map)");
    }

    /// <summary>Terrain map TIFFs named &lt;prefix&gt;.&lt;suffix&gt;.&lt;id&gt;.tif; newest wins per type.</summary>
    private static List<TerryProjectWriter.TerrainMapRef> CollectTerrainMaps(string folder, string prefix, Action<string> log)
    {
        var terrainMaps = new List<TerryProjectWriter.TerrainMapRef>();
        foreach (var (suffix, type) in SuffixToType)
        {
            var pattern = new Regex($"^{Regex.Escape(prefix)}\\.{Regex.Escape(suffix)}\\.([0-9a-f]{{15}})\\.tif$");
            var candidates = Directory.EnumerateFiles(folder, $"{prefix}.{suffix}.*.tif")
                .Select(p => (Path: p, Match: pattern.Match(Path.GetFileName(p))))
                .Where(c => c.Match.Success)
                .OrderByDescending(c => File.GetLastWriteTimeUtc(c.Path))
                .ToList();
            if (candidates.Count == 0)
                continue;
            if (candidates.Count > 1)
                log($"Note: multiple .{suffix}.*.tif files found, using newest ({Path.GetFileName(candidates[0].Path)})");

            var (width, height) = ReadTiffSize(candidates[0].Path);
            terrainMaps.Add(new TerryProjectWriter.TerrainMapRef(type, width, height, candidates[0].Match.Groups[1].Value));
        }
        return terrainMaps;
    }

    private double ResolveWorldWidth(Action<string> log)
    {
        if (_s.WorldWidthOverride is { } value)
            return value;

        if (_src.DbWorldWidth is { } dbWidth)
            return dbWidth;

        if (_src.TreeListPath != null && File.Exists(_src.TreeListPath))
        {
            var (width, _) = TreeListRenderer.ReadWorldSize(_src.TreeListPath);
            log($"world_width {width:0.####} taken from the tree list header");
            return width;
        }

        if (_src.CampaignFolder != null && File.Exists(In("full_height_map.dds")))
        {
            var dds = DdsTexture.Load(In("full_height_map.dds"));
            double approx = dds.Width / 12.0;
            log($"Warning: world_width approximated as height map width / 12 = {approx:0.####}. " +
                "Set a manual override for the exact value.");
            return approx;
        }

        log("Warning: could not derive world_width, defaulting to 1000. Set it manually.");
        return 1000;
    }

    private static (int Width, int Height) ReadTiffSize(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new IOException($"Cannot open {path}");
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        return (width, height);
    }

    private static string? ReadLayerName(string layerPath)
    {
        foreach (string line in File.ReadLines(layerPath).Take(3))
        {
            var match = Regex.Match(line, "<!-- (.*) -->");
            if (match.Success)
                return match.Groups[1].Value.Trim();
        }
        return null;
    }

    // ---------------------------------------------------------------- png helpers

    private static void SavePngRgb(string path, int width, int height, byte[] rgb)
    {
        using var image = Image.LoadPixelData<Rgb24>(rgb, width, height);
        image.SaveAsPng(path);
    }

    private static void SavePngRgba(string path, int width, int height, byte[] rgba)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        image.SaveAsPng(path);
    }
}
