# The campaign and battle projects in detail

How each step reads the compiled map and what it writes. For using the tool, see
the [README](../README.md).

## Where the inputs come from

The files are read from the game's packs through the RPFM 5 server
(`rpfm_server.exe`, JSON over a WebSocket on `127.0.0.1:45127`). The tool starts
the server from the RPFM folder when none is running, and the server exits by
itself once the tool disconnects. Extracted files go to a scratch folder in
`%TEMP%` that is deleted after the run.

The `campaigns` DB table says where everything lives:

| campaigns column | Used for | Immortal Empires |
|---|---|---|
| `terrain_location` | `terrain/campaigns/<name>/`, the compiled terrain | wh3_main_combi_map_1 |
| `display_location` | `campaign_maps/<name>/display/trees/`, the tree list | wh3_main_combi_map_5 |
| `battle_path` | `terrain/battles/<name>/`, the catchment maps and battle project | wh3_main_combi_map |
| `terrain_location_devastate` | the devastated map, see [devastated-project.md](devastated-project.md) | wh3_main_combi_map_devastate_1 |

The names differ from each other, so they are looked up rather than guessed.
The tile database is always `terrain/tiles/campaign/_tile_database/`: its
`tiles/` folder holds the tile shapes, and its `_settings.bin` the tile set and
climate colours.

