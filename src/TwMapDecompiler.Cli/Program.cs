using System.Globalization;
using TwMapDecompiler.Core.Pipeline;
using TwMapDecompiler.Core.Tiles;

if (args.Length < 1 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine($"""
        TwMapDecompiler CLI - decompile a vanilla Total War campaign map into Terry projects.

        Reads the map straight from the game's .pack files through RPFM and writes the
        campaign and battle Terry projects into the Assembly Kit.

        Usage:
          TwMapDecompiler.Cli <campaign> [options]

          e.g. TwMapDecompiler.Cli wh3_main_combi
               TwMapDecompiler.Cli --list-campaigns

        The campaigns DB table supplies the rest: terrain_location (the compiled terrain
        folder), display_location (the campaign_maps folder holding the tree list) and
        battle_path (the battle terrain folder).

        Devastated map (the DevastatedProject step, off by default: add it with --steps):
          The campaign's terrain_location_devastate as a project of its own, built on the main
          project, which must be on disk already: its terrain maps are the main project's,
          hard-linked, with the devastation as a layer on top; one prop layer per event area.
          --devastated-name <name>       Devastated project name (default: terrain_location_devastate,
                                         or <map name> with _map_devastate for _map when renamed)

        Packs (RPFM 5 server):
          --rpfm <folder>                RPFM install folder (default: the RPFM_FOLDER environment variable)
          --game <wh3|3k>                Game (default wh3)
          --list-campaigns               List the campaigns in the packs and exit

        New names (default: keep the vanilla ones):
          --map-name <name>              New map / campaign project name
          --campaign-name <name>         New campaign name (water materials, environment file)
          --battle-name <name>           New battle project name

        Source overrides (default: from the campaigns table):
          --source-map <name>            terrain/campaigns/<name>
          --source-campaign-map <name>   campaign_maps/<name> (display_location; holds the tree list)
          --source-battle-path <name>    terrain/battles/<name>

        Input overrides (default: read from the packs):
          --input <folder>               Compiled terrain/campaigns/<map> folder
          --tree-list <path>             trees.campaign_tree_list
          --tile-db <folder>             terrain/tiles/campaign/_tile_database/tiles
                                         (_settings.bin, holding the tile set colours,
                                          is read from the _tile_database folder above it)
          --battles <folder>             terrain/battles/<map> (blm_*.compressed_map)
          --blend-textures <path>        warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml
                                         (the blend map's texture list and colours)
          --world-width <value>          World width (default: campaign_map_playable_areas maxx - minx)

        Resize (counted on the campaign hex grid, tile_list.bin's header: 1440x970 on combi):
          --scale <factor>               Uniform scale, snapped to the nearest hex grid Terry can hold
                                         (even width, at most 2048 hexes each way); the log says which
                                         factor that is. Terrain maps are resampled; positions, heights,
                                         prop scales, radii, lengths and outlines scale with them
          --pad <l,r,t,b>                Hexes of the scaled hex grid added to the left, right, top
                                         (north) and bottom of the map. One hex is 2 tile map pixels,
                                         4 shroud height pixels and 8 height / blend / overlay pixels;
                                         new pixels repeat the edge (trees start out bare). world_width
                                         grows to match, and padding the left or bottom moves every
                                         prop and environment volume with the map. Left + right must
                                         keep the width even

        Output (default: the Assembly Kit RPFM has configured):
          --assembly-kit <folder>        Assembly Kit root (contains raw_data and working_data);
                                         both Terry projects and the lighting environment go under it

        Other:
          --refine-height                Move each land height within its full_logic_map step to
                                         fit lf_normal.dds (a minute or two; only helps where
                                         lf_normal.dds was made from the same heights)
          --no-custom-cultures           Do not add the modded cultures (mixer_nag_nagash, ...) to
                                         the culture masks of the vanilla cultures they follow
          --seed <int>                   Fixed random seed for entity ids
          --steps <a,b,c>                Only run the listed steps
          --list-steps                   Show the steps and exit
        """);
    return args.Contains("--help") || args.Contains("-h") ? 0 : 1;
}

