# BOB's tree scatter: what is known

Notes from reverse-engineering how BOB turns a tree map TIFF into
`trees.campaign_tree_list`, September 2026. Shelved part-way: the **decompile
direction is finished and exact**, the **compile direction is about half solved**.

Read this before touching `TreeListRenderer.cs` or attempting a tree compiler.

## The one fact that governs everything

BOB divides the tree map into cells of exactly **2x2 pixels**, `N = width/2` by
`M = height/2`, and each cell reads **one pixel** and emits at most one tree:

```
cell (i, j),  i in [0,N),  j in [0,M):

    b   = i & 1
    col = 2*i                       the even column, always
    row = 2*j + b                   counted up from the south edge
    sample the tree map at (col, row); index 255 emits nothing
    x = i*dx  + U(-0.2, +0.2)       dx = worldWidth / (N - 1)
    z = row*sz + U(-0.2, +0.2)      sz = worldHeight / height
```

Three quarters of a tree map is therefore **never read**: every odd column, and
one row of every cell. Those pixels cannot be recovered from a tree list, and
they do not affect the compiled output. A byte-exact round trip back to CA's
original TIFF is impossible in principle; the achievable target is a TIFF that
*recompiles to the same tree list*, which is what the decompiler produces by
filling all four pixels of each cell.

When the height is odd (it is on every map here) the topmost image row falls
outside the last cell row and is never covered.

### Evidence

- A flood-filled 2880x1941 probe compiled to exactly 1440 * 970 = 1,396,800
  instances, one per cell, in row-major scan order.
- `dx = worldWidth/(N-1)` is confirmed to 4e-8 on the probes and predicts
  exactly `0.668` for `cr_oldworld_map_1` — which is what a Fourier scan of that
  map's instance positions independently finds.
- The 0.2 jitter is an absolute world-space constant, not a fraction of a cell:
  residuals land on +-0.2 for both probe maps and for `cr_oldworld_map_1`, which
  has a different scale.
- `b = i & 1` was read off a probe whose TIFF was a repeating 4x4 tile of 16
  distinct tree types, so each tree's base name identified the (col%4, row%4) of
  the pixel it sampled. The sampled column was `2*i` for all 1.4M instances.

## BOB is deterministic

Compiling the same tree map twice produces a **byte-identical**
`trees.campaign_tree_list`. There is no RNG and no seed — the jitter, rotation
and variant are pure functions of the grid cell.

They are also independent of map size and of the map's content. Comparing the
2880x1941 probes against The Old World's 4096x3549 `cr_oldworld_map_1` at the same (i,row):
`RotationIndex` agrees for **99.98%** of shared cells (chance is 16.7%), and
positions agree to a mean difference of 0.0002, which is float32 noise from the
slightly different `dx`. The three probes (grass / rocks_small / 4x4 tile) are
bit-identical to each other in position and rotation.

## Decompile: solved, exact

Because `b` is fixed by `i & 1` it never has to be guessed from `z`, and 0.2 is
0.30 of a column and 0.26 of a cell row — both under a half — so both roundings
are unambiguous:

```
i = round(x / dx)
b = i & 1
j = round((z - b*sz) / dz)          dz = 2*sz
col = 2*i,  row = height - 1 - (2*j + b)
```

Recovers the exact sampled pixel for **100.0000%** of instances on all three
probes, and 99.79% on `cr_oldworld_map_1` (that TIFF is a year-old Desktop copy
of a map since rebuilt, so a small disagreement is expected, not a model error).

Implemented in `src/TwMapDecompiler.Core/Trees/TreeListRenderer.cs`.

### Orientation

World +Z runs south to north; TIFF row 0 is the northernmost row, so the row
index is mirrored. Every tree list checked puts the snow types at ~0.83 of the
world height and desert/jungle/palm at ~0.2, and the source TIFFs put snow at
the top.

