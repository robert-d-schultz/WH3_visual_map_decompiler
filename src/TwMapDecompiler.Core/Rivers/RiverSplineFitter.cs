using TwMapDecompiler.Core.Maths;

namespace TwMapDecompiler.Core.Rivers;

/// <summary>One point of a Terry river spline (ECRiverSpline), in the river entity's frame.</summary>
/// <param name="TangentIn">Handle towards the previous point, relative to <paramref name="Position"/>.</param>
/// <param name="TangentOut">Handle towards the next point, relative to <paramref name="Position"/>.</param>
public sealed record RiverPoint(Vec3 Position, Vec3 TangentIn, Vec3 TangentOut, double Width);

/// <summary>
/// Fits a Terry river spline to the rows of a baked river mesh (<see cref="RiverMesh"/>).
///
/// BOB builds a river from its spline alone (WARSCAPE::tessellate_spline): segment i is the cubic
/// Bézier p[i], p[i] + tangent_out[i], p[i+1] + tangent_in[i+1], p[i+1]. The width follows a cubic
/// Bézier with control values w0, 0.75·w0 + 0.25·w1, 0.25·w0 + 0.75·w1, w1, both clamped to at least
/// 0.01. That is <see cref="WidthAt"/>. flow_speed, alpha_fade, foam_amount and terrain_offset never
/// reach the mesh.
///
/// The fit puts its points on mesh rows: the first and last row, then greedily the worst-fitting
/// row of every segment until each row is within the tolerance of the curve, in position and in
/// width. Then it drops every point the tolerance does without. Handles lie along the mesh's own
/// direction at the row and are a third of the chord to the neighbouring point long. So each
/// segment depends only on its two points, and the curve is smooth through every point, the way
/// Terry draws it. The first point's in-handle mirrors its out-handle, and the last point's
/// out-handle is zero, as in the splines Terry saves.
/// </summary>
public static class RiverSplineFitter
{
    /// <summary>Default largest distance, in world units, a mesh row may be off the fitted spline.</summary>
    public const double DefaultTolerance = 0.05;

    /// <summary>Sample spacing along a segment when measuring its fit.</summary>
    private const double SampleSpacing = 0.01;

    public static List<RiverPoint> Fit(IReadOnlyList<Vec3> centres, IReadOnlyList<double> widths, double tolerance = DefaultTolerance)
    {
        if (centres.Count < 2 || widths.Count != centres.Count)
            throw new ArgumentException("a river needs at least two rows, each with a width");

        var directions = Directions(centres);
        var knots = new List<int> { 0, centres.Count - 1 };
        while (true)
        {
            var added = new List<int>();
            for (int j = 0; j + 1 < knots.Count; j++)
            {
                var (error, worst) = SegmentError(centres, widths, directions, knots[j], knots[j + 1]);
                if (error > tolerance)
                    added.Add(worst);
            }
            if (added.Count == 0)
                break;
            knots.AddRange(added);
            knots.Sort();
        }

        for (int j = 1; j + 1 < knots.Count;)
        {
            if (SegmentError(centres, widths, directions, knots[j - 1], knots[j + 1]).Error <= tolerance)
                knots.RemoveAt(j);
            else
                j++;
        }

        var points = new List<RiverPoint>(knots.Count);
        for (int j = 0; j < knots.Count; j++)
        {
            int k = knots[j];
            var d = directions[k];
            double next = j + 1 < knots.Count ? Vec3.Distance(centres[knots[j + 1]], centres[k]) : 0;
            double previous = j > 0 ? Vec3.Distance(centres[k], centres[knots[j - 1]]) : next;
            points.Add(new RiverPoint(centres[k], -d * (previous / 3), d * (next / 3), widths[k]));
        }
        return points;
    }

    /// <summary>The width BOB gives a segment from width <paramref name="w0"/> to <paramref name="w1"/> at Bézier parameter <paramref name="t"/>.</summary>
    public static double WidthAt(double w0, double w1, double t)
    {
        w0 = Math.Max(w0, 0.01);
        w1 = Math.Max(w1, 0.01);
        double smooth = t * t * (3 - 2 * t);
        return w0 + (w1 - w0) * (0.75 * t + 0.25 * smooth);
    }

    /// <summary>A point of the cubic Bézier p0, p1, p2, p3.</summary>
    public static Vec3 Bezier(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double t)
    {
        double s = 1 - t;
        return p0 * (s * s * s) + p1 * (3 * s * s * t) + p2 * (3 * s * t * t) + p3 * (t * t * t);
    }

    /// <summary>The river's direction at each row, from its neighbours (one-sided, second order, at the ends).</summary>
    private static Vec3[] Directions(IReadOnlyList<Vec3> c)
    {
        int n = c.Count;
        var d = new Vec3[n];
        if (n == 2)
        {
            d[0] = d[1] = (c[1] - c[0]).Normalized();
            return d;
        }
        for (int k = 1; k < n - 1; k++)
            d[k] = (c[k + 1] - c[k - 1]).Normalized();
        d[0] = (c[1] * 4 - c[0] * 3 - c[2]).Normalized();
        d[n - 1] = (c[n - 1] * 3 - c[n - 2] * 4 + c[n - 3]).Normalized();
        return d;
    }

    /// <summary>
    /// How far the rows strictly between rows <paramref name="a"/> and <paramref name="b"/> are off the
    /// segment joining them (the larger of the distance and the width difference), and the worst row.
    /// </summary>
    private static (double Error, int Worst) SegmentError(IReadOnlyList<Vec3> c, IReadOnlyList<double> w, Vec3[] d, int a, int b)
    {
        if (b - a < 2)
            return (0, -1);

        double chord = Vec3.Distance(c[a], c[b]);
        Vec3 p0 = c[a], p1 = c[a] + d[a] * (chord / 3), p2 = c[b] - d[b] * (chord / 3), p3 = c[b];
        double arc = 0;
        for (int k = a; k < b; k++)
            arc += Vec3.Distance(c[k], c[k + 1]);
        int n = Math.Max(16, (int)Math.Ceiling(arc / SampleSpacing));
        var q = new Vec3[n + 1];
        var qw = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            q[i] = Bezier(p0, p1, p2, p3, t);
            qw[i] = WidthAt(w[a], w[b], t);
        }

        double worstError = 0;
        int worst = -1;
        for (int k = a + 1; k < b; k++)
        {
            // Nearest point on the sampled curve, and the width there.
            double best = double.MaxValue, bestWidth = 0;
            for (int i = 0; i < n; i++)
            {
                var edge = q[i + 1] - q[i];
                double len2 = Vec3.Dot(edge, edge);
                double s = len2 > 0 ? Math.Clamp(Vec3.Dot(c[k] - q[i], edge) / len2, 0, 1) : 0;
                double dist = Vec3.Distance(c[k], q[i] + edge * s);
                if (dist < best)
                {
                    best = dist;
                    bestWidth = qw[i] + (qw[i + 1] - qw[i]) * s;
                }
            }
            double error = Math.Max(best, Math.Abs(bestWidth - w[k]));
            if (error > worstError)
                (worstError, worst) = (error, k);
        }
        return (worstError, worst);
    }
}
