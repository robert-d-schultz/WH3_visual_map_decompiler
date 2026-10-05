using TwMapDecompiler.Core.Maths;

namespace TwMapDecompiler.Core.Bmd;

/// <summary>
/// The compiled map stores terrain holes as arbitrary 3D triangles, but Terry
/// defines them as 2D polylines plus an entity rotation. This rotates the
/// triangle flat onto the XZ plane and keeps the inverse rotation as Euler angles.
/// </summary>
public static class TerrainHoleMath
{
    public sealed record FlattenResult(BmdTransform Transform, BmdTransform Vertex2, BmdTransform Vertex3);

    public static FlattenResult Flatten(
        (float X, float Y, float Z) p1,
        (float X, float Y, float Z) p2,
        (float X, float Y, float Z) p3)
    {
        double[] v2 = [p2.X - (double)p1.X, p2.Y - (double)p1.Y, p2.Z - (double)p1.Z];
        double[] v3 = [p3.X - (double)p1.X, p3.Y - (double)p1.Y, p3.Z - (double)p1.Z];

        double[] normal = Cross(v2, v3);
        double normalLength = Length(normal);

        double[][] rotation;
        if (normalLength < 1e-12)
        {
            rotation = Identity(); // degenerate (zero-area) triangle: leave it as-is
        }
        else
        {
            normal = [normal[0] / normalLength, normal[1] / normalLength, normal[2] / normalLength];
            double angle = Math.Acos(Math.Clamp(normal[1], -1.0, 1.0));

            double[] axis = Cross(normal, [0, 1, 0]);
            double axisLength = Length(axis);
            if (axisLength < 1e-12)
            {
                // Normal already (anti)parallel to Y: identity, or 180 deg about X.
                rotation = Identity();
                if (normal[1] < 0)
                {
                    rotation[1][1] = -1;
                    rotation[2][2] = -1;
                }
            }
            else
            {
                axis = [axis[0] / axisLength, axis[1] / axisLength, axis[2] / axisLength];
                rotation = Rodrigues(axis, angle);
            }
        }

        double[] r2 = Multiply(rotation, v2);
        double[] r3 = Multiply(rotation, v3);

        // Euler angles of the inverse rotation (= transpose), extracted the same
        // way Blender's mathutils did in the reference script: the inverse matrix's
        // rows become the column vectors handed to the euler decomposition.
        var inverse = Mat3.FromColumns(rotation[0], rotation[1], rotation[2]);
        var (rx, ry, rz) = inverse.ToEulerXyzDegrees();

        return new FlattenResult(
            new BmdTransform(p1.X, p1.Y, p1.Z, rx, ry, rz),
            new BmdTransform(Sane(r2[0], v2[0]), Sane(r2[1], v2[1]), Sane(r2[2], v2[2])),
            new BmdTransform(Sane(r3[0], v3[0]), Sane(r3[1], v3[1]), Sane(r3[2], v3[2])));
    }

    private static double Sane(double rotated, double fallback) => double.IsNaN(rotated) ? fallback : rotated;

    private static double[][] Identity() =>
    [
        [1.0, 0, 0],
        [0, 1.0, 0],
        [0, 0, 1.0],
    ];

    /// <summary>Rodrigues rotation matrix for the given unit axis and angle.</summary>
    private static double[][] Rodrigues(double[] a, double angle)
    {
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        double t = 1 - c;
        return
        [
            [c + a[0] * a[0] * t, a[0] * a[1] * t - s * a[2], a[0] * a[2] * t + s * a[1]],
            [a[1] * a[0] * t + s * a[2], c + a[1] * a[1] * t, a[1] * a[2] * t - s * a[0]],
            [a[2] * a[0] * t - s * a[1], a[2] * a[1] * t + s * a[0], c + a[2] * a[2] * t],
        ];
    }

    private static double[] Multiply(double[][] m, double[] v) =>
    [
        m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
        m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
        m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
    ];

    private static double[] Cross(double[] a, double[] b) =>
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ];

    private static double Length(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
}
