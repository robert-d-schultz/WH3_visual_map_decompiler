# Colour overlays: how Terry combines the layers

Measured 2026-09-29 on the Immortal Empires Expanded project (`cr_combi_expanded_map_1`),
from Terry's exported `lf_sea_colour.dds`.

## The rule

Terry does **not** alpha-over the layers of a colour overlay map (`ColorOverlay`,
`ColorOverlaySea`). Each visible layer is **hard-light** blended onto the result of
the layers below it, per channel (0-255):

```
out = top < 128 ? 2*top*bottom/255
                : 255 - 2*(255-top)*(255-bottom)/255
```

- A `top` of 128 (mid grey) leaves `bottom` unchanged. Darker values darken it and
  lighter values lighten it.
- Where the layer's alpha is 0, `bottom` passes through unchanged.
- Partial alpha hasn't been tested.
- The bottom-most visible layer comes out as itself. Vanilla has one layer, and it
  exports unchanged.

## Evidence

The IEE sea overlay had two visible layers:
- `base`: vanilla, opaque everywhere.
- `iee`: the IEE colours where they differ from vanilla, alpha 0/255.

The export is `DX10 BC1_UNORM`, 12800x7764, RGB only. Compared with the two layers:

| Where | Export vs `iee` | Export vs `base` | Export vs hardlight(`iee`, `base`) |
|---|---|---|---|
| `iee` alpha 255 (20.4M px) | mean 56, p99 119 | mean 73, p99 223 | **mean 2.7, median 3, p99 5** |
| `iee` alpha 0 (79.0M px) | n/a | **mean 0.08, p99 2** | n/a |

The hard-light residual is BC1 compression noise. Sample pixels (`iee`, `base` → export):

| `iee` | `base` | export | hard light |
|---|---|---|---|
| 107 242 206 | 24 32 41 | 16 231 173 | 20 232 173 |
| 30 32 28 | 24 32 41 | 8 8 8 | 6 8 9 |
| 25 32 41 | 90 203 239 | 16 48 74 | 18 51 77 |
| 3 251 71 | 0 255 189 | 0 255 104 | 0 255 105 |

## Conflict with the disassembly

`TerrainMapLayer::composite` mode 6 (`ColorOverlay`, `ColorOverlaySea`) was read on
2026-09-24 as "RGBA alpha-over, src alpha × opacity". The export shows hard light.
Either that reading is wrong, or the export takes a different path from the
in-editor composite. The export is what the game gets, so go by it.

## Consequences

- **An overlay can't be patched with a partial opaque layer.** The patch gets
  blended with the colours under it instead of replacing them. Keep each overlay as
  one full visible layer and hide the vanilla one (`visible="0"`; hidden layers are
  skipped). IEE does this: `iee` is the whole overlay, and vanilla is a hidden
  `vanilla_ref`.
- **The devastated project's colour overlay `devastation` layers are wrong.**
  `DevastationLayers.AlphaOver` writes the devastated colour, opaque where it
  changed. Terry then hard-lights it over vanilla rather than replacing vanilla, so
  the devastated areas come out wrong (e.g. a devastated 100 grey over a vanilla
  116 gives 91). The fix is to treat colour overlays like trees and snow: **write
  them whole**, with the main project's layers hidden as `reference` layers. Not
  done yet.