`world_width` comes from `campaign_map_playable_areas` (`maxx - minx`, 961.3 for
Immortal Empires). Without a matching row it falls back to the tree list header
(The Old World's tree list says 1367.396, its `.terry` says 1367.4), and failing
that to the height map's width / 12, with a warning.

With every input path overridden, the tool only contacts RPFM for the prop layer
step, which reads the province of each region from the DB.

## Steps

| Step | Input | Output |
|---|---|---|
| Layers | global_props.bin + global_props_sound.bin + models/ (baked river meshes) + region_to_province_junctions + prefab_types | `<map>.<id>.layer` per province, plus `_global`. Rivers become river splines |
| Height | full_logic_map.compressed_map (land) + full_height_map.dds (BC6H, sea bed) | `.height` + `.sea_height` 32-bit float TIFFs |
| HeightShroud | shroud_heights.dds (R32F) | `.height_shroud` float TIFF |
| Blend | global_map/global_blend.dds (L8) | `.blend` indexed TIFF |
| ColorOverlay | colour_overlay.dds (BC1) | `.color_overlay` RGBA TIFF |
| ColorOverlaySea | lf_sea_colour.dds (BC1) | `.color_overlay_sea` RGBA TIFF |
| SnowMask | snow_mask.dds (BC4) | `.snow_mask` gray TIFF, a quarter of the height map |
| CorruptionMask | corruption_mask.dds (R8) | `.corruption_mask` gray TIFF |
| EventAreaMask | event_area_mask.dds (R8) + campaign_map_event_areas | `.event_area_mask` indexed TIFF |
| Trees | trees.campaign_tree_list + campaign_tree_ids | `.tree` indexed TIFF |
| TileMap | tile_list.bin + tile database + _settings.bin | tile_map.png + climate_map.png |
| NormalMap | lf_normal.dds (DXT5nm) | lf_normal.png (RGB normal map) |
| EnvironmentVolumes | environment_collection.xml | `_environments` layer of lighting spheres and cylinders |
| Catchment | battle blm_*.compressed_map | blm_*.png in the battle project |
| TerryProject | everything above + the map's lighting | `<map>.terry` and the Weather `.environment` it points at |
| BattleProject | – | `<battle map>.terry` with placeholder maps |
| DevastatedProject (off by default) | the main project on disk + the devastated map | the devastated Terry project, see [devastated-project.md](devastated-project.md) |

The two project steps scan their output folders, so steps can be re-run one at a
time. For each terrain map type the newest TIFF wins, and for each layer name
the newest layer file. A step whose input is missing from the packs is skipped
with a note in the log.

## Project layout

Both projects are laid out like a campaign built by hand in Terry (the author's
The Old World map, `cr_oldworld_map_1`, is the model). The campaign project
holds `tile_map.png` and `lf_normal.png` next to its TIFFs. The battle project
holds the three catchment override PNGs and a skeleton `.terry` whose 2x2 zero
Height / HeightSea TIFFs and 4x4 single-colour `tile_map.png` /
`climate_map.png` are placeholders. Existing placeholders are never overwritten.

All TIFFs are LZW-compressed, with the bit depths and photometric layouts Terry
writes. Booleans are written lowercase, as Terry writes them.

### Indexed maps and their colour tables

- **Blend**: the colour table comes from the packs'
  `warscape_asset_variation_db/terrain_textures_campaign.assetdb.xml`. A
  `global_blend.dds` value is an index into that file's `campaign_base_colour`
  entries, in file order, and each entry's `display_r/g/b` is the colour Terry
  shows. The entries are sorted by name, so a game update that adds textures
  shifts every index after them. That is why the table is read from the game.
- **Event area mask**: every row of the `campaign_map_event_areas` DB table,
  sorted by key. `_empty` (black) is index 0 and fills the background. This
  matches the table of CA's own `wh3_main_combi_map_1` event area TIFF exactly.
- **Trees**: from the `campaign_tree_ids` DB table. Each tree id (a variant,
  `tree_large_01`) has a `colour_hex`, shared by all variants of a type, and the
  table is those colours sorted by RGB value. Every CA tree TIFF is laid out
  this way (the white last entry is `tree_large_burnt`). 255 means no vegetation.
- **Tile and climate maps**: the colours come from the `_settings.bin` that ships
  with the tile database, so a game with different tile sets renders correctly
  without a code change. Three Kingdoms tile databases are read with the Game
  setting.

### Patch visibility

The campaign project's PatchVisibilityMask is always blank (no layers). Only
Realms of Chaos uses patch visibility (compiled to `terrain_visibility_mask.dds`).
`patch_mask.dds` is a tile-map file and has nothing to do with it. Terry needs
the mask at its own patch-grid size, which it takes from the tile map (warscape's
`TERRAIN_DIMENSIONS::campaign_control_points`, about 128 patches across): 125x84
for combi's 2880x1941 tiles, 128x77 at 3200x1941. At any other size Terry shows
only the tiles in the global mesh (roads, cliffs, coast).

## Heights

The heightmap compile writes the composited Height map twice.
`full_height_map.dds` is BC6H, which snaps each 4x4 block onto a few
interpolated half-float levels. `full_logic_map.compressed_map` is 16 bits per
texel at the same resolution, quantised linearly over the map's own range:

```
v = trunc((h - lo) * (1 / (hi - lo)) * 65535)     float32; lo, hi = header floats 1 and 4
```

One step is (hi - lo) / 65535: 0.0007 on IEE, 0.0008 on vanilla combi, 0.0015 on
The Old World. Its rows run south to north, the other way round from the DDS.
The land heights are read from the logic map: each texel is decoded to the
middle of its step, and the lowest and highest steps to lo and hi themselves, so
the heights recompile to the same logic map (`LogicMapHeights`). Measured on IEE
(`cr_combi_expanded_map_1`, 12800x7764) against its Terry source:

| land height from | height RMS | height max | normal RMS |
|---|---|---|---|
| BC6H red channel | 0.0063 | 2.55 | 4.13 deg |
| full_logic_map | 0.00021 | 0.00035 | 0.18 deg |

(Normals are the compile's own: a 3x3 Sobel at spacing 4, before 8-bit and
DXT5nm.) Recompiling the decoded heights gives a byte-identical full_logic_map on
both IEE and The Old World. The BC6H red channel is used only when the folder
has no logic map of the height map's size, with a warning in the log.

**Refine the heights with the normal map** (GUI, under Height; `--refine-height`;
off by default) then moves each texel within its step to fit `lf_normal.dds`,
the compile's 3x3 Sobel of the same heights. On The Old World normal RMS drops
from 0.287 to 0.222 deg and height RMS from 0.00039 to 0.00034, still recompiling
to a byte-identical logic map. It takes about 2 more minutes and ~8 GB of memory,
and only helps where `lf_normal.dds` was made from the same heights (IEE's, for
one, keeps vanilla's normals in the vanilla areas).

**Sea bed.** `full_height_map.dds` is BC6H RGB and carries both surfaces: land in
red, sea bed in green, blue unused. The sea bed has no other copy and is written
as decoded to `.sea_height`. On the combi map it runs -108.94 to 0.97, with most
of it at the -0.913 shelf.

## Snow mask

Terry's SnowMask is a quarter of the height map, like the tree and corruption
maps (2880x1941 on combi), but `snow_mask.dds` is one pixel bigger each way,
rounded up to whole BC4 blocks (2884x1944). BOB stretches the TIFF bilinearly
over that extent; a TIFF already at the compiled size goes through untouched.
Decoded as it is, the DDS would be off Terry's grid: its pixels wouldn't line up
with the tree and corruption maps', drifting up to 4 pixels by the far corner.
The step shrinks it back (`SnowMaskStretch`: a bilinear shrink plus one
back-projection step). On a 3200x1941 TIFF compiled to 3204x1944, the stretch
drifts from 0 at the north-west corner to 3.75 pixels east and 3 south, and the
un-stretch recovers the TIFF to 0.39 levels on average.

## Normal map

`lf_normal.png` is an ordinary RGB normal map, which Terry compiles to DXT5nm
(`..._normal.*` -> `dxt5n` in rules.bob). Checked against The Old World project
and its height TIFF: R and G follow the same signs and orientation as the DDS's
alpha and green channels, B is Z, and alpha is opaque.

## Trees

BOB reads exactly one pixel per 2x2 cell of the tree map, so three quarters of a
tree map never reaches `trees.campaign_tree_list`. An exact copy of the original
TIFF can't be recovered. The step instead builds a map that recompiles to the
same tree list. See [tree-scatter-algorithm.md](tree-scatter-algorithm.md).

## Prop layers

The compiled map stores props per region, which is far more layers than Terry is
comfortable with, so they are grouped by the province each region belongs to
(`region_to_province_junctions`). On the combi map that turns 639 region layers
into 214: 213 provinces plus `_global`, which is listed first in the `.terry`.

`_global` holds everything that belongs to no province:

- the 71 province-less regions (the seas, rivers, lakes and
  `wh_main_unknown_terrain`), merged together;
- every light probe on the map (20 on combi);
- the water planes that make up the sea, including those a province layer would
  otherwise hold because the sea touches one of its regions;
- the `materials/full_shr.xml.material` meshes (10 on combi), which blank out the
  map's unreachable northern and southern border.

A water plane counts as sea when it sits at sea level (|y| <= 0.01) and either is
drawn where the rest of the map is not (`visible_in_tactical_view` and
`visible_in_shroud` both set) or has an outline area of at least 1000 square
world units (`BmdPolyMesh.IsSeaWaterPlane`). The area test catches the few sea
pieces with both flags off. The flags catch the small sea pieces, such as the
three inside `the_galleons_graveyard` (7, 172 and 289 square units). Everything
else is cosmetic water (a pond, a waterfall pool) and stays in the layer it was
found in. On the combi map, that splits `_global`'s 42 water planes into 34 sea
and 8 cosmetic.

Sound emitters come from `global_props_sound.bin`. It uses the same container as
`global_props.bin` but has no root entry: every entry is a quadtree tile named
`bmd_objects.<region>.<n>.<depth>.bin`, holding only sound emitters, which carry
their own culture masks. Each tile's region picks the province layer its emitters
join, as for the props.

### Logical layers

Entities that belong together are also collected into logical layers (an
`ECLayer` entity plus `Logical` associations), so they can be toggled as a group
in Terry. Membership is exclusive:

| Logical layer | Where | Contents |
|---|---|---|
| `uses_culture_mask` | every layer | everything with a non-empty culture mask (374k of the combi map's 502k entities: the corruption and faction variants of a prop). Its sound emitters sit in a `sound` layer nested inside it |
| `sound` | every layer | the sound emitters (`ECSoundMarker`) without a culture mask |
| `light_probes` | `_global` | the light probes |
| `water_planes` | `_global` | the water planes that make up the sea |
| `full_shr` | `_global` | the full-shroud border meshes |

Every `sound` layer, nested or not, has an empty `bmd_export_type`. BOB names a
BMD after its export type and adds `_sound` for sound emitters by itself, so a
`sound` export type would compile them into `global_props_sound_sound.bin` and
`bmd_objects_sound_sound.bin`, which the game never loads.

### Culture masks

Culture masks are 64-bit masks whose bits come from the `prefab_types` DB table:
a row with a culture owns bit `value - 1` (`NONE`, value 0, wraps to bit 63 and
is written as `*`). The other rows (`BASE`, `BUILDING_*`, `ABANDONED`, ...) are
prefab states, not cultures, and are left out of `culture_mask`. A new culture
adds a row (`UNDEAD_LEGIONS`, value 36, bit 35), so the table is read from the
game. Rows that share a bit (`KISLEV` and `KISLEV_PROLOGUE`) can't be told apart;
the first by key wins.

**Modded cultures** (on by default; a checkbox under Layers, or
`--no-custom-cultures`): the modded cultures listed in
`CultureMask.CustomCultures` follow one or more vanilla cultures, and a mask that
names all of them also names the modded culture. A single culture is a straight
alias (mixer_nag_nagash follows Undead Legions, ovn_troll follows Norsca, ...). A
set is a consensus: the order-faction mods (ovn_albion, mixer_nip_nippon, ...)
follow Empire + Dwarfs + High Elves + Cathay together, so they get generic
good-guy dressing (2,800 entities on the combi map), not one faction's own props.

### Rivers

The compiled map has no river entities. BOB bakes each Terry river spline into
`models/river_<entity id>.wsmodel` and places it as a prop with the river
entity's transform. The Layers step turns every such prop back into an `ECRiver`
entity, with a spline fitted to the baked mesh: each mesh row lies within 0.05 of
the spline, in position and in width, under BOB's own tessellation rules. The
material comes from the `.wsmodel` and the flags from the prop. The bake keeps
nothing of `flow_speed`, `alpha_fade`, `foam_amount` or `terrain_offset`, so
those get Terry's defaults. All 206 combi rivers convert (5351 points).

A river model that can't be read stays a prop, with a warning. A renamed map then
copies the models/ folder to `working_data/terrain/campaigns/<map>/models/`. How
the bake works is in [river-splines.md](river-splines.md).

## Lighting

The campaign project's `global_lighting` points at a Weather environment file
written for it, the way a hand-made project has one per map:
`working_data/Weather/default_battle_<campaign>.environment`. It is a copy of
`working_data/Weather/terry_default_battle.environment` whose empty
`post_processes` attribute is filled in from the map's own lighting: the tool
follows `environment_collection.xml`'s `global_lighting` to the `.environment` it
names and copies its `post_processes` (for Immortal Empires, the snow and
corruption overlay materials). Nothing else in the template is touched. The
lighting file is extracted from the packs wherever `global_lighting` points
(`Weather/campaign/combi/default.environment` for Immortal Empires); with an
explicit input folder, only a path inside the map folder can be found.

`environment_collection.xml` can end with an `ENVIRONMENT_DEVASTATIONS` section:
map-wide end-times lighting (`devastation_nagash` ->
`Weather/et_nagash.environment`, ...) that nothing else references. These go into
the `.terry`'s `devastation_light_environments` as `name=path` pairs, comma
separated and sorted by name.

### Environment volumes

`environment_collection.xml` also holds the map's environment volumes: the
spheres and cylinders that fade the lighting over to a local `.environment`
between an inner and an outer radius (Skavenblight's green murk, Cathay's haze).
They get a layer of their own, `_environments`, with one entity per volume
carrying `ECEnvironmentVolume` plus `ECDoubleSphere` or `ECDoubleCylinder`,
collected into logical layers by environment name. The combi map has 66 of them
(1 sphere, 65 cylinders) across 18 environments.

A cylinder's `height` doesn't survive compilation and is written as 0, the value
205 of The Old World's 247 cylinders carry. Position and both radii come through as they are.
The `lighting_file` paths keep pointing at the source map's lighting, which Terry
resolves from the packs whatever the new map is called, unless the campaign is
cloned.

## Cloned campaigns

Giving the campaign a new name makes the map a clone, with its own copies of the
files vanilla would otherwise share with it. They are written to the Assembly
Kit's `working_data`, under their pack paths:

- **Sea material**: the material the sea water planes share is copied unchanged
  to `materials/environment/campaign_sea/<campaign>_campaign_water_plane.xml.material`
  (the `.terry`'s water plane material), and every sea water plane in `_global`
  points at it.
- **Snow material**: the snow entry in the `post_processes` of the `.environment`
  named by `global_lighting` is copied beside the original as
  `<campaign>_campaign_snow.xml.material`, with `t_xml_snow_mask` set to
  `terrain/campaigns/<map>/snow_mask.dds`. The corruption material stays shared.
- **Lighting**: every `.environment` that `environment_collection.xml` names (its
  `global_lighting`, every volume's `lighting`, and every devastation's
  `lighting`) is copied to `Weather/campaign/<map>/`, with its snow material
  swapped for the clone's. Devastation lighting is renamed
  `<name>_<map>.environment` (`et_nagash_<map>.environment`); the rest keep their
  names. The `_environments` volumes and `devastation_light_environments` point
  at these copies.
- The project's own Weather environment also gets the cloned snow material.

The files are found by the paths the map names, so they follow the game's
lighting wherever an update moves it.
