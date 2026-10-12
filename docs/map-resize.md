# Scaling and padding the map

The *Resize the map* options (`--scale <factor>` and `--pad left,right,top,bottom`
on the command line) scale the map, then add to its edges. Both count on the
campaign hex grid, whose size comes from `tile_list.bin`'s header (1440x970 on
Immortal Empires). The GUI reads every campaign's grid when it loads the campaign
list, and shows what the scale snaps to and the final grid as you type.

## The hex grid

The hex grid sits on square pixels: a hex is 2x2 tile map pixels, and the columns
are staggered, which makes every map half a hex taller than its rows (tile map
2880x1941 = 2 x 1440 by 2 x 970.5). Every terrain map has its own pixels per hex:

| Maps | Pixels per hex (Immortal Empires) |
|---|---|
| height, sea height, blend, colour overlays, lf_normal (11520x7764) | 8 |
| shroud heights (5760x3882) | 4 |
| tile, climate, tree, corruption, event area, snow (2880x1941) | 2 |

## Scale

The scale is uniform. Terry takes at most 2048 hexes each way and an even number
across, so the factor is snapped: the width becomes the even number of hexes
nearest width x factor, and that fixes the factor actually used (1.25 on
Immortal Empires gives 1800 hexes, exactly 1.25; 1.001 gives 1442, 1.00139). The
rows follow as round(factor x (rows + 1/2) - 1/2), 1213 at 1.25.

The half hex of the staggered columns doesn't scale in whole rows, so the scaled
map generally ends up to half a hex short of, or past, the grid's north edge
(0.375 hexes short at 1.25). The world's origin is the south-west corner, so that
sliver goes at the north edge: repeated from the map's edge, or cropped. A factor
that would exceed 2048 hexes is capped.

Every map keeps its pixels per hex, so hexes and texels keep their world size,
and the world scales with them about the origin:

- **Maps**: indexed maps (blend, event area, tree, tile, climate) and the
  hard-edged devastation layers are resampled nearest-neighbour. Heights, colour
  overlays, snow and corruption masks and lf_normal are bilinear when enlarging
  and averaged when shrinking. Heights are multiplied by the factor too, which
  keeps lf_normal valid as it is (the normal map's X and Y are resampled, and Z
  rebuilt). Forests keep their density, since the trees themselves don't scale.
- **Props, VFX and composite scenes**: position, and the transform's scale.
- **Everything without a transform scale** has its own sizes scaled instead:
  light probe and environment volume radii, point light radius, spot light
  length, sound emitter spheres, lines and point clouds, and the outlines of
  terrain holes and polygon meshes (water planes included).
- **world_width** is multiplied by the factor.

## Padding

Padding then adds hexes of the scaled grid to each side. Left + right must keep
the width even. Every map grows by its pixels per hex. The new pixels repeat the
map's edge, except on the tree map, where the new ground starts with no trees.
world_width grows by the same proportion. Padding the left or bottom (south)
moves every prop and environment volume east or north with the map. The sea water
planes and full-shroud meshes don't grow over the padding.

The patch visibility mask isn't resized: it is written blank at Terry's
patch-grid size for the resized tile map (see
[campaign-project.md](campaign-project.md#patch-visibility)).
