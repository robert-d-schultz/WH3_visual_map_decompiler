using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Rpfm;
using TwMapDecompiler.Core.Tiles;

namespace TwMapDecompiler.Core.Imaging;

/// <summary>One campaign_tree_ids row: a tree variant (tree_large_01) and the tree map colour it is painted with.</summary>
public sealed record TreeId(string Id, TileColor Color);

/// <summary>
/// The tree map's colour table, read from the <c>campaign_tree_ids</c> DB table. Each row is one
/// variant of a tree type (tree_large_01..04), and every variant of a type carries the type's
/// colour; BOB paints a pixel of that colour with one of the variants. The colour table is the
/// table's distinct colours sorted by RGB value, with nothing else in it. That is how every CA
/// tree TIFF is laid out; for example, CA's wh3_main_combi_map_1 tree TIFF carries exactly the
/// current table's 70 colours in that order. (Its last entry is white, tree_large_burnt, not a
/// sentinel.) A game update that adds a tree type moves every index after it, so the table is read, not recorded.
/// </summary>
public sealed class TreePalette
{
    public const string TableName = "campaign_tree_ids_tables";

    /// <summary>Pixel value for "no vegetation"; never a colour index.</summary>
    public const byte Empty = 255;

    /// <summary>Distinct tree colours in index order.</summary>
    public IReadOnlyList<TileColor> Colors { get; }

    /// <summary>Number of tree ids (variants) the table has.</summary>
    public int TreeIdCount => _indexById.Count;

    private readonly Dictionary<string, byte> _indexById;

    private TreePalette(IReadOnlyList<TileColor> colors, Dictionary<string, byte> indexById)
    {
        Colors = colors;
        _indexById = indexById;
    }

    /// <summary>Reads the table using an already open pack session.</summary>
    public static async Task<TreePalette> ReadAsync(PackFileSource packs, string scratchFolder, CancellationToken ct = default)
    {
        var rows = new List<TreeId>();
        foreach (var row in await packs.ReadTableAsync(TableName, scratchFolder, ct))
        {
            if (!row.TryGetValue("tree_id", out var v) || v is not JsonValue value || !value.TryGetValue(out string? id) || id.Length == 0)
                continue;
            rows.Add(new TreeId(id, EventAreaPalette.ReadColour(row, id, "Tree id")));
        }
        return FromRows(rows);
    }

    /// <summary>
    /// Sorts the distinct colours by RGB. A tree id in more than one table file keeps its first
    /// row, the one the game loads (table files are read in name order).
    /// </summary>
    public static TreePalette FromRows(IEnumerable<TreeId> rows)
    {
        var ids = rows.DistinctBy(r => r.Id, StringComparer.Ordinal).ToList();
        var colors = ids.Select(r => r.Color).Distinct().OrderBy(c => c.R).ThenBy(c => c.G).ThenBy(c => c.B).ToList();
        if (colors.Count > Empty)
            throw new InvalidDataException(
                $"{colors.Count} tree colours do not fit an 8-bit tree map (at most {Empty}; {Empty} means no vegetation).");

        var indexByColor = colors.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => (byte)x.i);
        return new TreePalette(colors, ids.ToDictionary(r => r.Id, r => indexByColor[r.Color], StringComparer.Ordinal));
    }

    /// <summary>The colour index of a tree list type name (a tree id, variant suffix included), or null for an unknown one.</summary>
    public byte? IndexOf(string treeId) => _indexById.TryGetValue(treeId, out byte i) ? i : null;

    /// <summary>256 RGB triplets for an indexed TIFF; indices past the last colour are black.</summary>
    public byte[] ToRgbTable()
    {
        var table = new byte[256 * 3];
        for (int i = 0; i < Colors.Count; i++)
        {
            var c = Colors[i];
            table[i * 3] = c.R;
            table[i * 3 + 1] = c.G;
            table[i * 3 + 2] = c.B;
        }
        return table;
    }
}
