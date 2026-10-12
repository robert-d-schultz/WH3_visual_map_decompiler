# Total War Map Decompiler

Turns a Total War: Warhammer III campaign map, as it ships in the game, back into
**Terry** projects that you can open, edit and recompile with the Assembly Kit.
Start a map mod from Immortal Empires, or from any other campaign, instead of
from an empty map.

## What you get

For the campaign you pick, the tool writes:

- **A campaign Terry project** with the whole map:
  - the terrain: land and sea-bed heights, shroud heights, texture blend, colour
    overlays, snow, corruption and event area masks, trees, the tile and climate
    maps, and the normal map
  - every prop, sound emitter, light, light probe and water plane, in one layer
    per province, plus a `_global` layer for the sea, the border and whatever
    belongs to no province
  - rivers as editable river splines
  - the map's lighting and its environment volumes (the local fog and haze
    around places like Skavenblight)
- **A battle Terry project** with the campaign's catchment maps.
- Optionally, the **devastated map** (the craters, chasms and burning ground of
  the event areas) as a project of its own.

The map can also be **renamed** into a new campaign and **resized**, all in the
same run.

## Requirements

- Windows 10 or 11, 64-bit.
- Total War: Warhammer III and its Assembly Kit.
- [RPFM](https://github.com/Frodo45127/rpfm) 5, with the game and Assembly Kit
  paths set in its settings. The tool reads the game's packs through RPFM's
  server and writes into the Assembly Kit that RPFM points at.

## Install

Download `TwMapDecompiler-<version>-win-x64.zip` from the
[Releases](https://github.com/robert-d-schultz/WH3_visual_map_decompiler/releases)
page and unzip it anywhere. It holds two programs:

- `TwMapDecompiler.App.exe`: the program with a window
- `TwMapDecompiler.Cli.exe`: the same thing for the command line

There is nothing else to install.

## Using it

1. Start `TwMapDecompiler.App.exe`.
2. Set **RPFM folder** to the folder that holds `rpfm_server.exe`. The tool
   starts the server itself.
3. Pick a **Campaign**. The list comes from the game's packs, and below it you
   see where the campaign's terrain, trees and battle map are read from.
4. Under **Steps**, everything is on except the devastated project. Leave it
   that way for a full decompile.
5. Click **Run** and follow the log.
6. Click **Open projects** to open the output folders, and open the `.terry`
   file in Terry.

The output goes into the Assembly Kit:

| What | Where |
|---|---|
| Campaign project | `raw_data/terrain/campaigns/<map>/` |
| Battle project | `raw_data/terrain/battles/<battle map>/` |
| The project's lighting | `working_data/Weather/default_battle_<campaign>.environment` |

By default the projects keep the vanilla names (for Immortal Empires,
`wh3_main_combi_map_1` and `wh3_main_combi_map`).

### Making a new map

Under **Overrides → Rename the output**, give the map, campaign and battle map
names of your own. A new campaign name also gives the map its own copies of the
files it would otherwise share with the vanilla campaign: the sea material, the
snow material and the lighting files. Your edits to them then only affect your
map.

### Resizing

**Resize the map** scales the whole map, then adds hexes to its edges:

- **Scale** grows or shrinks everything: the terrain, the props, the lights and
  the world size. The factor snaps to the nearest map size Terry can hold, and
  the window shows what it snaps to as you type.
- **Pad** adds hexes to the left, right, top and bottom. The new ground repeats
  the map's edge and starts without trees.

The details are in [docs/map-resize.md](docs/map-resize.md).

### The devastated map

Immortal Empires has a second map that holds every event area in its devastated
state. Turn on the **Devastated project** step to get it as a Terry project:

- **Decompile the main map first.** The devastated project is built on top of it.
  Its terrain maps are the main project's, linked rather than copied, with the
  devastation as a layer on top. Its props are one layer per event area.
- After you edit the main project in Terry, run the step again so the devastated
  project picks up your changes.
- To compile it back into the game's pieces, turn on Terry's *Process
  Devastation Pieces*.

The details are in [docs/devastated-project.md](docs/devastated-project.md).

### Other options

- **Refine the heights with the normal map** (under the Height step) makes the
  land heights a little more accurate, for a couple of extra minutes and about
  8 GB of memory.
- **Modded cultures** (under the Layers step, on by default) also shows props
  dressed for vanilla cultures to popular modded cultures that follow them: a
  Nagash mod's culture gets the Undead Legions' props, and order-faction mods
  such as Albion and Nippon get the props the Empire, Dwarfs, High Elves and
  Cathay share.
- **Overrides → Read from here instead of the packs** reads any input from a
  folder or file of your own, and **Write to this Assembly Kit** writes
  somewhere other than the Assembly Kit RPFM points at.

## Command line

`TwMapDecompiler.Cli.exe` runs the same steps without a window:

```
TwMapDecompiler.Cli --list-campaigns
TwMapDecompiler.Cli wh3_main_combi --rpfm "C:\Tools\RPFM"
TwMapDecompiler.Cli wh3_main_combi --map-name my_map_1 --campaign-name my_campaign --scale 1.25
```

`--help` lists every option. RPFM's folder can also be set once in the
`RPFM_FOLDER` environment variable.

## What can't be recovered

The game's files don't keep everything the original project had. Where something
is lost, the tool gives you the closest thing that compiles to the same map:

- **Trees**: the compile reads only a quarter of the tree map's pixels. You get a
  tree map that compiles to the same trees, not CA's original painting.
- **Rivers**: the shape, width and material come back, but flow speed, alpha
  fade, foam and terrain offset start at Terry's defaults.
- **Layers**: CA's own layer structure isn't in the game's files, so props are
  grouped by province.
- **Environment volumes**: cylinders get a height of 0.
- **Patch visibility** is left blank. Only Realms of Chaos uses it.

## How it works

The [docs](docs/) folder explains how each part of the map is read back:

- [campaign-project.md](docs/campaign-project.md): every step, the file formats,
  the prop layers, lighting and renamed campaigns
- [map-resize.md](docs/map-resize.md): scaling and padding
- [devastated-project.md](docs/devastated-project.md): the devastated project
- [river-splines.md](docs/river-splines.md): rebuilding river splines from the
  game's river meshes
- [tree-scatter-algorithm.md](docs/tree-scatter-algorithm.md): how the game's
  trees are placed from the tree map
- [event-area-pieces.md](docs/event-area-pieces.md): the event area pieces the
  game swaps in and out
- [colour-overlay-blending.md](docs/colour-overlay-blending.md): how Terry
  combines colour overlay layers

## Building from source

Needs the .NET 8 SDK.

```
dotnet build
dotnet run --project src/TwMapDecompiler.App           # window
dotnet run --project src/TwMapDecompiler.Cli -- --help  # command line
dotnet test
```

| Project | Contents |
|---|---|
| [src/TwMapDecompiler.Core](src/TwMapDecompiler.Core/) | The file readers and writers, the steps, and the RPFM client |
| [src/TwMapDecompiler.App](src/TwMapDecompiler.App/) | The window (WPF) |
| [src/TwMapDecompiler.Cli](src/TwMapDecompiler.Cli/) | The command line |

To build the release zip's programs (the version is set in
[Directory.Build.props](Directory.Build.props)):

```
dotnet publish src/TwMapDecompiler.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
dotnet publish src/TwMapDecompiler.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## License

[MIT](LICENSE)
