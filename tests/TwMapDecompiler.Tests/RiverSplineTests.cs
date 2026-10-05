using TwMapDecompiler.Core.Maths;
using TwMapDecompiler.Core.Rivers;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// Baked river meshes back to Terry river splines: reading the rows (RiverMesh) and fitting the
/// spline (RiverSplineFitter), against rivers tessellated here the way BOB does it.
/// </summary>
public sealed class RiverSplineTests
{
    [Fact]
    public void WidthEasesBetweenPointsAndIsClamped()
    {
        Assert.Equal(1, RiverSplineFitter.WidthAt(1, 3, 0), 9);
        Assert.Equal(3, RiverSplineFitter.WidthAt(1, 3, 1), 9);
        Assert.Equal(2, RiverSplineFitter.WidthAt(1, 3, 0.5), 9);
        // Measured on IEE's baked rivers: 0.75·t + 0.25·smoothstep(t) of the way at t = 0.25.
        Assert.Equal(1 + 2 * (0.75 * 0.25 + 0.25 * 0.15625), RiverSplineFitter.WidthAt(1, 3, 0.25), 9);
        Assert.Equal(0.01, RiverSplineFitter.WidthAt(0, 1, 0), 9);
    }

    [Fact]
    public void FitReproducesATessellatedRiver()
    {
        // An S-bend that narrows and widens again, falling as it goes.
        var original = new[]
        {
            new RiverPoint(new Vec3(0, 0, 0), new Vec3(-1, 0, 0), new Vec3(1, 0, 0), 2),
            new RiverPoint(new Vec3(4, -0.5, 3), new Vec3(-1.5, 0.1, -0.5), new Vec3(1.5, -0.1, 0.5), 1),
            new RiverPoint(new Vec3(9, -1, 1), new Vec3(-1, 0, 1), new Vec3(1, 0, -1), 1),
            new RiverPoint(new Vec3(12, -1.2, -4), new Vec3(-0.5, 0.05, 1.5), Vec3.Zero, 3),
        };
        var (centres, widths) = Tessellate(original);

        var fitted = RiverSplineFitter.Fit(centres, widths, 0.01);

        Assert.True(fitted.Count < centres.Length / 4, $"{fitted.Count} points for {centres.Length} rows");
        Assert.Equal(centres[0], fitted[0].Position);
        Assert.Equal(centres[^1], fitted[^1].Position);
        Assert.Equal(Vec3.Zero, fitted[^1].TangentOut);
        var (curve, curveWidths) = Sample(fitted, 2000);
        for (int k = 0; k < centres.Length; k++)
        {
            int nearest = Enumerable.Range(0, curve.Length).MinBy(i => Vec3.Distance(curve[i], centres[k]));
            Assert.True(Vec3.Distance(curve[nearest], centres[k]) < 0.011, $"row {k} is {Vec3.Distance(curve[nearest], centres[k])} off");
            Assert.True(Math.Abs(curveWidths[nearest] - widths[k]) < 0.011, $"row {k} width {curveWidths[nearest]}, mesh {widths[k]}");
        }
    }

    [Fact]
    public void RowsComeOutInFlowOrderAboutTheBoundingBoxCentre()
    {
        // Three rows of three vertices across a river running along +x, 2 wide, written last row
        // first, the way a reverse_direction river's highest v can come first.
        var rows = new[] { (X: 0.0, V: 0f), (X: 0.2, V: 0.02f), (X: 0.4, V: 0.04f) };
        var vertices = new List<(Vec3 P, float U, float V)>();
        foreach (var (x, v) in rows.Reverse())
            foreach (double side in new[] { -1.0, 0, 1 })
                vertices.Add((new Vec3(x, 5 - x, side), (float)(side / 10), v));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Rmv2(vertices));
            var (centres, widths) = RiverMesh.ReadRows(path);

            Assert.Equal(3, centres.Length);
            for (int r = 0; r < 3; r++)
            {
                Assert.Equal(rows[r].X, centres[r].X, 5);
                Assert.Equal(5 - rows[r].X, centres[r].Y, 5);
                Assert.Equal(0, centres[r].Z, 5);
                Assert.Equal(2, widths[r], 5);
            }

