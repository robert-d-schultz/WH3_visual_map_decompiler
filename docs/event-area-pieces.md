# Event-area pieces: what is known

Notes on the per-event-area "pieces" added by the 2026 game update, in
`terrain/campaigns/wh3_main_combi_map_1/pieces/` and the pieces-only map
`terrain/campaigns/wh3_main_combi_map_devastate_1/`, September 2026. Worked out
from a full extraction of both maps (202 pieces each). The tool uses them to
build the devastated project (the Devastated project step, see the README's "Devastated maps").

## What a piece is

A piece is a self-contained copy of one event area's bounding box. It holds every
terrain texture cropped to that box, and that area's props, sounds, trees and
road tiles. The engine can swap a piece into the live map at runtime, which is
presumably how one area becomes "devastated" while the rest of the map stays as
it is.

- A piece folder is named `event_<colour_hex>`, the area's `colour_hex` in
  `campaign_map_event_areas` (see `Imaging/EventAreaPalette.cs`). For example,
  `event_0006dc` is `wh3_main_combi_province_helspire_mountains_crater`.
- `wh3_main_combi_map_1` has the full map plus a piece per area. The piece
  textures are exact crops of the full-size map textures.
- `wh3_main_combi_map_devastate_1` has **only** pieces: the same 202 areas with
  the same bounding boxes, plus a few map-level files. It is reached through
  the new `terrain_location_devastate` column of the `campaigns` table. Its
  `event_area_mask.dds` and `models/` hold the same file names as the normal
  map's. The mask is byte-identical; some of the river models differ.

Map-level files that go with the pieces:

| File | Map | Contents |
|---|---|---|
| `event_area_mask.dds` | both | R8, 2880x1941. The pixel is the index of the area's row in all `campaign_map_event_areas` rows, sorted by key |
| `event_tiles` | both | text: one tile folder per line (92 roads/roads_light in normal, 48 `roads_grey` in devastate). `tile_list` indexes it |
| `event_trees` | both | text: one full tree name (with variant suffix) per line (192 in normal, 186 in devastate, which adds `tree_cwb_burn_01..04`). `tree_list` indexes it |
| `event_vfx` | normal only | text: 35 VFX names. **Not an index.** See below |
| `global_props_sound_devastation_{chaos,nagash,skaven}.bin` | devastate | global_props-style container (name → offset table). The game never reads it: it loads `global_props[_<type>].bin` and `global_props_sound[_<type>].bin` only from the main map's folder (Warhammer3.exe RVA 0x2720d78) |

## Files in a piece

Every piece has the textures, `mask`, `texture_info`, `objects.bin/.culture`
and `bmd_objects_sound.bin/.culture`. Seven have no `tile_list`, because they
contain no roads. Devastate pieces add `objects_devastation_{chaos,nagash,skaven}`
and `bmd_objects_sound_devastation_*` (.bin + .culture), except in 2 pieces. 12
devastate pieces also have a `rivers` file.

Between the normal and the devastate copy of a piece, over all 202:
- **Identical:** `lf_normal`, `tile_mask`, `mask`, `texture_info`.
- **Different:** `full_height_map` (craters), `global_blend`, `corruption_mask`,
  `snow_mask`, `shroud_heights`, `colour_overlay`, `lf_sea_colour` (one block),
  `tree_list`, `tile_list`, objects, sounds.

Every normal piece's textures are byte-exact crops of the vanilla map's top mip
at the rectangle `texture_info` gives (all 202 pieces, all 9 textures), and where
devastate pieces overlap they agree. So a piece is applied by copying its top
mip's rows back into the map texture, with no decoding; four pieces run past the
map's right or bottom edge and are clipped.

### `mask`

One bit per event-mask pixel over the piece's bounding box: set where the pixel
belongs to this area.

- The bits are packed LSB-first in row-major order. Row 0 is the bounding box's
  top row in the DDS's own row order, and no flip is applied.
- The total size is padded to a multiple of 4 bytes.
- The bounding box is the tightest box around the area's index.
- Verified on all 202 pieces: every bit equals `event_area_mask[pixel] == area index`.

### `texture_info`

Plain text: the table for copying each piece texture back into its full-size
map texture.

```
mask 2880 1941 782 170 348 152                  event-mask W H, then bbox x y w h (event-mask pixels)
full_height_map.dds 1 1                         <file> <mip count> <flipped>
3128 6476 1392 608 16 348 152 148               one line per mip:
lf_sea_colour.dds 10 0                            srcX srcY srcW srcH
3072 640 1472 704 8 368 176 148                   bytesPerBlock(or per pixel) blocksW blocksH
1536 320 736 352 8 184 88 518292                  byte offset of the mip in the piece file
...
```

- The source rectangle is in the full texture's own resolution:
  - x4 the event mask for `lf_*`, `colour_overlay`, `full_height_map` and
    `global_blend`
  - x1 for `tile_mask` and `corruption_mask`
  - x2 for `shroud_heights`
- `flipped = 1` (the height map and tile mask) means the full texture is stored
  bottom-up, so the source Y is `H - y - h`: 6476 = 7764 - 680 - 608, and
  1619 = 1941 - 170 - 152.
- Block-compressed textures get their rectangle widened to a 64-pixel grid (3128
  becomes 3072). Each mip halves it.
- Offsets start at 148 when the DDS has a DX10 header and at 128 without one.
  Verified on all pieces: the last mip's offset plus its size equals the file
  size. So a piece DDS is a raw crop with a full mip chain.

### `tile_list`

```
u32 1                        always 1 (version?)
repeat:  u16 x, u16 y, u32 index into event_tiles
```

x and y are tile-grid cells in `tile_mask` space, bottom-up. The list holds
**roads only**:
- The base tiles live in the piece's `tile_mask.dds`, which is identical in the
  normal and devastate pieces.
- The road layer is kept separate so that it can change style: devastate
  indexes the `roads_grey` variants of the same pieces.
- Every road a normal piece lists is in the vanilla `tile_list.bin` at the same
  position (18,131 of 18,131). A piece's road has no rotation, though vanilla
  roads are rotated (direction byte 16/32/64/128), so a devastated road takes the
  rotation of the vanilla road at its position.

### `tree_list`

16-byte records, no header:

```
f32 x, f32 y (elevation), f32 z, u8 IsRemovable (always 1), u8 RotationIndex (0-5), u16 index into event_trees
```

These are the same world coordinates as `trees.campaign_tree_list` (961.3 x
748.62 on combi; `TreeListRenderer.cs`), with the type moved to an index and no
season mask. Every tree index stays within the list.

The normal pieces' trees are the vanilla tree list's trees inside the areas: of
189,718, 184,062 match a vanilla tree exactly and most of the rest differ only in
elevation (the shipped tree list predates the pieces). No tree is in two pieces.
Which piece a tree goes into is not quite the event-mask pixel under it, nor the
tree-map pixel it was scattered from (each misses 1-2%).

### `objects.bin`, `bmd_objects_sound.bin` (+ `_devastation_*`)

A single bare `FASTBIN0` BMD body. This is **not** the `global_props.bin`
container: there is no name → offset table and no per-region child entries,
because everything in the area is flattened into one BMD. Prop records are
version 30. `GlobalPropsParser.ParsePiece` reads all 2,008 of them (both maps)
without error.

The culture-mask field inside the .bin is a placeholder (`1`): none of the real
masks occur anywhere in the .bin. They live in the `.culture` file next to it.

### `.culture`

The culture masks for the matching `.bin`, one per entity in file order,
grouped by entity kind:

```
repeat:  u32 count, char[4] tag (NUL-padded), count x u64 mask
```

| Tag | Section |
|---|---|
| `p` | props (the count equals the prop count in the .bin) |
| `v` | VFX |
| `lp` | point lights |
| `ls` | spot lights (5 on the whole map) |
| `m` | poly meshes |
| `ht` | terrain holes (one per Terry hole entity, not per triangle; see below) |
| `sc` | composite scenes |
| `ss` | sound emitters (only in `bmd_objects_sound*.culture`) |

Empty sections are left out. The masks are the same 64-bit culture masks as in
`global_props.bin` (`Bmd/CultureMask.cs`). They are probably the already
resolved ones: with no parent entry left, there is nothing to inherit from.

Every section's count matches its entities except `ht`. The .bin holds hole
*triangles*, each once, while `global_props.bin` repeats a triangle for every
culture it is cut for (19,147 triangles, 6,209 distinct). `ht` has one mask per
hole entity, so its count falls between the number of hole polygons and the
number of triangles (event_ffdf54: one hexagon of 4 triangles, 2 masks), and
which triangle belongs to which mask is lost. Every hole triangle of the normal
pieces is one of the vanilla map's; only 560 of the devastate pieces' 16,121 are.

### `rivers` (12 devastate pieces only)

16-byte records: `f32 x, f32 y, f32 z, u32 0` (always 0). Each record is the
exact position of one river prop in the vanilla `global_props.bin` (a
`models/river_*.wsmodel`, the baked mesh of a Terry river spline): all 25
records land on a river prop's origin. A river is listed in the piece whose area
contains its origin. There is no name or id; the position is the reference.

The record order means nothing, and no file indexes into the list. It is not
sorted by river id, and it is not the order of `global_props.bin` or of the Terry
layer. The same five rivers of IEE's `event_c14516`, in the same layer and entity
order in both projects, come out in different orders in the main and devastate
builds.

The listed rivers are exactly the ones that turn to lava. How the game swaps an
area, read from Warhammer3.exe (September 2026 build; the swap is the function at
RVA 0x2711250):

1. **Clear the area** (RVA 0x2734fd0). For each of nine entity kinds (props,
   VFX, lights, meshes, holes, scenes, sounds, ...) it deletes every live entity
   whose position is inside the area. The test (RVA 0x27235cc) looks up the
   position's bit in the piece's `mask`. The exception is a prop whose material
   name contains `campaign_water_plane` or `campaign_blood_river` (a substring
   test, so a cloned `cr_combi_expanded_campaign_water_plane` counts too). Such
   a prop is kept, and its x, z and id go on a list of candidate rivers.
2. **Load the piece** (RVA 0x272413c): `objects` plus `objects_<type>` for a
   devastation, and the sounds. It adds everything in them. Nothing is skipped
   for being a copy of a river that was kept.
3. **Claim the rivers**, on the area's first swap only. It reads `rivers`. For
   each candidate, it looks for a record whose x and z are within 0.0001
   (|dx| + |dz|; the height is ignored). A matched river joins the area's list
   with its current material.
4. **Set the material**. While the area is devastated, every river in its list
   gets the material hardcoded in the exe,
   `materials/environment/campaign/cwb_campaign_river_lava.xml.material`.
   Otherwise each gets its own material back.

So a record is found by its position alone, and nothing selects a model. The
geometry stays the vanilla river's. The devastate map's `models/` folder holds
all 206 rivers under the same file names (127 re-baked over the craters, and
the same 26 with the lava material), but nothing shipped uses it. The 26 lava
ones are the 25 listed, plus `river_17eddb13a82fedf` at (418, 78), which
starts outside every area. BOB's apparent rule is a river spline with that lava
material whose origin is in the area: a main project that has such rivers gets
`rivers` files in its normal pieces too.

What follows from it:
- A river prop only survives a swap if its material is a water or blood river
  one. Any other river in an area is deleted with the area's props, and nothing
  brings it back unless the piece being loaded holds it. That includes combi's
  one `wh_campaign_lava` river, which is listed but never a candidate.
- The rivers themselves are never in a piece's `objects.bin`, in either map.
  The normal pieces' props are exact copies of the map-wide props in the area;
  the river props stay map-wide only. A river prop in a piece would be added on
  top of the one that was kept. It lasts until the area's next swap, because
  both swaps first unload what the area's previous piece brought in (RVA
  0x27336e0).
