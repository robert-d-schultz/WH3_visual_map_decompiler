# The devastated project in detail

A campaign with a `terrain_location_devastate` (Immortal Empires:
`wh3_main_combi_map_devastate_1`) has a second, pieces-only map. For every event
area it holds a copy of that area's bounding box as devastated (see
[event-area-pieces.md](event-area-pieces.md)). The *Devastated project* step
(`DevastatedProject`, off by default) turns it into a Terry project of its own,
named after `terrain_location_devastate`. When the map is renamed, `_map` in the
new name becomes `_map_devastate`. *Devastated name* (`--devastated-name`)
overrides either.

The project is built on the main project, which must already be on disk: at
least its terrain maps and its `.terry`. Without the main project's `.terry` the
step stops with an error. It never writes a stand-alone devastated project.

The step writes the whole project in one go. It is the vanilla map with every
area swapped for its devastate piece.

## Terrain maps

Each piece's DDS is a byte-exact crop of the map texture's top mip (checked on
all 202 combi pieces). So the devastate pieces are pasted into copies of the
vanilla DDS files block for block, and each map is compared with the vanilla one.
The devastated project then lists the **main project's layers of that map,
hard-linked** into its folder under its own file names, with one `devastation`
layer on top that holds only what the pieces change. Where a layer can't express
the change, `devastation` is the whole devastated map, and the main project's
layers under it are hidden `reference` layers.

Terry composites a map's layers bottom-up, with a fixed rule per map type
(`TerrainMapLayer::composite` in qttoolutility). The rule decides what a
devastation layer can express:

| Map | Rule | Devastation layer |
|---|---|---|
| height, sea height, shroud height | added | devastated − vanilla heights, 0 elsewhere |
| blend, event area mask | replaced where the layer isn't 255 | changed pixels, 255 elsewhere |
| colour overlays | **hard light**, not alpha-over (see [colour-overlay-blending.md](colour-overlay-blending.md)) | changed pixels opaque, transparent elsewhere. **Wrong under hard light**: should be written whole |
| trees | replaced where the layer isn't 255 | **written whole**: 255 (no tree) is transparent, so a layer can't take away the main project's trees |
| corruption, snow | `dst + src·(1 − dst)`: can only raise | **written whole**: devastation lowers them almost everywhere it touches them |
| patch visibility | bitwise AND | none: blank, like the main project's |

- A map **written whole** has the whole devastated map as its `devastation`
  layer. The main project's layers stay under it as `reference` layers. They are
  hard-linked like the others, but hidden (`visible="0"`), so Terry and BOB skip
  them. Show one in Terry to compare with the undevastated map. If the main map
  has more than one layer, each is named `reference <its name>`.
- A map the pieces don't change gets the main project's layers only. On combi,
  that's the event area mask. `lf_normal.png` is identical too, so it's a hard
  link to the main project's.
- A map the main project doesn't have is left out of the devastated project, and
  the log says which. It is never written whole in its place.
- The layers are built from the vanilla compiled maps, so they hold the
  devastation itself, and edits to the main project show through them. A map
  written whole doesn't let them through: its references are hidden.
- The sea and shroud height layers are exact to about 1 float ulp.
- The devastate map ships no logic map. The land craters are the devastated
  BC6H decode over the main project's logic-map heights, in the blocks the pieces
  change. CA re-encoded every block of a piece, so most of a piece differs by
  BC6H noise alone. The delta is kept where its magnitude, blurred (sigma 2
  texels), clears 0.02 to 0.05 (smoothstep). On combi, 1% of vanilla's own BC6H
  noise gets through that mask, real craters move 0.0003 RMS, and 85% of the
  changed texels keep a delta. The craters themselves keep BC6H's stepping.
  Sea height and shroud are plain differences.

## Trees and roads