string? GetOption(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string? GetPath(string name) => GetOption(name) is { } p ? Path.GetFullPath(p) : null;

var game = GetOption("--game") == "3k" ? TileGame.ThreeKingdoms : TileGame.Warhammer3;
string? rpfmFolder = GetOption("--rpfm") ?? DecompileSettings.DefaultRpfmFolder;

if (args.Contains("--list-campaigns"))
{
    var catalog = await CampaignCatalog.LoadAsync(rpfmFolder,
        game == TileGame.ThreeKingdoms ? "three_kingdoms" : "warhammer_3", Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine($"{"campaign",-24} {"terrain_location",-24} {"display_location",-24} {"battle_path",-22} width");
    foreach (var entry in catalog)
        Console.WriteLine($"{entry.CampaignName,-24} {entry.TerrainLocation,-24} {entry.DisplayLocation,-24} {entry.BattlePath,-22} " +
                          $"{entry.WorldWidth?.ToString("0.####", CultureInfo.InvariantCulture) ?? "?"}" +
                          (entry.TerrainLocationDevastate != null ? $"  devastated: {entry.TerrainLocationDevastate}" : ""));
    return 0;
}

var settings = new DecompileSettings
{
    VanillaCampaignName = args[0],
    VanillaMapName = GetOption("--source-map"),
    VanillaCampaignMapName = GetOption("--source-campaign-map"),
    VanillaBattlePath = GetOption("--source-battle-path"),
    MapName = GetOption("--map-name"),
    CampaignName = GetOption("--campaign-name"),
    BattleMapName = GetOption("--battle-name"),
    RpfmFolder = rpfmFolder,
    InputFolder = GetPath("--input"),
    TreeListPath = GetPath("--tree-list"),
    TileDatabaseFolder = GetPath("--tile-db"),
    BattlesFolder = GetPath("--battles"),
    BlendTexturesPath = GetPath("--blend-textures"),
    AssemblyKitFolder = GetPath("--assembly-kit"),
    Game = game,
    WorldWidthOverride = GetOption("--world-width") is { } w ? double.Parse(w, CultureInfo.InvariantCulture) : null,
    RandomSeed = GetOption("--seed") is { } s ? int.Parse(s) : null,
    RefineHeightWithNormals = args.Contains("--refine-height"),
    AddCustomCultures = !args.Contains("--no-custom-cultures"),
    DevastatedMapName = GetOption("--devastated-name"),
    Padding = GetOption("--pad") is { } pad ? ParsePadding(pad) : MapPadding.None,
    Scale = GetOption("--scale") is { } scale ? ParseScale(scale) : 1,
};

var stepInfos = Decompiler.DescribeSteps(settings);

if (args.Contains("--list-steps"))
{
    foreach (var info in stepInfos)
        Console.WriteLine($"  {info.Id,-22} {(info.Available ? info.Description : "unavailable: " + info.UnavailableReason)}");
    return 0;
}

List<StepId> steps;
if (GetOption("--steps") is { } stepsOption)
{
    steps = stepsOption.Split(',').Select(name => Enum.Parse<StepId>(name.Trim(), ignoreCase: true)).ToList();
    var unavailable = steps.Where(step => !stepInfos.First(i => i.Id == step).Available).ToList();
    if (unavailable.Count > 0)
    {
        foreach (var step in unavailable)
            Console.Error.WriteLine($"Step {step} unavailable: {stepInfos.First(i => i.Id == step).UnavailableReason}");
        return 1;
    }
}
else
{
    steps = stepInfos.Where(i => i.DefaultEnabled).Select(i => i.Id).ToList();
}

Console.WriteLine($"Campaign: {settings.VanillaCampaignName}");
Console.WriteLine($"Steps:    {string.Join(", ", steps)}");
Console.WriteLine();

var decompiler = new Decompiler(settings);
try
{
    decompiler.Run(steps, Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine("Done.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex}");
    return 2;
}

static double ParseScale(string value)
{
    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || !(scale > 0) || double.IsInfinity(scale))
        throw new ArgumentException($"--scale takes a number above 0 (got '{value}')");
    return scale;
}

static MapPadding ParsePadding(string value)
{
    int[] sides = value.Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
    if (sides.Length != 4 || sides.Any(v => v < 0))
        throw new ArgumentException($"--pad takes four whole numbers of hexes, 0 or more: left,right,top,bottom (got '{value}')");
    return new MapPadding(sides[0], sides[1], sides[2], sides[3]);
}