- Un-devastating is real. When an area drops out of the campaign's active
  event areas (script `deactivate_campaign_map_event_area`, console
  `event_area_deactivate`), RVA 0x2711428 queues the normal map's piece
  (callback RVA 0x27338e8). That callback unloads the devastate piece and
  loads the normal piece's objects, sounds and trees. It doesn't clear the
  area first, so the normal piece is what brings back the props the
  devastation deleted.

## `event_vfx`

`event_vfx` is not an index into anything: pieces store VFX names inline in
their .bin files. It is a preload list. At campaign load (Warhammer3.exe RVA
0x27206d4) the game asks the VFX system to load each name, and throws the
result away. If the file is missing, it skips that step. The piece loader loads
anything that isn't loaded yet itself, so a missing name only costs a load at
swap time. Nothing in the modder Assembly Kit writes the file: BOB's
devastation-pieces job writes `event_tiles` and `event_trees`, but no AK file
contains `event_vfx`.
- 33 of its 35 names occur in some piece .bin. 24 of those appear **only** in
  the devastate pieces (almost all in `objects_devastation_*`), and none appear
  only in the normal pieces.
- The other two, `wh3_dlc29_undead_campaign_ghosts` and
  `wh3_main_campaign_black_smoke`, are used by no piece.
- Many VFX that the normal pieces do use (`poison_river_blood`, for example) are
  not listed. The list is roughly the VFX the swap can *introduce*, but not
  exactly: the devastate pieces use 29 VFX that no map-wide prop does, and 4
  of them are missing from the list (`wh3_dlc23_campaign_enviro_smoke_embers`,
  `wh3_main_campaign_enviro_siege_smoke`, `wh_main_campaign_raze_smoke`,
  `wh_main_generic_fire_smoke_loop`). Meanwhile 10 listed names are
  already map-wide or used by no piece.

## Still unknown

- What the `tile_list` header's `1` means.
- BOB's exact rule for which piece a tree goes into.
