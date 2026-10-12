# Total War Map Decompiler

Decompiles a compiled Total War: Warhammer 3 campaign map (the binary files the
game actually loads) back into **Terry** map-editor projects. C# rewrite of the
Python reverse-engineering scripts in [reference/](reference/), with a GUI.

```
Input:  a campaign, picked from the campaigns DB table   read from the game's .pack files through RPFM
Output: <AK>/raw_data/terrain/campaigns/<map>/           campaign Terry project
        <AK>/raw_data/terrain/battles/<battle map>/      battle Terry project (catchment maps)
        <AK>/working_data/Weather/<campaign>.environment lighting the campaign project points at
```

## Projects

| Project | Purpose |
|---|---|
| [src/TwMapDecompiler.Core](src/TwMapDecompiler.Core/) | All parsers, image codecs, writers, pipeline, RPFM client |
| [src/TwMapDecompiler.App](src/TwMapDecompiler.App/) | WPF GUI |
| [src/TwMapDecompiler.Cli](src/TwMapDecompiler.Cli/) | Command-line runner (same pipeline, scriptable) |

Build and run (requires .NET 8 SDK):

```
dotnet build
dotnet run --project src/TwMapDecompiler.App          # GUI
dotnet run --project src/TwMapDecompiler.Cli -- --help # CLI
```

CLI example:

```
TwMapDecompiler.Cli --list-campaigns
TwMapDecompiler.Cli wh3_main_combi
```

## Where the inputs come from

The files are read from the game's packs through the **RPFM 5 server**
(`rpfm_server.exe`, JSON over a WebSocket on `127.0.0.1:45127`). The tool starts
the server from the RPFM folder when none is running; the server exits by itself
once the tool disconnects. Tell the tool where RPFM is installed (the folder
holding `rpfm_server.exe`) with the GUI's RPFM field, `--rpfm <folder>`, or the
`RPFM_FOLDER` environment variable. Extracted files go to a scratch folder in `%TEMP%`
that is deleted after the run.

Choosing a campaign is the whole input. The **`campaigns`** DB table says where
everything lives, and the GUI reads it at startup to fill its drop-down and show
what the campaign resolves to:

| campaigns column | Used for | Immortal Empires |
|---|---|---|
| `terrain_location` | `terrain/campaigns/<name>/`, the compiled terrain | wh3_main_combi_map_1 |
| `display_location` | `campaign_maps/<name>/display/trees/`, the tree list | wh3_main_combi_map_5 |
| `battle_path` | `terrain/battles/<name>/`, the catchment maps and battle project | wh3_main_combi_map |

Those three names differ from each other, which is why they are looked up rather
than guessed. `campaign_map_playable_areas` then gives the world width
(`maxx - minx`, 961.3 for Immortal Empires); the tile database is always
`terrain/tiles/campaign/_tile_database/`, whose `tiles/` folder holds the tile
shapes and whose `_settings.bin` holds the tile set and climate colours.

Outputs go into the Assembly Kit that RPFM has configured for the game, in its
standard subfolders; `--assembly-kit` points the whole lot somewhere else. The
names and input paths are overridable too (`--map-name`, `--source-map`,
`--input`, ...), and with all of them set the tool only contacts RPFM for the
prop layer step, which reads the province of each region from the DB.
In the GUI these live behind the *Overrides* expander; by default the output
keeps the vanilla names.

### Map scale and padding

The *Resize the map* expander (`--scale <factor>` and `--pad left,right,top,bottom`
on the command line) scales the map and then grows it. Both count on the
campaign hex grid, whose size comes from `tile_list.bin`'s header (1440x970 on
Immortal Empires). The GUI reads every campaign's grid when it loads the
campaign list, and shows what the scale snaps to and the final grid as you type.

The hex grid sits on square pixels: a hex is 2x2 tile map pixels, and the
columns are staggered, which makes every map half a hex taller than its rows
(tile map 2880x1941 = 2 x 1440 by 2 x 970.5). Every terrain map has its own
pixels per hex:

| Maps | Pixels per hex (IE) |
|---|---|
| height, sea height, blend, colour overlays, lf_normal (11520x7764) | 8 |
| shroud heights (5760x3882) | 4 |
| tile, climate, tree, corruption, event area, snow (2880x1941) | 2 |

