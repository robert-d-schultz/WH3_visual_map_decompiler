namespace TwMapDecompiler.Core.Maths;

/// <summary>
/// 3x3 rotation/scale matrix stored as three column vectors, matching how the game
/// serializes prop transforms. Euler/scale extraction mirrors Blender's mathutils
/// (the reference Python scripts used mathutils.Matrix.to_euler()/to_scale()),
/// so results are bit-compatible with the proven Python output.
/// </summary>
public readonly struct Mat3
{
    // m[column][row], same memory layout as Blender's C matrices.
    private readonly double[][] m;

    public Mat3(float[] col1, float[] col2, float[] col3)
    {
        m = new[]
        {
            new double[] { col1[0], col1[1], col1[2] },
            new double[] { col2[0], col2[1], col2[2] },
            new double[] { col3[0], col3[1], col3[2] },
        };
    }

    private Mat3(double[][] columns) => m = columns;

    public static Mat3 FromColumns(double[] col1, double[] col2, double[] col3)
        => new([(double[])col1.Clone(), (double[])col2.Clone(), (double[])col3.Clone()]);

    public static Mat3 Read(BinaryReader reader)
    {
        var c1 = new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
        var c2 = new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
        var c3 = new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
        return new Mat3(c1, c2, c3);
    }

    /// <summary>Per-axis scale = length of each column vector (Blender mat3_to_size).</summary>
    public (double X, double Y, double Z) ToScale()
        => (Length(m[0]), Length(m[1]), Length(m[2]));

    /// <summary>
    /// Euler XYZ angles in degrees (Blender Matrix.to_euler(): normalize columns,
    /// then mat3_normalized_to_eul which picks the candidate with the smaller
    /// total absolute rotation).
    /// </summary>
    public (double X, double Y, double Z) ToEulerXyzDegrees()
    {
        var n = NormalizedColumns();

        double cy = Math.Sqrt(n[0][0] * n[0][0] + n[0][1] * n[0][1]);
        double e1x, e1y, e1z, e2x, e2y, e2z;
        if (cy > 16.0 * 1.192092896e-07)
        {
            e1x = Math.Atan2(n[1][2], n[2][2]);
            e1y = Math.Atan2(-n[0][2], cy);
            e1z = Math.Atan2(n[0][1], n[0][0]);

            e2x = Math.Atan2(-n[1][2], -n[2][2]);
            e2y = Math.Atan2(-n[0][2], -cy);
            e2z = Math.Atan2(-n[0][1], -n[0][0]);
        }
        else
        {
            e1x = Math.Atan2(-n[2][1], n[1][1]);
            e1y = Math.Atan2(-n[0][2], cy);
            e1z = 0.0;
            (e2x, e2y, e2z) = (e1x, e1y, e1z);
        }

        double sum1 = Math.Abs(e1x) + Math.Abs(e1y) + Math.Abs(e1z);
        double sum2 = Math.Abs(e2x) + Math.Abs(e2y) + Math.Abs(e2z);
        var (x, y, z) = sum1 > sum2 ? (e2x, e2y, e2z) : (e1x, e1y, e1z);
        return (Degrees(x), Degrees(y), Degrees(z));
    }

    private double[][] NormalizedColumns()
    {
        var result = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            double len = Length(m[c]);
            result[c] = len > 0
                ? new[] { m[c][0] / len, m[c][1] / len, m[c][2] / len }
                : new double[] { 0, 0, 0 };
        }
        return result;
    }

    private static double Length(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    public static double Degrees(double radians) => radians * (180.0 / Math.PI);
}
