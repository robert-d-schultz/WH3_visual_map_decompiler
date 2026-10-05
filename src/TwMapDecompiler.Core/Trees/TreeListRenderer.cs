using TwMapDecompiler.Core.IO;
using TwMapDecompiler.Core.Imaging;

namespace TwMapDecompiler.Core.Trees;

/// <summary>
/// Rasterizes trees.campaign_tree_list (world-space tree instances) into the
/// indexed CampaignTree map Terry uses. Each tree id's colour in campaign_tree_ids
/// picks its colour index (see <see cref="TreePalette"/>); each instance is mapped
/// back to the 2x2-pixel cell BOB read it from.
/// </summary>
public static class TreeListRenderer
{
    public sealed record RenderResult(int Width, int Height, byte[] Pixels, float WorldWidth, float WorldHeight);

    /// <summary>One tree instance. <paramref name="Name"/> carries its variant suffix (tree_large_01).</summary>
    public sealed record TreeInstance(string Name, float X, float Y, float Z);

    /// <summary>A campaign tree list's world size and instances, in file order.</summary>
    public sealed record TreeList(float WorldWidth, float WorldHeight, List<TreeInstance> Trees);

    public static TreeList Read(string treeListPath)
    {
        using var stream = new BufferedStream(File.OpenRead(treeListPath), 1 << 20);
        using var reader = new BinaryReader(stream);

        reader.Skip(8); // magic
        reader.Skip(4); // version
        float worldWidth = reader.ReadSingle();
        float worldHeight = reader.ReadSingle();

        var trees = new List<TreeInstance>();
        uint typeCount = reader.ReadUInt32();
        for (uint t = 0; t < typeCount; t++)
        {
            string treeName = reader.ReadU16String();
            uint instanceCount = reader.ReadUInt32();
            for (uint n = 0; n < instanceCount; n++)
            {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();
                reader.Skip(3); // IsRemovable (always 1), RotationIndex (0-5), SeasonMask (always 255)
                trees.Add(new TreeInstance(treeName, x, y, z));
            }
        }
        return new TreeList(worldWidth, worldHeight, trees);
    }

    /// <param name="width">Output image width in pixels (typically height map width / 4).</param>
    /// <param name="height">Output image height in pixels.</param>
    public static RenderResult Render(string treeListPath, TreePalette palette, int width, int height, Action<string>? log = null) =>
        Render(Read(treeListPath), palette, width, height, log);

    public static RenderResult Render(TreeList list, TreePalette palette, int width, int height, Action<string>? log = null)
    {
        float worldWidth = list.WorldWidth;
        float worldHeight = list.WorldHeight;

        // BOB's tree scatter, recovered by round-tripping probe maps through BOB itself
        // (a flood-filled 2880x1941 tree map compiled to exactly 1440 * 970 = 1,396,800
        // instances, one per cell, and compiling the same map twice gives byte-identical
        // output -- the scatter is a pure function of position, not a seeded RNG).
        //
        // The map is divided into cells of exactly 2x2 pixels, N = width/2 by M = height/2.
        // Cell (i, j) reads ONE pixel and emits at most one tree:
        //
        //     column = 2*i                       always the even column
        //     row    = 2*j + (i & 1)             counted up from the south edge
        //     x      = i * dx + U(-0.2, +0.2)    dx = worldWidth / (N - 1)
        //     z      = row * sz + U(-0.2, +0.2)  sz = worldHeight / height
        //
        // A 255 pixel emits nothing. Both jitters are uniform over the same absolute
        // constant of +-0.2 world units -- confirmed on the probes and on CA's
        // cr_oldworld_map_1, which has a different scale. dx is pinned to 4e-8 and
        // predicts exactly 0.668 for cr_oldworld_map_1, which is what a Fourier scan of
        // that map's instance positions independently finds.
        //
        // The inverse below is exact, because (i & 1) fixes the sub-row without having to
        // guess it from z: 0.2 is 0.30 of a column and 0.26 of a cell row, both under a
        // half, so both roundings are unambiguous. It recovers the sampled pixel for
        // 100.0000% of instances on all three probes.
        //
        // Only one pixel per 2x2 cell is ever read, so three quarters of a tree map --
        // every odd column, and one row of every cell -- never reaches the tree list. Those
        // pixels carry no information and cannot be recovered; the whole cell is filled
        // with its tree's colour, which both round-trips through BOB and avoids leaving the
        // map as a sparse dot screen.
        int cellCols = width / 2, cellRows = height / 2;
        double dx = worldWidth / (cellCols - 1);
        double sz = worldHeight / (double)height;
        double dz = 2 * sz;

        var pixels = new byte[width * height];
        Array.Fill(pixels, TreePalette.Empty);

        var unknownTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var tree in list.Trees)
        {
            if (palette.IndexOf(tree.Name) is not { } paletteIndex)
            {
                unknownTypes[tree.Name] = unknownTypes.GetValueOrDefault(tree.Name) + 1;
                continue;
            }
            // y is terrain elevation, sampled from the height map, so it is not needed.
            float x = tree.X, z = tree.Z;

            int i = (int)Math.Round(x / dx);
            if (i < 0) i = 0; else if (i >= cellCols) i = cellCols - 1;
            int b = i & 1;

            int j = (int)Math.Round((z - b * sz) / dz);
            if (j < 0) j = 0; else if (j >= cellRows) j = cellRows - 1;

            // World +Z runs south to north but TIFF row 0 is the northernmost row, so
            // the row index is mirrored -- the same convention TileMapRenderer applies
            // to its finished map. Every tree list checked puts the snow types at ~0.83
            // of the world height and the desert/jungle/palm types at ~0.2, and the
            // source TIFFs put snow at the top, which fixes the direction. (CA's
            // shipped wh3_main_combi_map_1 tree TIFF disagrees, but that one file is
            // itself stored upside down: its vegetation only lands on land when its own
            // height map is flipped.)
            int col = 2 * i;
            int top = height - 1 - (2 * j + 1);
            Plot(pixels, width, height, col, top, paletteIndex);
            Plot(pixels, width, height, col + 1, top, paletteIndex);
            Plot(pixels, width, height, col, top + 1, paletteIndex);
            Plot(pixels, width, height, col + 1, top + 1, paletteIndex);
        }

        foreach (var (type, count) in unknownTypes)
            log?.Invoke($"Warning: unknown tree type '{type}' (not in {TreePalette.TableName}), {count} instances skipped");

        return new RenderResult(width, height, pixels, worldWidth, worldHeight);
    }

    private static void Plot(byte[] pixels, int width, int height, int x, int y, byte value)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
            pixels[y * width + x] = value;
    }

    /// <summary>Reads just the world dimensions from a tree list header (used for the .terry world_width).</summary>
    public static (float Width, float Height) ReadWorldSize(string treeListPath)
    {
        using var stream = File.OpenRead(treeListPath);
        using var reader = new BinaryReader(stream);
        reader.Skip(12);
        return (reader.ReadSingle(), reader.ReadSingle());
    }
}