**Scale** is uniform. Terry takes at most 2048 hexes each way and an even number
across, so the factor is snapped: the width becomes the even number of hexes
nearest width x factor, and that fixes the factor actually used (1.25 on IE gives
1800 hexes, exactly 1.25; 1.001 gives 1442, 1.00139). The rows follow as
round(factor x (rows + 1/2) - 1/2), 1213 at 1.25. The half hex of the staggered
columns doesn't scale in whole rows, so the scaled map generally ends up to half
a hex short of, or past, the grid's north edge (0.375 hexes short at 1.25). The
world's origin is the south-west corner, so that sliver goes at the north edge:
repeated from the map's edge, or cropped. A factor that would exceed 2048 hexes
is capped. Every map keeps its pixels per hex, so hexes and texels keep their
world size, and the world scales with them about the origin:

- Maps: indexed maps (blend, event area, tree, tile, climate) and the hard-edged
  devastation layers are resampled nearest-neighbour; heights, colour overlays,
  snow and corruption masks and lf_normal are bilinear when enlarging and
  averaged when shrinking. Heights are multiplied by the factor too, which is
  what keeps lf_normal valid as it is (the normal map's X and Y are resampled,
  and Z rebuilt). Forests keep their density, since the trees themselves don't
  scale.
- Props, VFX and composite scenes: position, and the transform's scale.
- Everything without a transform scale has its own sizes scaled instead: light
  probe and environment volume radii, point light radius, spot light length,
  sound emitter spheres, lines and point clouds, and the outlines of terrain
  holes and polygon meshes (water planes included).
- world_width: multiplied by the factor.

**Padding** then adds hexes of the scaled grid to each side; left + right must
keep the width even. Every map grows by its pixels per hex. The new pixels repeat
the map's edge, except on the tree map, where the new ground starts with no
trees. world_width grows by the same proportion. Padding the left or bottom
(south) moves every prop and environment volume east or north with the map. The
sea water planes and full-shroud meshes don't grow over the padding.

The patch visibility mask isn't resized. It's written blank at Terry's
patch-grid size for the resized tile map (see below).

## Pipeline steps