            // A mesh whose u doesn't span its width is no baked river.
            File.WriteAllBytes(path, Rmv2(vertices.Select(v => (v.P, v.U * 2, v.V)).ToList()));
            Assert.Throws<InvalidDataException>(() => RiverMesh.ReadRows(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>BOB's tessellation: rows every ~0.2 of arc length, each with the spline's width there.</summary>
    private static (Vec3[] Centres, double[] Widths) Tessellate(IReadOnlyList<RiverPoint> spline)
    {
        var (curve, curveWidths) = Sample(spline, 20000);
        var arc = new double[curve.Length];
        for (int i = 1; i < curve.Length; i++)
            arc[i] = arc[i - 1] + Vec3.Distance(curve[i], curve[i - 1]);
        int steps = (int)Math.Ceiling(arc[^1] / 0.2);
        var centres = new Vec3[steps + 1];
        var widths = new double[steps + 1];
        for (int s = 0, i = 0; s <= steps; s++)
        {
            double target = arc[^1] * s / steps;
            while (i < curve.Length - 1 && arc[i] < target)
                i++;
            (centres[s], widths[s]) = (curve[i], curveWidths[i]);
        }
        return (centres, widths);
    }

    private static (Vec3[] Points, double[] Widths) Sample(IReadOnlyList<RiverPoint> spline, int perSegment)
    {
        var points = new List<Vec3>();
        var widths = new List<double>();
        for (int j = 0; j + 1 < spline.Count; j++)
        {
            var (a, b) = (spline[j], spline[j + 1]);
            for (int i = 0; i <= perSegment; i++)
            {
                double t = (double)i / perSegment;
                points.Add(RiverSplineFitter.Bezier(a.Position, a.Position + a.TangentOut, b.Position + b.TangentIn, b.Position, t));
                widths.Add(RiverSplineFitter.WidthAt(a.Width, b.Width, t));
            }
        }
        return (points.ToArray(), widths.ToArray());
    }

    /// <summary>A one-mesh RMV2 v8 file as BOB writes rivers: 48-byte vertices relative to the bounding box centre.</summary>
    private static byte[] Rmv2(IReadOnlyList<(Vec3 P, float U, float V)> vertices)
    {
        var min = new Vec3(vertices.Min(v => v.P.X), vertices.Min(v => v.P.Y), vertices.Min(v => v.P.Z));
        var max = new Vec3(vertices.Max(v => v.P.X), vertices.Max(v => v.P.Y), vertices.Max(v => v.P.Z));
        var pivot = (min + max) / 2;
        const int meshStart = 0xA8, meshHeader = 48;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RMV2"u8);
        w.Write(8u);                              // version
        w.Write(1u);                              // LODs
        w.Write(new byte[128]);                   // skeleton name
        w.Write(1u);                              // meshes in the LOD
        w.Write((uint)(vertices.Count * 48));     // vertex bytes
        w.Write(0u);                              // index bytes
        w.Write((uint)meshStart);
        w.Write(1e6f);                            // visibility distance
        w.Write(0u);
        w.Write(0u);
        w.Write(68u);                             // material
        w.Write((uint)(meshHeader + vertices.Count * 48));
        w.Write((uint)meshHeader);
        w.Write((uint)vertices.Count);
        w.Write((uint)(meshHeader + vertices.Count * 48));
        w.Write(0u);                              // indices
        foreach (var v in new[] { min, max })
        {
            w.Write((float)v.X);
            w.Write((float)v.Y);
            w.Write((float)v.Z);
        }
        foreach (var (p, u, v) in vertices)
        {
            var local = p - pivot;
            w.Write((float)local.X);
            w.Write((float)local.Y);
            w.Write((float)local.Z);
            w.Write(1f);
            w.Write(u);
            w.Write(v);
            w.Write(new byte[24]);                // uv2, normal, tangent, bitangent, colour
        }
        return stream.ToArray();
    }
}
