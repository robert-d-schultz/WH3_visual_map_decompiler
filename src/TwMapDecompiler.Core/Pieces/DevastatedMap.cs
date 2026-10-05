using TwMapDecompiler.Core.Tiles;
using TwMapDecompiler.Core.Trees;

namespace TwMapDecompiler.Core.Pieces;

/// <summary>
/// Builds the devastated map's tree and road lists: the vanilla lists with each event area's
/// contents swapped for the devastate map's piece of that area. (Textures are swapped by
/// <see cref="DdsPiecePatcher"/>.)
/// </summary>
public static class DevastatedMap
{
    /// <summary>
    /// The event-mask index each piece covers: the pixel under its first mask bit.
    /// </summary>
    public static byte AreaIndex(EventPiece piece, byte[] eventMask, int maskWidth)
    {
        bool[] bits = piece.ReadMask();
        int first = Array.IndexOf(bits, true);
        if (first < 0)
            throw new InvalidDataException($"{piece.Folder}: the mask has no pixel set.");
        return eventMask[(piece.Y + first / piece.Width) * maskWidth + piece.X + first % piece.Width];
    }

    /// <summary>
    /// Vanilla trees, minus those inside a devastated area, plus the devastate pieces' trees.
    ///
    /// A vanilla tree is inside an area when a normal piece lists a tree at its exact x/z, or
    /// when its position falls on one of the areas' event-mask pixels. Both are needed: BOB's
    /// own rule for which piece a tree goes into is not quite either (1-2% of the pieces' trees
    /// sit on a neighbouring area's pixel), and the shipped tree list can predate the pieces
    /// (it differs from them in elevation, and a few hundred trees in variant).
    /// </summary>
    public static TreeListRenderer.TreeList MergeTrees(TreeListRenderer.TreeList vanilla, PieceSet normal, PieceSet devastate,
        byte[] eventMask, int maskWidth, int maskHeight, Action<string> log)
    {
        var normalPositions = new HashSet<(float X, float Z)>();
        foreach (var piece in normal.Pieces)
            foreach (var t in piece.ReadTreeList())
                normalPositions.Add((t.X, t.Z));

        var areas = new bool[256];
        foreach (var piece in devastate.Pieces)
            areas[AreaIndex(piece, eventMask, maskWidth)] = true;

        int removedByPiece = 0, removedByMask = 0;
        var trees = new List<TreeListRenderer.TreeInstance>(vanilla.Trees.Count);
        foreach (var tree in vanilla.Trees)
        {
            if (normalPositions.Contains((tree.X, tree.Z)))
            {
                removedByPiece++;
                continue;
            }
            int col = (int)(tree.X / vanilla.WorldWidth * maskWidth);
            int row = maskHeight - 1 - (int)(tree.Z / vanilla.WorldHeight * maskHeight);
            if (col >= 0 && col < maskWidth && row >= 0 && row < maskHeight && areas[eventMask[row * maskWidth + col]])
            {
                removedByMask++;
                continue;
            }
            trees.Add(tree);
        }

        int added = 0, badIndex = 0;
        foreach (var piece in devastate.Pieces)
        {
            foreach (var t in piece.ReadTreeList())
            {
                if (t.Tree >= devastate.EventTrees.Count)
                {
                    badIndex++;
                    continue;
                }
                trees.Add(new TreeListRenderer.TreeInstance(devastate.EventTrees[t.Tree], t.X, t.Y, t.Z));
                added++;
            }
        }

        log($"Trees: {vanilla.Trees.Count:N0} vanilla, {removedByPiece:N0} removed as listed by a normal piece, " +
            $"{removedByMask:N0} more as standing in a devastated area, {added:N0} added from the devastate pieces " +
            $"-> {trees.Count:N0}");
        if (badIndex > 0)
            log($"Warning: {badIndex} devastate piece trees index past the end of event_trees ({devastate.EventTrees.Count} entries); skipped");
        return vanilla with { Trees = trees };
    }

    /// <summary>
    /// Vanilla tiles with each area's roads swapped: every tile a normal piece lists (same
    /// position, same tile) is removed, and the devastate pieces' roads are placed instead.
    ///
    /// A piece's road carries no rotation or climate, so it takes them from the vanilla road it
    /// replaces at the same position (the devastated roads are the same roads, mostly in the
    /// roads_grey tile set). Where there is none, it is placed unrotated with the climate of any
    /// vanilla tile at that position.
    /// </summary>
    public static TileMapRenderer.TileList MergeRoads(TileMapRenderer.TileList vanilla, PieceSet normal, PieceSet devastate,
        Action<string> log)
    {
        var listed = new HashSet<(int X, int Y, string Tile)>();
        foreach (var piece in normal.Pieces)
            foreach (var (x, y, tile) in piece.ReadTileList())
                if (tile < normal.EventTiles.Count)
                    listed.Add((x, y, Normalise(normal.EventTiles[tile])));

        var removedAt = new Dictionary<(int X, int Y), TileMapRenderer.TileEntry>();
        var anyAt = new Dictionary<(int X, int Y), TileMapRenderer.TileEntry>();
        var tiles = new List<TileMapRenderer.TileEntry>(vanilla.Tiles.Count);
        var found = new HashSet<(int X, int Y, string Tile)>();
        int removed = 0;
        foreach (var entry in vanilla.Tiles)
        {
            anyAt.TryAdd((entry.X, entry.Y), entry);
            var key = (entry.X, entry.Y, Normalise(entry.TilePath));
            if (listed.Contains(key))
            {
                removedAt.TryAdd((entry.X, entry.Y), entry);
                found.Add(key);
                removed++;
            }
            else
            {
                tiles.Add(entry);
            }
        }

        const byte Unrotated = 16;
        int added = 0, rotationFound = 0, badIndex = 0;
        foreach (var piece in devastate.Pieces)
        {
            foreach (var (x, y, tile) in piece.ReadTileList())
            {
                if (tile >= devastate.EventTiles.Count)
                {
                    badIndex++;
                    continue;
                }
                byte direction = Unrotated, climate = 0;
                if (removedAt.TryGetValue((x, y), out var replaced))
                {
                    direction = replaced.Direction;
                    climate = replaced.Climate;
                    rotationFound++;
                }
                else if (anyAt.TryGetValue((x, y), out var other))
                {
                    climate = other.Climate;
                }
                tiles.Add(new TileMapRenderer.TileEntry(devastate.EventTiles[tile], climate, x, y, direction));
                added++;
            }
        }

        log($"Roads: {removed:N0} vanilla road tiles removed, {added:N0} devastated ones placed " +
            $"({rotationFound:N0} over a vanilla road they take their rotation from, {added - rotationFound:N0} unrotated)");
        if (found.Count != listed.Count)
            log($"Note: the normal pieces list {listed.Count:N0} roads, {listed.Count - found.Count:N0} of them not in tile_list.bin");
        if (badIndex > 0)
            log($"Warning: {badIndex} devastate piece roads index past the end of event_tiles ({devastate.EventTiles.Count} entries); skipped");
        return vanilla with { Tiles = tiles };
    }

    private static string Normalise(string tilePath) =>
        tilePath.Replace('/', '\\').Trim().TrimEnd('\\').ToLowerInvariant();
}
