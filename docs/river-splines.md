# River splines

A compiled campaign map has no rivers, only props. BOB bakes every Terry river
(an `ECRiver` entity with an `ECRiverSpline`) into a mesh,
`terrain/campaigns/<map>/models/river_<entity id>.wsmodel` plus its
`.rigid_model_v2`. It then places the mesh in `global_props.bin` as a prop whose
transform is the river entity's own. The Prop layers step turns those props back
into river entities. It fits a spline to each baked mesh
(`Rivers/RiverMesh.cs`, `Rivers/RiverSplineFitter.cs`, `Rivers/RiverModels.cs`).

Ground truth was IEE's `cr_combi_expanded_map_1`. The AK has the Terry project in
`raw_data` and what BOB made of it in `working_data`, so all 60 river splines
pair with their baked meshes by entity id. The bake was also read in the AK
binaries (build of 2026-09-19).

## How BOB bakes a river

`bob_terrain.modder.x64.dll` (fn RVA 0x2ddf0) calls
`QTU::to_warscape_spline` (qttoolutility) and then
`TOOLDATABUILDER::process_river_spline` (tooldatabuilderdll). That calls
`WARSCAPE::tessellate_spline` (warscape, RVA 0x72c100), which writes the rmv2.

- **Segments.** RVA 0x710980 turns each segment into a 56-byte record: the four
  Bézier control points `p[i]`, `p[i] + tangent_out[i]`,
  `p[i+1] + tangent_in[i+1]`, `p[i+1]`, then the widths `w0` and `w1`. That is
  everything the mesh is built from. `flow_speed`, `alpha_fade`,
  `foam_amount` and `terrain_offset` never reach it. bob_terrain does
  serialise a `{vertex, width, flow_speed}` record per point, but that record
  isn't the mesh's input.
- **Width** is clamped to at least 0.01. It follows a cubic Bézier with control
  values `w0, 0.75·w0 + 0.25·w1, 0.25·w0 + 0.75·w1, w1`, which is
  `w0 + (w1 − w0)·(0.75·t + 0.25·smoothstep(t))`. On IEE every varying-width
  segment fits this within 0.0003; the ones starting at width 0 fit only with the
  clamp.
- **Rows.** The mesh is a grid of cross-section rows. Each row is a horizontal
  line square to the spline (offset along `(−dz, 0, dx)`), `width` long and
  centred on the curve. All rows of a river have the same number of vertices:
  2 when the river is narrow, more when it is wide. Rows are evenly spaced in
  arc length, `ceil(length / 0.2)` steps, and lie on the Bézier to within 3 mm.
- **UV.** u runs across a row, ±width/20. v runs along the river: arc length/10,
  0 at the start. `reverse_direction` only flips the UVs: v starts at the far
  end and u is mirrored, and the geometry is unchanged. So a reversed spline
  bakes to the same mesh as the unreversed spline with its points in reverse
  order. The final uv is the tessellator's uv times a per-spline scale
  (`DYNAMIC_SEGMENTED_SPLINE + 0x18`). For campaign rivers that comes out as the
  tenth above.
- **Vertices** are 48 bytes: position (4 floats, w = 1), uv, uv2 (always 0),
  normal, tangent and bitangent (4 bytes each), colour (always 0 in the file).
  The file is RMV2 v8 with one LOD and one mesh named `River`. Vertices are
  stored relative to the bounding box centre, in the river entity's unrotated
  frame. The prop's transform supplies rotation and position. Vanilla combi's
  206 river models have the same layout.
- **Prop flags.** The prop's visibility and campaign flags (tactical view,
  shroud, no culling, culture mask) are the entity's, 1:1 on all 60 IEE rivers.

## Reading it back

`RiverMesh.ReadRows` groups the vertices by v and orders the groups by
increasing v, so the river keeps its flow direction. Each row's two end
vertices give its centre and width. A mesh whose u span doesn't match its width
is rejected, and its prop stays a prop.

`RiverSplineFitter.Fit` places the spline's points on rows:

1. Start with the first and last row.
2. Repeatedly add the worst-fitting row of every segment, until each row is
   within the tolerance (default 0.05) of the curve, in position and in width.
3. Drop every point the tolerance does without.

A point's handles lie along the mesh's own direction at that row. Each is a third
of the chord to the neighbouring point long. So every segment depends only on its
two points, and the curve is smooth through each point. The first point's
in-handle mirrors its out-handle and the last point's out-handle is zero, as in
the splines Terry saves. Terry stores whatever handles it is given, and BOB bakes
them as stored. Only about 60% of IEE's interior points have Terry's
Catmull-Rom handles (`±(p[i+1] − p[i−1])/6`), so any handles work.

Result on vanilla combi: all 206 rivers convert, with 5351 points. Every row of
every mesh is within 0.05 of its fitted spline when that spline is
re-tessellated with the rules above. On IEE's 60 rivers the fit uses 1089
points; the hand-made originals have 1178. At a tolerance of 0.01 it took 9059 and
1899 points.

The written entity has `reverse_direction="false"` and `terrain_relative="false"`.
The point attributes the bake drops get the values Terry gives a new point:
`terrain_offset="0" alpha_fade="1" flow_speed="0.5" foam_amount="0.100000001"`.
These are the only values IEE uses, apart from `foam_amount` on 2 points. The
material is the `.wsmodel`'s, retargeted like water planes when the campaign is
renamed.
