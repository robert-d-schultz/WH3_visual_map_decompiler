namespace HeightReconstructionBench;

/// <summary>
/// The pre-2026-08 reconstruction, kept only as an A/B baseline for the bench.
///
/// It posed the problem as an unconstrained weighted Poisson solve: texels whose
/// neighbour decoded to a different quantized level became "anchors" pulled toward the
/// midpoint of the two levels with weight <c>anchorWeight</c>, every other texel got a
/// weak pull toward its raw decoded value, and the normal map's gradient was integrated
/// between them. Two hand-tuned weights, and nothing bounded how far the result could
/// drift from what the height map actually said.
///
/// Note this reads the normal with the OLD channel assignment (X from green, Y from
/// alpha), which is what shipped; the bench uses that to show what the swap costs.
/// </summary>
public static class LegacyAnchored
{
    public static float[] Reconstruct(float[] quantizedHeight, int width, int height, byte[] normalRgba,
        double texelSize, double anchorWeight = 20.0, double weakPriorWeight = 0.05, bool swappedChannels = true)
    {
        int n = width * height;
        var gx = new double[n];
        var gy = new double[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            double nx = normalRgba[o + (swappedChannels ? 1 : 3)] / 127.5 - 1.0;
            double ny = normalRgba[o + (swappedChannels ? 3 : 1)] / 127.5 - 1.0;
            double nz = Math.Sqrt(Math.Max(1.0 - nx * nx - ny * ny, 0.05 * 0.05));
            gx[i] = -nx / nz * texelSize;
            gy[i] = -ny / nz * texelSize;
        }

        var sum = new double[n];
        var cnt = new int[n];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width - 1; x++)
            {
                int i = y * width + x;
                if (quantizedHeight[i] != quantizedHeight[i + 1])
                {
                    double mid = (quantizedHeight[i] + quantizedHeight[i + 1]) * 0.5;
                    sum[i] += mid; cnt[i]++;
                    sum[i + 1] += mid; cnt[i + 1]++;
                }
            }
        for (int y = 0; y < height - 1; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, j = i + width;
                if (quantizedHeight[i] != quantizedHeight[j])
                {
                    double mid = (quantizedHeight[i] + quantizedHeight[j]) * 0.5;
                    sum[i] += mid; cnt[i]++;
                    sum[j] += mid; cnt[j]++;
                }
            }

        var weight = new double[n];
        var target = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool anchor = cnt[i] > 0;
            weight[i] = anchor ? anchorWeight : weakPriorWeight;
            target[i] = anchor ? sum[i] / cnt[i] : quantizedHeight[i];
        }

        var degree = new double[n];
        var b = new double[n];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                double deg = 0, bi = 0;
                if (x < width - 1) { deg++; bi -= gx[i]; }
                if (x > 0) { deg++; bi += gx[i - 1]; }
                if (y < height - 1) { deg++; bi -= gy[i]; }
                if (y > 0) { deg++; bi += gy[i - width]; }
                degree[i] = deg;
                b[i] = bi + weight[i] * target[i];
            }

        var x0 = new double[n];
        for (int i = 0; i < n; i++) x0[i] = quantizedHeight[i];

        void ApplyA(double[] v, double[] outVec)
        {
            for (int y = 0; y < height; y++)
                for (int col = 0; col < width; col++)
                {
                    int i = y * width + col;
                    double s = 0;
                    if (col > 0) s += v[i - 1];
                    if (col < width - 1) s += v[i + 1];
                    if (y > 0) s += v[i - width];
                    if (y < height - 1) s += v[i + width];
                    outVec[i] = (degree[i] + weight[i]) * v[i] - s;
                }
        }

        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];
        var pre = new double[n];
        for (int i = 0; i < n; i++) pre[i] = 1.0 / (degree[i] + weight[i]);

        ApplyA(x0, ap);
        for (int i = 0; i < n; i++) r[i] = b[i] - ap[i];
        double bNorm = Math.Sqrt(b.Sum(v => v * v));
        if (bNorm < 1e-12) bNorm = 1.0;
        for (int i = 0; i < n; i++) z[i] = r[i] * pre[i];
        Array.Copy(z, p, n);
        double rzOld = r.Zip(z, (a, c) => a * c).Sum();

        for (int iter = 0; iter < 500; iter++)
        {
            ApplyA(p, ap);
            double pAp = p.Zip(ap, (a, c) => a * c).Sum();
            if (Math.Abs(pAp) < 1e-300) break;
            double alpha = rzOld / pAp;
            for (int i = 0; i < n; i++) { x0[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            if (Math.Sqrt(r.Sum(v => v * v)) / bNorm < 1e-6) break;
            for (int i = 0; i < n; i++) z[i] = r[i] * pre[i];
            double rzNew = r.Zip(z, (a, c) => a * c).Sum();
            double beta = rzNew / rzOld;
            for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
            rzOld = rzNew;
        }

        var result = new float[n];
        for (int i = 0; i < n; i++) result[i] = (float)x0[i];
        return result;
    }
}
