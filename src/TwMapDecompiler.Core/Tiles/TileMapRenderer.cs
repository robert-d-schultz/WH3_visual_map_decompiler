using TwMapDecompiler.Core.IO;

namespace TwMapDecompiler.Core.Tiles;

/// <summary>
/// Renders a campaign tile_list.bin into a colour-coded tile map and climate map
/// (diagnostic RGB images). Every colour comes from the tile database's own
/// _settings.bin (see <see cref="TileSettings"/>) rather than a built-in table, so
/// titles that define different tile sets or colours render correctly without a
/// code change.
/// </summary>
public static class TileMapRenderer
{
    public sealed record RenderResult(
        int Width, int Height,
        byte[] TileMapRgb, byte[] ClimateMapRgb,
        int TileCount, float[] HeaderFloats);

    /// <summary>Stand-ins for names the settings file does not define, so a gap is visible rather than silent.</summary>
    private static readonly TileSetInfo UnknownTileSet =
        new("", "", "", "", "", new TileColor(255, 0, 255), ExcludeFromGlobalMesh: true);

    private static readonly ClimateInfo UnknownClimate = new("", new TileColor(128, 128, 128));

    /// <summary>One placed tile.</summary>
    /// <param name="TilePath">Tile folder, e.g. terrain\tiles\campaign\roads\2x2_straight_br_tr_b\.</param>
    /// <param name="X">Position in tile-map cells.</param>
    /// <param name="Y">Position in tile-map cells, counted from the bottom.</param>
    /// <param name="Direction">Rotation in the high nibble: 32, 64 or 128 turn it by 90, 180 or 270 degrees.</param>
    public sealed record TileEntry(string TilePath, byte Climate, int X, int Y, byte Direction);

    /// <summary>A tile_list.bin's map size, climate names and placed tiles, in file order, and the size of the campaign hex grid.</summary>
    public sealed record TileList(int Width, int Height, List<string> Climates, float[] HeaderFloats, List<TileEntry> Tiles,
        int HexWidth = 0, int HexHeight = 0);

    public static RenderResult Render(string tileListPath, TileDatabase database, TileSettings settings, Action<string>? log = null) =>
        Render(Read(tileListPath), database, settings, log);

    /// <summary>The campaign hex grid's size, from the header alone (width, height).</summary>
    public static (int Width, int Height) ReadHexGrid(string tileListPath)
    {
        using var stream = new BufferedStream(File.OpenRead(tileListPath), 1 << 16);
        using var reader = new BinaryReader(stream);
        var header = ReadHeader(reader);
        return (header.HexWidth, header.HexHeight);
    }

    private sealed record Header(List<string> Tiles, List<string> Climates, float[] Floats, int Width, int Height, int HexWidth, int HexHeight);

    public static TileList Read(string tileListPath)
    {
        using var stream = new BufferedStream(File.OpenRead(tileListPath), 1 << 20);
        using var reader = new BinaryReader(stream);
        var (tiles, climates, headerFloats, mapWidth, mapHeight, hexWidth, hexHeight) = ReadHeader(reader);

        uint infoCount = reader.ReadUInt32();
        var entries = new List<TileEntry>((int)infoCount);
        for (uint i = 0; i < infoCount; i++)
        {
            ushort one = reader.ReadUInt16();
            if (one != 1)
                throw new InvalidDataException($"tile info marker {one}, expected 1 (entry {i})");

            int tileIndex = (int)reader.ReadUInt32();
            byte climateIndex = reader.ReadByte();
            int posX = reader.ReadUInt16();
            int posY = reader.ReadUInt16();
            byte dirByte = reader.ReadByte();
            reader.ReadByte(); // 7 = in normal tile map, 0 = global-only
            reader.Skip(8);    // min/max tile height floats
            entries.Add(new TileEntry(tiles[tileIndex], climateIndex, posX, posY, dirByte));
        }

        return new TileList(mapWidth, mapHeight, climates, headerFloats, entries, hexWidth, hexHeight);
    }

    private static Header ReadHeader(BinaryReader reader)
    {
        reader.Skip(8); // magic
        ushort version = reader.ReadUInt16();
        if (version != 1 && version != 2)
            throw new InvalidDataException($"tile_list version {version}, expected 1 or 2");
        ushort tileListVersion = reader.ReadUInt16();
        if (tileListVersion != 1)
            throw new InvalidDataException($"tilelist sub-version {tileListVersion}, expected 1");

        var tiles = new List<string>();
        uint tileCount = reader.ReadUInt32();
        for (uint i = 0; i < tileCount; i++)
            tiles.Add(reader.ReadU16String());

        var climates = new List<string>();
        uint climateCount = reader.ReadUInt32();
        for (uint i = 0; i < climateCount; i++)
            climates.Add(reader.ReadU16String());

        var headerFloats = new float[7];
        for (int i = 0; i < 7; i++)
            headerFloats[i] = reader.ReadSingle();

        int mapWidth = (int)reader.ReadUInt32();
        int mapHeight = (int)reader.ReadUInt32();
        int hexWidth = (int)reader.ReadUInt32();
        int hexHeight = (int)reader.ReadUInt32();
        reader.Skip(16); // 4 signed ints
        reader.Skip(8);  // map width/height again (differs in WH3)
        reader.Skip(1);  // bool or padding
        return new Header(tiles, climates, headerFloats, mapWidth, mapHeight, hexWidth, hexHeight);
    }

