namespace TwMapDecompiler.Core.Maths;

/// <summary>A double-precision 3D vector, for geometry that is fitted rather than passed through.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vec3 operator *(double k, Vec3 a) => a * k;
    public static Vec3 operator /(Vec3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public double Length => Math.Sqrt(Dot(this, this));

    /// <summary>Unit vector in the same direction; zero stays zero.</summary>
    public Vec3 Normalized()
    {
        double length = Length;
        return length > 0 ? this / length : Zero;
    }

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
}
