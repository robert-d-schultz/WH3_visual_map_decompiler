namespace SyntheticTerrain;

public static class Metrics
{
    public static (double Rmse, double MaxErr) Compare(float[] truth, float[] other)
    {
        double sq = 0, max = 0;
        for (int i = 0; i < truth.Length; i++)
        {
            double e = Math.Abs(truth[i] - other[i]);
            sq += e * e;
            if (e > max) max = e;
        }
        return (Math.Sqrt(sq / truth.Length), max);
    }

    /// <summary>
    /// RMS angle, in degrees, between the surface normals of two height fields.
    ///
    /// This is the metric that tracks what the terrain actually looks like. Campaign terrain
    /// is shaded from the normal map, not from the geometry's own derivatives, so normal-space
    /// error is what the eye registers - a height field can have excellent RMSE and still read
    /// as visibly stepped, because BC6H's plateaus put large errors in the *derivative* while
    /// keeping the height itself close.
    /// </summary>
    public static double NormalErrorDegrees(float[] a, float[] b, int width, int height, double texelSize)
    {
        double sum = 0;
        long n = 0;
        for (int y = 0; y < height - 1; y++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                int i = y * width + x;
                var na = NormalAt(a, i, width, texelSize);
                var nb = NormalAt(b, i, width, texelSize);
                double dot = Math.Clamp(na.X * nb.X + na.Y * nb.Y + na.Z * nb.Z, -1.0, 1.0);
                double angle = Math.Acos(dot) * 180.0 / Math.PI;
                sum += angle * angle;
                n++;
            }
        }
        return Math.Sqrt(sum / n);

        static (double X, double Y, double Z) NormalAt(float[] h, int i, int width, double texelSize)
        {
            double sx = (h[i + 1] - h[i]) / texelSize;
            double sy = (h[i + width] - h[i]) / texelSize;
            double inv = 1.0 / Math.Sqrt(1.0 + sx * sx + sy * sy);
            return (-sx * inv, -sy * inv, inv);
        }
    }

    /// <summary>
    /// Mean |discrete Laplacian| - near zero on smooth/planar surfaces, spikes wherever
    /// there's staircase/bump structure. Used as a "how banded does this look" proxy.
    /// Best read as a ratio against the ground truth's own roughness: 1.0 is ideal, above
    /// means noisy, below means over-smoothed.
    /// </summary>
    public static double Roughness(float[] h, int width, int height)
    {
        double sum = 0;
        long n = 0;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                double lap = h[i - 1] + h[i + 1] + h[i - width] + h[i + width] - 4 * h[i];
                sum += Math.Abs(lap);
                n++;
            }
        }
        return sum / n;
    }
}