**Trap:** CA's shipped `assembly_kit\...\wh3_main_combi_map_1` tree TIFF is
itself stored upside down — its vegetation only lands on land when its own
height map is flipped. It is the one file that makes the tree map look
un-flipped, and `reference/4 trees.py` was written against it. Use the tree
TIFF of a hand-made project instead, such as The Old World's `cr_oldworld_map_1`
(the author's own map) or `cr_combi_expanded_map_1`.

## Compile: about half solved

### The jitter is a 2D additive (Weyl) recurrence

Normalised to the 0.4-wide jitter window:

```
jx(i,j) = 0.4 * frac(0.601355229*i + 0.244342*j + cx) - 0.2
jz(i,j) = 0.4 * frac(0.085032449*i + 0.686598*j + cz) - 0.2
```

Cell row 0 is exactly `frac(i*alpha)` across all 1440 columns — no drift, no
defects. The row-to-row phase advance is exactly constant: `0.488684` in x and
`0.373170` in z between consecutive fitting rows, identical to six decimals for
every row pair checked. Those pin the `j` coefficients, and they agree with an
independent 2D FFT of the whole field (0.244330 and 0.686598).

The x and z streams are **one stream, not two**: the x-step determines the
z-step. Wherever `dtx = 0.8916`, `dtz = 0.6014`, without exception, and the
observed step values form a single chain in which each pair's z-step is the next
pair's x-step.

### The variant is an integer counter, not a hash

On the fitting rows it steps by exactly **-1 mod 4** for the four-variant sets
and **+1 mod 3** for the three-variant sets. Both are satisfied by a counter
advancing `= 3 (mod 4)` and `= 1 (mod 3)` per cell, i.e. **+7 mod 12**.

Variant totals are uniform overall: grass split 349111/349321/349219/349149
across four, rocks_small 465560/465600/465640 across three.

### What is NOT solved

1. **Half the rows do not fit.** The model is exact on **480 of 970** cell rows.
   Which rows fit follows a quasi-periodic (Sturmian) pattern of density ~0.495
   — `j = 0,1,3,5..17, then 20,22..36, then 39,41..` — not a parity. On the
   non-fitting rows the advance is different: consecutive stream indices
   (`dk = 1` for every column) were recovered on rows 0, 1 and 3, but on rows 2
   and 4 no index below 300,000 fits either stream.
2. **RotationIndex.** Uniform over 0-5 and independent of the variant. It forms
   runs of equal values along a row, so it is a floor of something slowly
   varying, but the source was not identified.
3. **The absolute phases** `cx`, `cz`.

### Why it stalled

Positions are stored as **float32**. The jitter is recovered as `x - i*dx`, so
precision degrades as x grows — at the far edge of a 961-wide map an ulp is
6.1e-5, about 13 bits of the jitter's 0.4 range. Extrapolating a Weyl constant
across ~10^6 cells needs it to ~1e-14; the data supports ~1e-9. Hence the
increments are clean but the absolute stream index is not recoverable, and the
"predicted within 1e-3" coverage is 0.27 where "within 1e-2" is 0.51.

### Where to resume

One more BOB run, on a **differently sized map**. Probes are already generated
for `arena_map_4_medium_1` (1920x1663 -> grid 960x831, world 641 wide) at
`C:\Users\rob\Desktop\tree_probes\arena\`. `a_grass` alone is enough.

The diagnostic: if the fitting-row pattern is *identical in j* to the combi
map's, the generator's row advance is map-independent and the two row classes
are intrinsic to the sequence. If it scales with the new N, the advance is tied
to row length, which would explain the second class and give the full indexing.
Smaller world coordinates also buy back precision on the constants.

## Data and tooling

Probe round-trips (probe TIFF + the `trees.campaign_tree_list` BOB produced from
it) live outside the repo at `C:\Users\rob\Desktop\tree_probes\`:

| folder | TIFF |
| --- | --- |
| `a_grass` | every pixel `grass` (4 variants) — also `trees2.campaign_tree_list`, the byte-identical second run |
| `b_rocks_small` | every pixel `rocks_small` (3 variants) |
| `c_tile4x4` | repeating 4x4 tile of 16 distinct types |
| `arena/` | the same three, sized for `arena_map_4_medium_1`, not yet compiled |

Analysis scripts are in `tools/TreeScatterAnalysis/` (Python, needs numpy +
Pillow; run them from that directory):

| script | what it does |
| --- | --- |
| `parse.py` | tree list -> numpy record array |
| `palette.py` | TIFF palette index -> tree base name, via RGB |
| `generate_probes.py` | build drop-in probe TIFFs from a project's own tree TIFF |
| `cells_exact.py` | build the per-cell `(i,j) -> jx, jz, rotation, variant` table (`cells.npz`) |
| `exact.py` | verify exact-pixel recovery against the source TIFF |
| `probec2.py` | derive `b` from the 4x4 tile probe |
| `rowadv.py` | measure the row-to-row Weyl phase advance |
| `model2d.py` | test 2D Weyl model variants and their coverage |
| `fields.py`, `pairs.py` | variant/rotation rules and step-pair structure |

The record layout matches the 010 template at
`C:\Users\rob\CascadeProjects\CampaignMapToolkit\FileFormatDefinitions\010_hex_editor_campaign_trees_template.bt`:
15 bytes per instance, `float x, y, z` then `IsRemovable` (always 1),
`RotationIndex` (0-5), `SeasonMask` (always 255). `y` is terrain elevation
sampled from the height map and is not needed to decompile.
