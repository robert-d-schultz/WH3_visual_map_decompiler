using System.Globalization;

namespace TwMapDecompiler.Core.Pieces;

/// <summary>One mip level of a piece texture: where it came from in the map's texture and where it sits in the piece file.</summary>
/// <param name="SrcX">Left edge in the map texture, in pixels.</param>
/// <param name="SrcY">Top edge in the map texture, in pixels, counted in the file's own row order.</param>
/// <param name="BlockBytes">Bytes per 4x4 block (block-compressed) or per pixel (uncompressed).</param>
/// <param name="BlocksW">Width in blocks (or pixels when uncompressed).</param>
/// <param name="Offset">Byte offset of the mip in the piece's DDS file.</param>
public sealed record PieceMip(int SrcX, int SrcY, int SrcW, int SrcH, int BlockBytes, int BlocksW, int BlocksH, int Offset)
{
    /// <summary>Pixels per block side: 4 for block-compressed textures, 1 otherwise.</summary>
    public int BlockSize => BlocksW == SrcW ? 1 : 4;
}

/// <summary>A piece texture's entry in texture_info.</summary>
/// <param name="Flipped">The map texture stores its rows bottom-up (the height map and tile mask).</param>
public sealed record PieceTexture(string Name, bool Flipped, IReadOnlyList<PieceMip> Mips);

/// <summary>
/// One event area's piece: pieces/event_&lt;colour hex&gt;/, a copy of everything inside the area's
/// bounding box, which the game swaps into the live map. See docs/event-area-pieces.md.
/// </summary>
public sealed class EventPiece
{
    public required string Folder { get; init; }

    /// <summary>The area's colour_hex in campaign_map_event_areas, from the folder name (event_0006dc -> 0006dc).</summary>
    public required string ColourHex { get; init; }

    /// <summary>Bounding box in event-area-mask pixels (rows top-down).</summary>
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Size of the event area mask the box is measured against.</summary>
    public required int MaskWidth { get; init; }
    public required int MaskHeight { get; init; }

    public required IReadOnlyDictionary<string, PieceTexture> Textures { get; init; }

    public string File(string name) => Path.Combine(Folder, name);

    public bool Has(string name) => System.IO.File.Exists(File(name));

    public static EventPiece Load(string folder)
    {
        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string[] lines = System.IO.File.ReadAllLines(Path.Combine(folder, "texture_info"));

        // mask <mask width> <mask height> <x> <y> <width> <height>
        var head = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (head.Length != 7 || head[0] != "mask")
            throw new InvalidDataException($"{folder}: texture_info does not start with a mask line ('{lines[0]}').");
        int[] box = head[1..].Select(Int).ToArray();

        var textures = new Dictionary<string, PieceTexture>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            // <file> <mip count> <flipped>, then one line per mip
            var fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int mipCount = Int(fields[1]);
            var mips = new List<PieceMip>(mipCount);
            for (int m = 0; m < mipCount; m++)
            {
                int[] v = lines[++i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Int).ToArray();
                mips.Add(new PieceMip(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]));
            }
            textures[fields[0]] = new PieceTexture(fields[0], Int(fields[2]) != 0, mips);
        }

        return new EventPiece
        {
            Folder = folder,
            ColourHex = name.StartsWith("event_", StringComparison.OrdinalIgnoreCase) ? name[6..].ToLowerInvariant() : name,
            MaskWidth = box[0],
            MaskHeight = box[1],
            X = box[2],
            Y = box[3],
            Width = box[4],
            Height = box[5],
            Textures = textures,
        };
    }

    /// <summary>
    /// The area's pixels within the bounding box: one bit per event-mask pixel, LSB first, rows
    /// top-down. Row-major over <see cref="Width"/> x <see cref="Height"/>.
    /// </summary>
    public bool[] ReadMask()
    {
        byte[] bytes = System.IO.File.ReadAllBytes(File("mask"));
        var bits = new bool[Width * Height];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = (bytes[i >> 3] >> (i & 7) & 1) != 0;
        return bits;
    }

    /// <summary>Road tiles: (x, y) in tile-mask cells counted from the bottom, and an index into event_tiles.</summary>
    public List<(int X, int Y, int Tile)> ReadTileList()
    {
        var tiles = new List<(int, int, int)>();
        if (!Has("tile_list"))
            return tiles;
        using var reader = new BinaryReader(System.IO.File.OpenRead(File("tile_list")));
        reader.ReadUInt32(); // always 1
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            tiles.Add((reader.ReadUInt16(), reader.ReadUInt16(), (int)reader.ReadUInt32()));
        return tiles;
    }

    /// <summary>Trees: world position, rotation index and an index into event_trees.</summary>
    public List<(float X, float Y, float Z, byte Rotation, int Tree)> ReadTreeList()
    {
        var trees = new List<(float, float, float, byte, int)>();
        if (!Has("tree_list"))
            return trees;
        using var reader = new BinaryReader(System.IO.File.OpenRead(File("tree_list")));
        while (reader.BaseStream.Position + 16 <= reader.BaseStream.Length)
        {
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
            reader.ReadByte(); // IsRemovable, always 1
            byte rotation = reader.ReadByte();
            trees.Add((x, y, z, rotation, reader.ReadUInt16()));
        }
        return trees;
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}

/// <summary>
/// All pieces of one map folder, plus the map-level lists their tile and tree records index:
/// event_tiles (tile folder per line) and event_trees (tree name per line).
/// </summary>
public sealed class PieceSet
{
    public required string MapFolder { get; init; }
    public required IReadOnlyList<EventPiece> Pieces { get; init; }
    public required IReadOnlyList<string> EventTiles { get; init; }
    public required IReadOnlyList<string> EventTrees { get; init; }

    /// <summary>Loads terrain/campaigns/&lt;map&gt;/pieces; null when the map has none.</summary>
    public static PieceSet? Load(string mapFolder)
    {
        string piecesFolder = Path.Combine(mapFolder, "pieces");
        if (!Directory.Exists(piecesFolder))
            return null;

        var pieces = Directory.EnumerateDirectories(piecesFolder, "event_*")
            .Where(d => File.Exists(Path.Combine(d, "texture_info")))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(EventPiece.Load)
            .ToList();

        return new PieceSet
        {
            MapFolder = mapFolder,
            Pieces = pieces,
            EventTiles = ReadList(Path.Combine(mapFolder, "event_tiles")),
            EventTrees = ReadList(Path.Combine(mapFolder, "event_trees")),
        };
    }

    public EventPiece? Find(string colourHex) =>
        Pieces.FirstOrDefault(p => string.Equals(p.ColourHex, colourHex, StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadList(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
}