    public static RenderResult Render(TileList list, TileDatabase database, TileSettings settings, Action<string>? log = null)
    {
        int mapWidth = list.Width, mapHeight = list.Height;
        var climates = list.Climates;
        var tileMap = new byte[mapWidth * mapHeight * 3];
        var climateMap = new byte[mapWidth * mapHeight * 3];

        var missingTiles = new HashSet<string>();
        var missingCategories = new HashSet<string>();
        var missingClimates = new HashSet<string>();
        int outOfBounds = 0;

        foreach (var entry in list.Tiles)
        {
            byte climateIndex = entry.Climate;
            int posX = entry.X;
            int posY = entry.Y;
            int direction = entry.Direction & 0xF0;

            string tilePath = entry.TilePath;
            if (!database.TryGet(tilePath, out var tile))
            {
                missingTiles.Add(tilePath);
                continue;
            }

            byte[] mask = tile.Mask.Length == tile.Width * tile.Height
                ? tile.Mask
                : Enumerable.Repeat((byte)1, tile.Width * tile.Height).ToArray();
            var grid = new MaskGrid(mask, tile.Width, tile.Height);
            grid = grid.FlipUd();
            grid = direction switch
            {
                32 => grid.Rot90(1),
                64 => grid.Rot90(2),
                128 => grid.Rot90(3),
                _ => grid,
            };

            if (!settings.TryGetTileSet(tile.Category, out var tileSet))
            {
                if (missingCategories.Add(tile.Category))
                    log?.Invoke($"Warning: tile category '{tile.Category}' has no TILE_SET in _settings.bin, using magenta");
                tileSet = UnknownTileSet with { Name = tile.Category };
            }
            var tileColor = tileSet.Color;

            string climateName = climates[climateIndex];
            if (!settings.TryGetClimate(climateName, out var climate))
            {
                if (missingClimates.Add(climateName))
                    log?.Invoke($"Warning: climate '{climateName}' has no CLIMATE in _settings.bin, using grey");
                climate = UnknownClimate with { Name = climateName };
            }
            var climateColor = climate.Color;

            // Unchanged name heuristic: this also catches sea_coast, sea_rock_coast and
            // sea_coast_sand. TileSetInfo.ExcludeFromGlobalMesh (false for exactly generic and
            // sea) is the file's own grouping, but it describes the zoomed-out campaign mesh
            // rather than paint order, so it is not substituted here without evidence.
            bool isSea = tile.Category.Contains("sea");
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid[x, y] != 1)
                        continue;
                    int px = posX + x;
                    int py = posY + y;
                    if (px < 0 || px >= mapWidth || py < 0 || py >= mapHeight)
                    {
                        outOfBounds++;
                        continue;
                    }
                    int o = (py * mapWidth + px) * 3;
                    // Sea tiles never overwrite already-drawn pixels.
                    if (isSea && (tileMap[o] != 0 || tileMap[o + 1] != 0 || tileMap[o + 2] != 0))
                        continue;
                    tileMap[o] = tileColor.R;
                    tileMap[o + 1] = tileColor.G;
                    tileMap[o + 2] = tileColor.B;
                    climateMap[o] = climateColor.R;
                    climateMap[o + 1] = climateColor.G;
                    climateMap[o + 2] = climateColor.B;
                }
            }
        }

        if (missingTiles.Count > 0)
            log?.Invoke($"Warning: {missingTiles.Count} tile paths missing from tile database (e.g. {missingTiles.First()})");
        if (outOfBounds > 0)
            log?.Invoke($"Note: {outOfBounds} tile pixels fell outside the map bounds");

        FlipTopBottomRgb(tileMap, mapWidth, mapHeight);
        FlipTopBottomRgb(climateMap, mapWidth, mapHeight);

        return new RenderResult(mapWidth, mapHeight, tileMap, climateMap, list.Tiles.Count, list.HeaderFloats);
    }

    private static void FlipTopBottomRgb(byte[] data, int width, int height)
    {
        int stride = width * 3;
        var temp = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Array.Copy(data, top, temp, 0, stride);
            Array.Copy(data, bottom, data, top, stride);
            Array.Copy(temp, 0, data, bottom, stride);
        }
    }

    /// <summary>Row-major 2D grid of the tile footprint mask with numpy-like transforms.</summary>
    private readonly struct MaskGrid(byte[] data, int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        private readonly byte[] _data = data;

        public byte this[int x, int y] => _data[y * Width + x];

        public MaskGrid FlipUd()
        {
            var result = new byte[_data.Length];
            for (int y = 0; y < Height; y++)
                Array.Copy(_data, y * Width, result, (Height - 1 - y) * Width, Width);
            return new MaskGrid(result, Width, Height);
        }

        /// <summary>Counter-clockwise 90-degree rotation, matching numpy.rot90.</summary>
        public MaskGrid Rot90(int times)
        {
            var grid = this;
            for (int i = 0; i < times; i++)
            {
                var result = new byte[grid._data.Length];
                // result[y, x] = source[x, W-1-y]; result is H=W_src, W=H_src
                for (int y = 0; y < grid.Width; y++)
                    for (int x = 0; x < grid.Height; x++)
                        result[y * grid.Height + x] = grid[grid.Width - 1 - y, x];
                grid = new MaskGrid(result, grid.Height, grid.Width);
            }
            return grid;
        }
    }
}