| Step | Input | Output | Reference script |
|---|---|---|---|
| Prop layers | global_props.bin + global_props_sound.bin + models/ (baked river meshes) + region_to_province_junctions + prefab_types | `<map>.<id>.layer` per province (+ `_global`), rivers as river splines (see [Rivers](#rivers)) | 1 |
| Height map | full_logic_map.compressed_map (16-bit) + full_height_map.dds (BC6H) | `.height` (from the logic map) + `.sea_height` (BC6H green) 32-bit float TIFFs | 3 |
| Shroud height | shroud_heights.dds (R32F) | `.height_shroud` float TIFF | (new) |
| Blend map | global_map/global_blend.dds (L8) | `.blend` indexed TIFF | 2 |
| Colour overlay | colour_overlay.dds (BC1) | `.color_overlay` RGBA TIFF | (new) |
| Sea colour overlay | lf_sea_colour.dds (BC1) | `.color_overlay_sea` RGBA TIFF | (new) |
| Snow mask | snow_mask.dds (BC4) + full_height_map.dds (size) | `.snow_mask` gray TIFF, un-stretched to a quarter of the height map | (new) |
| Corruption mask | corruption_mask.dds (R8) | `.corruption_mask` gray TIFF | (new) |
| Event area mask | event_area_mask.dds (R8) + campaign_map_event_areas | `.event_area_mask` indexed TIFF | (new) |
| Tree map | trees.campaign_tree_list + campaign_tree_ids | `.tree` indexed TIFF | 4 |
| Tile map | tile_list.bin + tile database + _settings.bin | tile_map.png + climate_map.png | 8 |
| Normal map | lf_normal.dds (DXT5nm) | lf_normal.png (RGB normal map) | – |
| Environment volumes | environment_collection.xml | `<map>.<id>.layer` of the lighting spheres and cylinders (`_environments`) | (new) |
| Catchment maps | battles blm_*.compressed_map | blm_*.png in the battle project | 5 |
| Campaign Terry project | everything above + lighting/*.environment | `<map>.terry` (with a blank PatchVisibilityMask) and the `working_data/Weather/default_battle_<campaign>.environment` it points at | 1 (partially) |
| Battle Terry project | – | `<battle map>.terry` + placeholders | (new) |
| Devastated project (off by default) | the main project on disk + terrain/campaigns/&lt;terrain_location_devastate&gt; | the devastated Terry project (see [Devastated maps](#devastated-maps)) | (new) |

The two project steps scan their output folders, so steps can be re-run
individually; per terrain-map type the newest TIFF wins, per layer name the
newest layer file wins. A step whose input is missing from the packs is skipped with a note.

The campaign project's PatchVisibilityMask is always blank (no layers). Only
Realms of Chaos uses patch visibility (compiled to `terrain_visibility_mask.dds`).
`patch_mask.dds` is a tile-map file and has nothing to do with it; earlier
versions wrongly decompiled it into this map. Terry needs the map at its own
patch-grid size, which it takes from the tile map (warscape's
`TERRAIN_DIMENSIONS::campaign_control_points`, about 128 patches across):
125x84 for combi's 2880x1941 tiles, 128x77 once padded to 3200x1941. At any
other size Terry shows only the tiles in the global mesh (roads, cliffs, coast).

All TIFFs are LZW-compressed with the bit depths and photometric layouts Terry
writes. The blend, event area and tree TIFFs are indexed. The blend TIFF's colour table is
read from the packs' `warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml`:
a `global_blend.dds` value is an index into that file's `campaign_base_colour`
entries, in file order, and each entry's `display_r/g/b` is the colour Terry
shows it in. The entries are sorted by name, so a game update that adds textures
shifts every index after them (the 2026 update's `cwb_*` and `nagash_creep*`
entries moved everything from index 40 on). That is why this table is read, not
recorded. The event area mask works the same way against the
`campaign_map_event_areas` DB table: every row, sorted by key (`_empty`, black, is
index 0 and fills the background), which reproduces the table of CA's own
`wh3_main_combi_map_1` event area TIFF exactly. The tree map's table comes from the
`campaign_tree_ids` DB table: each tree id (a variant, `tree_large_01`) has a
`colour_hex`, shared by all variants of a type, and the table is those colours
sorted by RGB value. Every CA tree TIFF is laid out that way (the white last entry
is `tree_large_burnt`, not a sentinel), and the result matches the table of CA's
`wh3_main_combi_map_1` tree TIFF exactly. 255 means no vegetation.

The layout of both projects follows the author's own The Old World map
(`cr_oldworld_map_1`), a campaign built in Terry and used throughout as the
reference for what a hand-made project looks like. The campaign
project holds `tile_map.png` and `lf_normal.png` next to the TIFFs. The battle
project (`terrain/battles/cr_oldworld_map`) holds the three catchment override
PNGs plus a skeleton `.terry` whose 2x2 zero Height / HeightSea TIFFs and 4x4
single-colour `tile_map.png` / `climate_map.png` are placeholders; the tool
writes the same placeholders, and never overwrites existing ones.

The campaign project's `global_lighting` points at a Weather environment file
written as part of setting the project up, the way a hand-made project has one
per map (The Old World has `default_battle_oldworld.environment`, an IEE project
has `default_battle_iee.environment`): a copy of
`working_data/Weather/terry_default_battle.environment` whose empty
`post_processes` attribute is filled in from the map's own lighting - follow
`environment_collection.xml`'s `global_lighting` to the `.environment` it names
and copy its `post_processes` (for Immortal Empires the snow and corruption
overlay materials). Nothing else in the template is touched. `global_lighting`
is a pack path that has moved between game updates (once
`terrain/campaigns/<map>/lighting/default.environment`, now
`Weather/campaign/combi/default.environment`), so the file is extracted from the
packs wherever it points; with an explicit input folder only a path inside the
map folder can be found.

Newer `environment_collection.xml` files end with an `ENVIRONMENT_DEVASTATIONS`
section: map-wide end-times lighting (`devastation_nagash` ->
`Weather/et_nagash.environment`, ...) that nothing else references. These go into
the `.terry`'s `devastation_light_environments` as `name=path` pairs, comma
separated and sorted by name, on every run; a cloned campaign gets its clones
there (see below).

## Environment volumes

The same `environment_collection.xml` also holds the map's environment volumes -
the spheres and cylinders that fade the lighting over to a local
`.environment` between an inner and an outer radius, which is what gives
Skavenblight its green murk and Cathay its haze. They get a layer of their own,
`_environments`, one entity per volume carrying `ECEnvironmentVolume` plus
`ECDoubleSphere` or `ECDoubleCylinder`, collected into logical layers by
environment name the way The Old World's `cr_oldworld_map_1` does it. The combi map has
66 of them (1 sphere, 65 cylinders) across 18 environments.

One attribute does not survive compilation: a cylinder's `height`, which is
written as 0 - the value 205 of `cr_oldworld_map_1`'s 247 cylinders carry.
Position and both radii are stored and come through as they are. The
`lighting_file` paths are left pointing at the source map's lighting,
which Terry resolves out of the packs whatever the new map is called - unless
the campaign is cloned (below).

## Cloned campaigns

Overriding the campaign name marks the map as a clone, which gets its own copies
of the files vanilla would otherwise share with it, all in the Assembly Kit's
`working_data` under their pack paths:

- **Sea material**: the material the sea water planes use (they share one) is
  copied unchanged to
  `materials/environment/campaign_sea/<campaign>_campaign_water_plane.xml.material`
  (the `.terry`'s water plane material), and every sea water plane in `_global`
  is pointed at it.
- **Snow material**: the snow entry in the `post_processes` of the `.environment`
  named by `global_lighting` is copied beside the original as
  `<campaign>_campaign_snow.xml.material`, with `t_xml_snow_mask` set to
  `terrain/campaigns/<map>/snow_mask.dds`. The corruption material stays shared.
- **Lighting**: every `.environment` that `environment_collection.xml` names
  (its `global_lighting`, every volume's `lighting`, and every `DEVASTATION`'s
  `lighting` in the newer files' `ENVIRONMENT_DEVASTATIONS` section) is copied to
  `Weather/campaign/<map>/` with its snow material swapped for the clone.
  Devastation lighting is renamed `<name>_<map>.environment`
  (`et_nagash_<map>.environment`); the rest keep their names. The
  `_environments` volumes point at these copies.
- The project's own Weather environment also gets the cloned snow material.

The files are found by the paths the map names, not by where they used to live,
so they follow the game's lighting as it moves between updates.

`lf_normal.png` is an ordinary RGB normal map, which Terry compiles to DXT5nm
(`..._normal.*` -> `dxt5n` in rules.bob). Checked against The Old World project
and its height TIFF: R and G follow the same signs and orientation as the DDS's
alpha and green channels (correlation with the height gradient 0.78 / 0.75),
B is Z, alpha is opaque.

`world_width` comes from `campaign_map_playable_areas` (`maxx - minx`; 961.3 for
Immortal Empires), so it is not a setting. Without a matching row it falls back
to the tree list header (The Old World's tree list says 1367.396, its .terry
says 1367.4), and failing that to height-map-width / 12 with a warning.

## Devastated maps

A campaign with a `terrain_location_devastate` (Immortal Empires:
`wh3_main_combi_map_devastate_1`) has a second, pieces-only map. For every event
area, it holds a copy of that area's bounding box as devastated (see
[docs/event-area-pieces.md](docs/event-area-pieces.md)). The *Devastated
project* step (`DevastatedProject`, off by default) turns it into a Terry project
of its own.

**It is built on the main project, which must already be on disk.** Decompile
the main map first, at least its terrain maps and the *Campaign Terry project*
step. Then run *Devastated project*, in the same run (it always goes last) or a
later one. Without the main project's `.terry` the step stops with an error; it
never writes a stand-alone devastated project.

The devastated project is named after `terrain_location_devastate`. When the map
is renamed, `_map` in the new name becomes `_map_devastate`. *Devastated name*
(`--devastated-name`) overrides either.

The step writes the whole project in one go. It is the vanilla map with every
area swapped for its devastate piece:

- **Terrain maps.** Each piece's DDS is a byte-exact crop of the map texture's
  top mip (checked on all 202 combi pieces). So the devastate pieces are pasted
  into copies of the vanilla DDS files block for block, and each map is compared
  with the vanilla one. The devastated project then lists the **main project's
  layers of that map, hard-linked** into its folder under its own file names,
  with one `devastation` layer on top that holds only what the pieces change
  (see the table below). Where a layer can't express the change, `devastation`
  is the whole devastated map, and the main project's layers under it are
  hidden `reference` layers.
- **Trees**: the vanilla tree list minus the trees inside the areas (those a
  vanilla piece lists, plus any standing on an area's mask pixels), plus the
  devastate pieces' trees.
- **Roads**: the roads each vanilla piece lists are taken out of `tile_list.bin`,
  and the devastate pieces' roads (the `roads_grey` set) are put in. A piece's
  road has no rotation, so it takes the rotation of the vanilla road it replaces.
  `tile_map.png` and `climate_map.png` are the devastated project's own.
- **Prop layers**: one layer per event area, named after its
  `campaign_map_event_areas` key. It holds the piece's `objects.bin` and
  `bmd_objects_sound.bin`.
  - Each devastation type's `objects_<type>.bin` and `bmd_objects_sound_<type>.bin`
    go into a logical layer named after the type, with that `bmd_export_type`
    (`devastation_chaos`, `devastation_nagash`, `devastation_skaven`).
  - The type's sound emitters sit in a `sound` logical layer nested inside it.
    The base sounds get the usual `sound` layers: top level, or nested in
    `uses_culture_mask` when culture-masked.
  - Culture masks come from the `.culture` file beside each `.bin`, except for
    terrain holes, whose masks can't be paired with their triangles (see
    `PieceCulture.HoleMasks`). A hole triangle the vanilla map also has takes its
    vanilla mask; the rest take the combined mask of their file's holes.
  - Only what the pieces hold is written. Outside the areas, the game keeps the
    vanilla map's props.
  - The exception is the map-wide `global_props_sound_<type>.bin` files. Almost
    every emitter in them is also in a piece. The few that aren't (5 on the combi
    map, on event-area borders) go into a `_global` layer, inside their type's
    logical layer.
  - The devastated project gets no **rivers**, like CA's devastate pieces. When
    an area swaps to a piece, the game deletes what stands in the area, except
    props whose material name contains `campaign_water_plane` or
    `campaign_blood_river`. So the map-wide rivers stay, and a river in a piece
    would be a second copy on top (see [docs/event-area-pieces.md](docs/event-area-pieces.md)).
  - The main project's rivers are river splines again (see [Rivers](#rivers)),
    so BOB keeps them out of the pieces and writes the devastate pieces' `rivers`
    records (the rivers that turn to lava, 25 on combi), which it does only for
    splines. While rivers were decompiled as props, BOB put the ones in an event
    area into the area's normal piece. Each time the area was restored, the game
    loaded that copy on top of the river that stayed, and a rebuilt map lost the
    `rivers` records.
- **Environment volumes** come from the devastate map's own
  `environment_collection.xml`, which has no devastation lighting.
- The project's `global_lighting` is the main project's Weather environment. The
  devastated map shares the main map's battle project.

Terry composites a map's layers bottom-up, with a fixed rule per map type
(`TerrainMapLayer::composite` in qttoolutility). The rule decides what a
devastation layer can express:

| Map | Rule | Devastation layer |
|---|---|---|
| height, sea height, shroud height | added | devastated − vanilla heights, 0 elsewhere |
| blend, event area mask | replaced where the layer isn't 255 | changed pixels, 255 elsewhere |
| colour overlays | **hard light**, not alpha-over (measured from Terry's export; see [docs/colour-overlay-blending.md](docs/colour-overlay-blending.md)) | changed pixels opaque, transparent elsewhere. **Wrong under hard light**: should be written whole |
| trees | replaced where the layer isn't 255 | **written whole**: 255 (no tree) is transparent, so a layer can't take away the main project's trees |
| corruption, snow | `dst + src·(1 − dst)`: can only raise | **written whole**: devastation lowers them almost everywhere it touches them |
| patch visibility | bitwise AND | none: blank, like the main project's |

- A map **written whole** has the whole devastated map as its `devastation`
  layer. The main project's layers stay under it as `reference` layers. They are
  hard-linked like the others, but hidden (`visible="0"`), so Terry and BOB skip
  them. They keep the undevastated map at hand: show one in Terry to compare. If
  the main map has more than one layer, each is named `reference <its name>`.
- A map the pieces don't change gets the main project's layers only. On combi,
  that's the event area mask. `lf_normal.png` is identical
  too, so it's a hard link to the main project's.
- A map the main project doesn't have is left out of the devastated project, and
  the log says which. It is never written whole in its place.
- The layers are built from the *vanilla compiled* maps, so they hold the
  devastation itself, and edits to the main project show through them. A map
  written whole doesn't let them through: its references are hidden.
- The sea and shroud height layers are exact to about 1 float ulp.
- The devastate map ships no logic map. The land craters are the devastated
  BC6H decode over vanilla's logic-map heights (what the main project has), in
  the blocks the pieces change. CA re-encoded every block of a piece, so most
  of a piece differs by BC6H noise alone: the delta is kept where its
  magnitude, blurred (sigma 2 texels), clears 0.02 to 0.05 (smoothstep). On
  combi, 1% of vanilla's own BC6H noise gets through that mask, real craters
  move 0.0003 RMS, and 85% of the changed texels keep a delta. The craters
  themselves keep BC6H's stepping. Sea height and shroud are plain differences.

Each run of the step reads the main project's `.terry` and links its layers as
they are then, including any layers added in Terry. It first removes the links
the previous run made. So after re-decompiling the main map, or after Terry has
saved it, run the step again.

Things to know about hard links:
- A hard link is a second name for the same file data (NTFS, same drive).
  Painting into a linked layer changes it in both projects, but only if the
  program writes into the existing file. A program that saves by writing a new
  file and renaming it over the old one gives that project its own copy again,
  without any warning.
- Where no link can be made, the file is copied and the log says so.
- Deleting either name leaves the other intact.

To compile the project back into pieces, BOB needs `export_devastation_pieces =
true` in the `[Terrain]` section of the campaign's `rules.bob` (Terry's
*Process Devastation Pieces* sets it itself).

## Verification

Every step was diffed against the Python reference output on
`wh3_main_combi_map_1` (Immortal Empires):

- **Layers**: all 639 regions, 501,031 entities, 9.1M numeric attributes —
  zero numeric deviations. (The entities are the same after the move to
  province layers; only their distribution over files changed.)
- **Catchment maps**: bit-identical PNGs.
- **Tile map / climate map**: bit-identical at the time; both have since moved
  off the reference's hardcoded palette onto the game's own colours (see
  difference 6 below), so they no longer match it pixel for pixel.
- **Tree map**: identical except 59 pixels caused by a reference bug (see below).
- **Blend TIFF**: indices byte-identical to the DDS payload.

## Intentional differences from the Python scripts (bug fixes)

1. **Culture masks with bit 63 set**: Python's `bin()` on a negative signed
   64-bit mask produced a garbled two's-complement culture list (219 entities on
   the combi map). The C# reads the actual mask bits (bit 63 = `*`).
2. **Spot lights**: the script reused stale position/rotation variables from a
   previous section; C# uses the spot light's own position and its
   quaternion-derived rotation (5 entities on the combi map).
3. **Sphere sound emitters**: the script wrote the literal text
   `{sound_emitter.radius}` into the XML; C# writes the actual radius.
4. **Tree map edge wrap**: trees just off the west map edge were painted on the
   east edge (PIL negative-index wraparound); C# clips them.
5. Booleans are written lowercase (`true`/`false`) as Terry itself writes them.
6. **Tile and climate map colours** come from the tile database's own
   `_settings.bin` rather than the palette the Python scripts hardcoded. That
   table had drifted: it was a mix of WH3 and Three Kingdoms category names, of
   which only 11 of 31 entries were both current and correct. `generic` - the
   whole landmass - was `#96AA64` where WH3 defines `#DFB491`, `canals` and
   `canals_links` were under Three Kingdoms' `canal`/`canal_links` names with
   unrelated colours, and 21 WH3 tile sets (`cliff_base`, `roads_grey`, the
   `river_mesh_*` and `river_lava_mesh_*` families, ...) were absent altogether
   and would have rendered magenta. The climate map is affected too: the only
   climate the campaign defines, `default`, was hardcoded to black and is
   `#B99A6B` in the file.

## Rivers

The compiled map keeps no river entities. BOB bakes each Terry river spline into
`models/river_<entity id>.wsmodel` and places it as a prop with the river
entity's transform. The Prop layers step turns every such prop back into an
`ECRiver` entity. Its spline is fitted to the baked mesh: each mesh row lies
within 0.05 of the spline, in position and in width, when BOB's own tessellation
rules are applied. The material comes from the `.wsmodel`, and the flags from
the prop. The bake keeps nothing of `flow_speed`, `alpha_fade`, `foam_amount` or
`terrain_offset`, so those get Terry's defaults. Point order follows the river's
flow (`reverse_direction="false"`). All 206 combi rivers convert (5351 points).

A river model that can't be read stays a prop, with a warning. A renamed map
then copies the models/ folder to `working_data/terrain/campaigns/<map>/models/`
as before. How the bake works, and the evidence, is written up in
[docs/river-splines.md](docs/river-splines.md).

## Prop layer grouping

The compiled map stores props per region, which is far more layers than Terry is
comfortable with, so the layers are grouped instead by the province each region
belongs to (`region_to_province_junctions`). On the combi map that turns 639
region layers into 214: 213 provinces plus one `_global` layer. The `_global`
layer is listed first in the .terry project, above the provinces, so it is easy
to find in Terry.

`_global` holds everything that belongs to no province:

- the 71 province-less regions - the seas, rivers, lakes and
  `wh_main_unknown_terrain` - merged together;
- every light probe on the map (20), which cover the map rather than a province;
- the water planes that make up the sea, including those a province layer would
  otherwise hold because the sea touches one of its regions;
- the `materials/full_shr.xml.material` meshes (10 on the combi map), which
  blank out the map's unreachable northern and southern border.

A water plane counts as sea when it sits at sea level (|y| <= 0.01) and either
is drawn where the rest of the map is not - `visible_in_tactical_view` and
`visible_in_shroud` both set - or has an outline area of 1000 world units
squared or more (`BmdPolyMesh.IsSeaWaterPlane`). The area test is there for the
handful of sea pieces whose two flags are off; the flags are there for the sea
pieces that are small, such as the three inside `the_galleons_graveyard`
(7, 172 and 289 units squared). Everything else is cosmetic water - a pond, a
waterfall pool - and stays in the layer it was found in, loose rather than in
the `water_planes` group when that layer is `_global`. On the combi map that
splits the 42 water planes the layer ends up holding into 34 sea and 8 cosmetic.

The sound emitters come from a second file, `global_props_sound.bin`. It uses the
same container as `global_props.bin` but has no root entry: every entry is a
quadtree tile named `bmd_objects.<region>.<n>.<depth>.bin`, holding nothing but
sound emitters and no culture mask of its own (the emitters carry their own). Each
tile's region picks the province layer its emitters join, exactly as for the props.
The shipped `global_props.bin` itself holds no sound emitters any more.

Entities that belong together are additionally collected into logical layers -
an `ECLayer` entity plus `Logical` associations - so they can be toggled as a
group in Terry. Membership is exclusive, and the layers are:

| Logical layer | Where | Contents |
|---|---|---|
| `uses_culture_mask` | every layer | everything with a non-empty culture mask (374k of the map's 502k entities: the corruption and faction variants of a prop); its sound emitters sit in a `sound` layer nested inside it (11,231 on the combi map, in 210 layers) |
| `sound` | every layer | the sound emitters (`ECSoundMarker`) without a culture mask (3,508 on the combi map, in 207 layers) |
| `light_probes` | `_global` | the light probes |
| `water_planes` | `_global` | the water planes that make up the sea (34 on the combi map) |
| `full_shr` | `_global` | the full-shroud border meshes (10 on the combi map) |

Every `sound` layer, nested or not, has an empty `bmd_export_type`. BOB names a
BMD after its export type and adds `_sound` for sound emitters by itself, so a
`sound` type compiles them into `global_props_sound_sound.bin` and
`bmd_objects_sound_sound.bin`, which the game never loads.

Culture masks are 64-bit masks whose bits come from the `prefab_types` DB table:
a row with a culture owns bit `value - 1` (`NONE`, value 0, wraps to bit 63 and is
written as `*`). The other rows (`BASE`, `BUILDING_*`, `ABANDONED`, ...) are prefab
states, not cultures, and are left out of `culture_mask`. A new culture adds a row
(`UNDEAD_LEGIONS`, value 36, bit 35), so the table is read, not recorded. Rows that
share a bit (`KISLEV` and `KISLEV_PROLOGUE`) cannot be told apart; the first by key wins.

One hardcoded extra, on by default (UI checkbox under Prop layers, CLI
`--no-custom-cultures` to turn it off): modded cultures listed in
`CultureMask.CustomCultures` follow one or more vanilla cultures, and a mask that
names *all* of them also names the modded culture. A single culture is a straight
alias (mixer_nag_nagash follows Undead Legions, ovn_troll follows Norsca, ...). A set
is a consensus: the order-faction mods (ovn_albion, mixer_nip_nippon, ...) follow
Empire + Dwarfs + High Elves + Cathay together, so they get generic good-guy
dressing (2,800 entities on the combi map) and not one faction's own props (the
978 dwarf-only ones, say). No entity on the shipped combi map carries the Undead Legions bit yet.

## Notes

- **Heights come from the logic map.** The heightmap compile writes the
  composited Height map twice. `full_height_map.dds` is BC6H, which snaps each
  4x4 block onto a few interpolated half-float levels: the plateaus.
  `full_logic_map.compressed_map` is 16 bits per texel at the same resolution,
  quantised linearly over the map's own range:

  ```
  v = trunc((h - lo) * (1 / (hi - lo)) * 65535)     float32; lo, hi = header floats 1 and 4
  ```

  One step is (hi - lo) / 65535: 0.0007 on IEE, 0.0008 on vanilla combi, 0.0015
  on The Old World. Its rows run south to north, the other way round from the
  DDS. The Height map step decodes each texel to the middle of its step, and the
  lowest and highest steps to lo and hi themselves, so the heights recompile to
  the same logic map (`LogicMapHeights`). Measured on IEE
  (`cr_combi_expanded_map_1`, 12800x7764) against its Terry source:

  | land height from | height RMS | height max | normal RMS | time |
  |---|---|---|---|---|
  | BC6H red channel | 0.0063 | 2.55 | 4.13 deg | seconds |
  | the former solve against `lf_normal.dds` | 0.0326 | 6.15 | 6.59 deg | 468 s, ~8 GB |
  | full_logic_map | 0.00021 | 0.00035 | 0.18 deg | 1.5 s to read |

  (Normals are the compile's own: 3x3 Sobel at spacing 4, before 8-bit and
  DXT5nm.) Recompiling the decoded heights gives a byte-identical
  full_logic_map on both IEE and The Old World. `lf_normal.dds` adds nothing,
  since the compile derives it from the same heights. The BC6H red channel is
  used only when the folder has no logic map of the height map's size, with a
  warning in the log.

  *Refine the heights with the normal map* (GUI, under Height map;
  `--refine-height`; off by default) then moves each texel within its step to
  fit `lf_normal.dds`, the compile's 3x3 Sobel of the same heights: on The Old
  World normal RMS 0.287 to 0.222 deg and height RMS 0.00039 to 0.00034, still
  recompiling to a byte-identical logic map, for about 2 more minutes (173 s
  for the whole step, 232M texels, 4 cores) and ~8 GB. It only helps where
  `lf_normal.dds` was made from the same heights; IEE's, for one, keeps
  vanilla's normals (of CA's own heights) in the vanilla areas.
- **The snow mask is stretched.** Terry's SnowMask is a quarter of the height map,
  like the tree and corruption maps (2880x1941 on combi), but `snow_mask.dds` is
  one pixel bigger each way, rounded up to whole BC4 blocks (2884x1944). BOB
  stretches the TIFF bilinearly over the same extent to get there; a TIFF that is
  already the compiled size goes through untouched. Decoding the DDS as it is
  gives a TIFF off Terry's grid: its pixels don't line up with the tree and
  corruption maps', and a snow layer from a quarter-size project pasted onto it
  pixel for pixel ends up to 4 pixels north-west of where it was (what happened
  to the IEE merge). The Snow mask step shrinks it back (`SnowMaskStretch`: a bilinear shrink plus
  one back-projection step). Measured on rob's old IEE project (3200x1941 TIFF,
  3204x1944 DDS), the stretch drifts from 0 at the north-west corner to 3.75
  pixels east and 3 south, and the un-stretch recovers the TIFF to 0.39 levels
  on average. The values are not otherwise changed.
- **Two height maps, one file.** `full_height_map.dds` is BC6H RGB and carries
  both surfaces: land in red, sea bed in green, blue unused. The Height map step
  writes both (`.height` and `.sea_height`), matching the pair CA's projects
  carry - on the combi map the sea bed runs -108.94 to 0.97 with most of it
  sitting at the -0.913 shelf. The land is read from the logic map instead (see
  *Heights come from the logic map*); the sea bed has no other copy and is
  written as decoded.
- 3K tile databases are supported via the Game setting (version quirk). Tile
  colours need no such setting: they are read from whichever `_settings.bin`
  ships with the tile database, so a title with different tile sets renders
  correctly without a code change.
- **Tree scatter.** BOB reads exactly one pixel per 2x2 cell of the tree map, so
  three quarters of a tree map never reaches `trees.campaign_tree_list` and a
  byte-exact decompile is impossible; the CampaignTree step instead reconstructs
  a map that recompiles to the same tree list, recovering the sampled pixel for
  100% of instances. The algorithm, the evidence, and the half-solved compile
  direction are written up in [docs/tree-scatter-algorithm.md](docs/tree-scatter-algorithm.md).

## License

[MIT](LICENSE)