- **Trees**: the vanilla tree list minus the trees inside the areas (those a
  vanilla piece lists, plus any standing on an area's mask pixels), plus the
  devastate pieces' trees.
- **Roads**: the roads each vanilla piece lists are taken out of `tile_list.bin`,
  and the devastate pieces' roads (the `roads_grey` set) are put in. A piece's
  road has no rotation, so it takes the rotation of the vanilla road it replaces.
  `tile_map.png` and `climate_map.png` are the devastated project's own.

## Prop layers

One layer per event area, named after its `campaign_map_event_areas` key. It
holds the piece's `objects.bin` and `bmd_objects_sound.bin`.

- Each devastation type's `objects_<type>.bin` and `bmd_objects_sound_<type>.bin`
  go into a logical layer named after the type, with that `bmd_export_type`
  (`devastation_chaos`, `devastation_nagash`, `devastation_skaven`).
- The type's sound emitters sit in a `sound` logical layer nested inside it. The
  base sounds get the usual `sound` layers: top level, or nested in
  `uses_culture_mask` when culture-masked.
- Culture masks come from the `.culture` file beside each `.bin`, except for
  terrain holes, whose masks can't be paired with their triangles (see
  `PieceCulture.HoleMasks`). A hole triangle the vanilla map also has takes its
  vanilla mask; the rest take the combined mask of their file's holes.
- Only what the pieces hold is written. Outside the areas, the game keeps the
  vanilla map's props.
- The map-wide `global_props_sound_<type>.bin` files are the first exception.
  Almost every emitter in them is also in a piece. The few that aren't (5 on the
  combi map, on event-area borders) go into a `_global` layer, inside their
  type's logical layer.

### Rivers

The rivers are the other exception. The devastated project gets every river of
the vanilla map, as river splines, with the material CA gave it in the devastated
map. The devastate map's `models/` folder holds all of the vanilla map's rivers
under the same names, with the same meshes (206 on combi; 127 of the files
differ, in two header padding bytes only), 26 of them with
`cwb_campaign_river_lava` instead of water. Each river goes into the layer of the
event area its position is in (BOB's rule, `DevastatedMap.MaskPixelAt`), or into
`_global` (20 on combi).

Only the lava ones change the build. BOB lists a river spline whose material
contains `cwb_campaign_river_lava` in the `rivers` file of the devastate piece
its position is in (25 on combi, in 12 `_fire` areas; the 26th lies outside every
area). While an area is devastated, the game turns the live river at each listed
x, z to lava. Without these rivers a rebuilt map has no `rivers` files, and no
river turns to lava. BOB keeps river splines out of the pieces' objects, so the
other rivers change nothing in the build; they are there because CA's project
has them.

Which rivers are lava is CA's choice: of the 186 rivers whose position is in an
event area, the 25 in fire areas are, and the 32 other rivers in fire areas and
every river in a chasm or crater area are not. BOB and the game don't look at the
area's type, so a lava river in a chasm area would turn to lava there too.

A river must not be in a piece as a prop. When an area swaps, the game deletes
what stands in it except props whose material name contains
`campaign_water_plane` or `campaign_blood_river`, so the map-wide rivers stay,
and a river prop in a piece would be a second copy on top of them.

## Other

- **Environment volumes** come from the devastate map's own
  `environment_collection.xml`, which has no devastation lighting.
- The project's `global_lighting` is the main project's Weather environment. The
  devastated map shares the main map's battle project.

## Hard links

Each run of the step reads the main project's `.terry` and links its layers as
they are then, including any layers added in Terry. It first removes the links
the previous run made. So after re-decompiling the main map, or after Terry has
saved it, run the step again.

- A hard link is a second name for the same file data (NTFS, same drive).
  Painting into a linked layer changes it in both projects, but only if the
  program writes into the existing file. A program that saves by writing a new
  file and renaming it over the old one gives that project its own copy again,
  without any warning.
- Where no link can be made, the file is copied and the log says so.
- Deleting either name leaves the other intact.

## Compiling

To compile the project back into pieces, BOB needs `export_devastation_pieces =
true` in the `[Terrain]` section of the campaign's `rules.bob` (Terry's *Process
Devastation Pieces* sets it itself).
