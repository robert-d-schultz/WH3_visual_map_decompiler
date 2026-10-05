using TwMapDecompiler.Core.IO;

namespace TwMapDecompiler.Core.Tiles;

public enum TileGame
{
    Warhammer3,
    ThreeKingdoms,
}

public sealed record TileInfo(string Name, string Category, byte[] Mask, int Width, int Height);

/// <summary>
/// Reads the campaign tile shape database
/// (terrain/tiles/campaign/_tile_database/tiles/*.bin): per-tile footprint
/// masks and categories, keyed by the tile path the tile list refers to.
/// </summary>
public sealed class TileDatabase
{
    private readonly Dictionary<string, TileInfo> _tiles = [];

    public int Count => _tiles.Count;

    public bool TryGet(string tilePath, out TileInfo info) => _tiles.TryGetValue(tilePath, out info!);

    public static TileDatabase Load(string tileDirectory, TileGame game, Action<string>? log = null)
    {
        var db = new TileDatabase();
        foreach (string file in Directory.EnumerateFiles(tileDirectory, "*.bin"))
        {
            try
            {
                var (path, info) = ReadTileFile(file, game);
                db._tiles[path] = info;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Warning: could not read tile '{Path.GetFileName(file)}': {ex.Message}");
            }
        }
        log?.Invoke($"Tile database: {db.Count} tiles loaded");
        return db;
    }

    private static (string TilePath, TileInfo Info) ReadTileFile(string file, TileGame game)
    {
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);

        reader.Skip(8); // magic
        ushort version = reader.ReadUInt16();
        if (version <= 2)
            throw new InvalidDataException($"tile version {version}, expected > 2");

        string name = reader.ReadU16String();
        string category = reader.ReadU16String();

        string maskString = reader.ReadU16String();
        var mask = new byte[maskString.Length];
        for (int i = 0; i < maskString.Length; i++)
            mask[i] = (byte)(maskString[i] == '1' ? 1 : 0);

        // Version numbering diverged between titles.
        if ((game == TileGame.ThreeKingdoms && version > 5) || (game == TileGame.Warhammer3 && version > 7))
            reader.Skip(2);

        int width = (int)reader.ReadUInt32();
        int height = (int)reader.ReadUInt32();
        if (width > 64 || height > 64)
            throw new InvalidDataException($"implausible tile size {width}x{height}");

        reader.Skip(21);
        if (version < 7)
            reader.Skip(9);

        for (int i = 0; i < 8; i++)
            reader.ReadU16String();

        string tilePath = reader.ReadU16String();
        if (!tilePath.Contains(@"tiles\campaign"))
            throw new InvalidDataException($"unexpected tile path '{tilePath}'");

        return (tilePath, new TileInfo(name, category, mask, width, height));
    }
}
