using System.Text;
using TwMapDecompiler.Core.Bmd;

namespace TwMapDecompiler.Core.Pieces;

/// <summary>
/// A piece's .culture file: the culture masks of the BMD beside it, which the piece's .bin
/// holds only as a placeholder. Sections of <c>u32 count, char[4] tag, count x u64 mask</c>,
/// one mask per entity of that kind in file order:
/// p props, v vfx, lp point lights, ls spot lights, m poly meshes, ht terrain hole
/// triangles, sc composite scenes, ss sound emitters. Empty sections are left out.
/// </summary>
public static class PieceCulture
{
    public static Dictionary<string, ulong[]> Read(string path)
    {
        var sections = new Dictionary<string, ulong[]>(StringComparer.Ordinal);
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            uint count = reader.ReadUInt32();
            string tag = Encoding.ASCII.GetString(reader.ReadBytes(4)).TrimEnd('\0');
            var masks = new ulong[count];
            for (int i = 0; i < count; i++)
                masks[i] = reader.ReadUInt64();
            sections[tag] = masks;
        }
        return sections;
    }

    /// <summary>
    /// Gives every entity of <paramref name="bmd"/> its mask from the .culture sections. The
    /// .bin's own mask field is a placeholder (1, which would read as culture bit 0), so an entity
    /// kind whose section is missing or has the wrong count gets "no restriction" instead, with a
    /// warning.
    ///
    /// Terrain holes are the exception: 'ht' holds one mask per Terry hole entity, while the .bin
    /// holds the triangles BOB cut them into, with triangles repeated across entities dropped - so
    /// for most pieces the counts differ and which triangle belongs to which mask is lost. When the
    /// counts differ the holes are left untouched and false is returned, for the caller to
    /// resolve (see <see cref="HoleMasks"/>).
    /// </summary>
    /// <returns>Whether the terrain hole masks were applied.</returns>
    public static bool Apply(CompiledBmd bmd, Dictionary<string, ulong[]> sections, string source, Action<string> log)
    {
        Assign(bmd.Props, "p");
        Assign(bmd.Vfx, "v");
        Assign(bmd.PointLights, "lp");
        Assign(bmd.SpotLights, "ls");
        Assign(bmd.PolyMeshes, "m");
        Assign(bmd.CompositeScenes, "sc");
        Assign(bmd.SoundEmitters, "ss");

        foreach (string tag in sections.Keys.Except(["p", "v", "lp", "ls", "m", "ht", "sc", "ss"]))
            log($"Warning: {source}: unknown .culture section '{tag}' ({sections[tag].Length} masks) ignored");

        if (bmd.TerrainHoleTris.Count == 0)
            return true;
        if (sections.TryGetValue("ht", out var holeMasks) && holeMasks.Length == bmd.TerrainHoleTris.Count)
        {
            for (int i = 0; i < holeMasks.Length; i++)
                bmd.TerrainHoleTris[i].CultureMask = holeMasks[i];
            return true;
        }
        return false;

        void Assign<T>(List<T> entities, string tag) where T : IMaskCombinable<T>
        {
            if (!sections.TryGetValue(tag, out var masks) || masks.Length != entities.Count)
            {
                if (entities.Count > 0)
                    log($"Warning: {source}: " + (masks == null
                        ? $"no '{tag}' section for {entities.Count} {typeof(T).Name} entities"
                        : $"'{tag}' section has {masks.Length} masks for {entities.Count} {typeof(T).Name} entities") +
                        "; they get no culture restriction");
                foreach (var entity in entities)
                    entity.CultureMask = ulong.MaxValue;
                return;
            }
            for (int i = 0; i < masks.Length; i++)
                entities[i].CultureMask = masks[i];
        }
    }

    /// <summary>
    /// Terrain hole triangle masks for a piece whose 'ht' section cannot be paired with its triangles.
    ///
    /// global_props.bin repeats a hole triangle once per culture it is cut for (19,147 triangles,
    /// 6,209 distinct, on combi), each copy under that culture's mask; a piece keeps each triangle
    /// once (every hole triangle of a vanilla piece is one of the vanilla map's). A triangle's mask
    /// is therefore the OR of the masks of the hole entities it came from. A triangle the vanilla map
    /// also has takes that combined vanilla mask. The devastated pieces mostly cut new holes (560 of
    /// their 16,121 triangles are vanilla ones); those get the OR of the file's 'ht' masks - the
    /// cultures that have any hole there, which contains each triangle's true mask.
    /// </summary>
    public sealed class HoleMasks
    {
        private readonly Dictionary<(long, long), List<BmdTerrainHoleTri>> _byPosition = [];

        public HoleMasks(IEnumerable<BmdTerrainHoleTri> vanilla)
        {
            foreach (var hole in vanilla)
            {
                var key = Key(hole);
                if (!_byPosition.TryGetValue(key, out var list))
                    _byPosition[key] = list = [];
                var same = list.Find(h => h.SameAs(hole));
                if (same != null)
                    same.CultureMask |= hole.CultureMask;
                else
                    list.Add(new BmdTerrainHoleTri
                    {
                        Transform = hole.Transform, Vertex2 = hole.Vertex2, Vertex3 = hole.Vertex3, CultureMask = hole.CultureMask,
                    });
            }
        }

        /// <summary>
        /// Gives each hole its vanilla twin's mask, or else <paramref name="fallback"/>; returns how
        /// many had no vanilla twin.
        /// </summary>
        public int Apply(IEnumerable<BmdTerrainHoleTri> holes, ulong fallback)
        {
            int unmatched = 0;
            foreach (var hole in holes)
            {
                var twin = _byPosition.TryGetValue(Key(hole), out var list) ? list.Find(h => h.SameAs(hole)) : null;
                if (twin == null)
                    unmatched++;
                hole.CultureMask = twin?.CultureMask ?? fallback;
            }
            return unmatched;
        }

        /// <summary>The OR of a file's 'ht' masks; no restriction when it has none.</summary>
        public static ulong Combined(Dictionary<string, ulong[]> sections) =>
            sections.TryGetValue("ht", out var masks) && masks.Length > 0
                ? masks.Aggregate(0UL, (all, m) => all | m)
                : ulong.MaxValue;

        private static (long, long) Key(BmdTerrainHoleTri hole) =>
            ((long)Math.Round(hole.Transform.PosX * 1000), (long)Math.Round(hole.Transform.PosZ * 1000));
    }
}
