using System.Text;
using TwMapDecompiler.Core.Maths;

namespace TwMapDecompiler.Core.Rivers;

/// <summary>
/// Reads the river meshes BOB bakes from Terry river splines
/// (terrain/campaigns/&lt;map&gt;/models/river_&lt;entity id&gt;.wsmodel.rigid_model_v2) back into the
/// cross-section rows they were built from. See docs/river-splines.md.
///
/// BOB tessellates a river (WARSCAPE::tessellate_spline) into rows across the spline every ~0.2
/// units of arc length. Each row is 2 or more vertices on a horizontal line square to the spline,
/// <c>width</c> long and centred on it. u runs across the row (±width/20) and v along the river
/// (arc length/10, 0 where the river starts, so the far end for a reverse_direction spline). The file
/// is RMV2 v8 with one LOD and one mesh of 48-byte vertices, stored relative to the bounding box centre
/// in the river entity's own (unrotated) frame.
/// </summary>
public static class RiverMesh
{
    private const int VertexSize = 48;

    /// <summary>u per unit of width: BOB's uv scale for campaign rivers is a tenth of a world unit.</summary>
    private const double UPerWidth = 0.1;

    /// <summary>
    /// The rows of the mesh, in the order the river flows (increasing v): each row's centre, in the
    /// entity's frame, and its width.
    /// </summary>
    public static (Vec3[] Centres, double[] Widths) ReadRows(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 0xA8 || Encoding.ASCII.GetString(data, 0, 4) != "RMV2")
            throw new InvalidDataException("not an RMV2 file");
        uint version = BitConverter.ToUInt32(data, 4);
        if (version != 8)
            throw new InvalidDataException($"RMV2 version {version}, expected 8");

        // LOD header after the 4+4+4 byte file header and the 128-byte skeleton name.
        const int lod = 12 + 128;
        uint meshCount = BitConverter.ToUInt32(data, lod);
        uint vertexBytes = BitConverter.ToUInt32(data, lod + 4);
        int mesh = checked((int)BitConverter.ToUInt32(data, lod + 12));
        if (meshCount != 1)
            throw new InvalidDataException($"{meshCount} meshes, expected 1");

        int headerSize = checked((int)BitConverter.ToUInt32(data, mesh + 8));
        int vertexCount = checked((int)BitConverter.ToUInt32(data, mesh + 12));
        if (vertexCount == 0 || vertexBytes != (uint)(vertexCount * VertexSize))
            throw new InvalidDataException($"vertex size {(vertexCount == 0 ? 0 : vertexBytes / vertexCount)}, expected {VertexSize}");
        var min = ReadVec(data, mesh + 24);
        var max = ReadVec(data, mesh + 36);
        var pivot = (min + max) / 2;

        // Group the vertices into rows by v, keeping each row's two ends (lowest and highest u).
        var rows = new SortedDictionary<float, (float MinU, Vec3 Min, float MaxU, Vec3 Max, int Count)>();
        int vertices = mesh + headerSize;
        for (int i = 0; i < vertexCount; i++)
        {
            int o = vertices + i * VertexSize;
            var position = ReadVec(data, o) + pivot;
            float u = BitConverter.ToSingle(data, o + 16);
            float v = BitConverter.ToSingle(data, o + 20);
            if (!rows.TryGetValue(v, out var row))
            {
                rows[v] = (u, position, u, position, 1);
                continue;
            }
            if (u < row.MinU) (row.MinU, row.Min) = (u, position);
            if (u > row.MaxU) (row.MaxU, row.Max) = (u, position);
            row.Count++;
            rows[v] = row;
        }
        if (rows.Count < 2)
            throw new InvalidDataException("fewer than 2 rows");

        var centres = new Vec3[rows.Count];
        var widths = new double[rows.Count];
        int r = 0;
        foreach (var row in rows.Values)
        {
            double width = Vec3.Distance(row.Min, row.Max);
            // Rules out meshes that only share the name: a baked river's u spans its width.
            double expected = (row.MaxU - row.MinU) / UPerWidth;
            if (row.Count < 2 || Math.Abs(width - expected) > 0.001 + 0.001 * width)
                throw new InvalidDataException($"row {r} is not a river cross-section (width {width:0.####}, u span says {expected:0.####})");
            centres[r] = (row.Min + row.Max) / 2;
            widths[r] = width;
            r++;
        }
        return (centres, widths);
    }

    private static Vec3 ReadVec(byte[] data, int offset) => new(
        BitConverter.ToSingle(data, offset),
        BitConverter.ToSingle(data, offset + 4),
        BitConverter.ToSingle(data, offset + 8));
}
