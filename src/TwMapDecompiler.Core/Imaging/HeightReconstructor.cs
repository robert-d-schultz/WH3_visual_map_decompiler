namespace TwMapDecompiler.Core.Imaging;

/// <summary>
/// Reconstructs a smooth float32 height field from the BC6H-compressed height map and the
/// accompanying normal map (lf_normal.dds).
///
/// The guiding observation is how these two textures behave in game: the geometry really is
/// stepped by BC6H's 4x4 block quantization, but you never see the steps, because the
/// terrain is *shaded* from the normal map. "Looks smooth and about right" is the normal
/// map's doing. So the target here is a height field whose own derived normals match
/// lf_normal.dds, with the height map supplying only what a gradient field fundamentally
/// cannot know: absolute elevation and long-wavelength shape.
///
/// Measuring the two against each other on a real map (see the coherence table below)
/// shows they carry information in different bands, so the solve gives each band to
/// whichever source actually knows something there:
///
///     minimise  ||grad(h) - g||^2  +  wd*||h - hq||^2  +  ws*||laplacian(h)||^2
///                     |                     |                      |
///               normal map            height map               smoothness
///              (mid scales)        (long wavelengths)        (finest scales)
///
/// In the frequency domain that is a three-way crossover. Writing s for the Laplacian's
/// eigenvalue at a given wavelength, the solution is
///
///     h(f) = [ s*h_gradient(f) + wd*h_quantized(f) ] / [ s + wd + ws*s^2 ]
///
/// so the height map wins as s -> 0 (long wavelengths, where integrating a noisy gradient
/// would drift), the normal map wins in the middle, and the ws term takes over at the
/// finest scales - where, crucially, *neither* input knows anything, so the honest output
/// is a smooth surface rather than either one's artifacts.
///
/// Measured coherence between the normal's slope and the height map's finite differences
/// on wh3_main_combi_map_1, which is what sets the crossovers:
///
///     wavelength   256tx   128tx   64tx   32tx   &lt;=16tx
///     coherence    0.97    0.96    0.63   0.22   ~0.00
///
/// Below ~16 texels they agree about nothing: the height map is all BC6H staircase, and the
/// normal map's curl (0.73) exceeds its own gradient variation (0.42), meaning it is not a
/// consistent surface there either. Integrating it at those scales manufactures detail that
/// was never in the data.
///
/// Solved as a sparse SPD system by matrix-free conjugate gradients; the unknown is the
/// correction h - hq, which starts at zero and stays small enough for float32 storage.
/// </summary>
public static class HeightReconstructor
{
    public sealed record Options
    {
        /// <summary>
        /// World units per texel, converting the normal's dimensionless slope into height
        /// units per texel. Null (the default) fits it from the two maps themselves, which
        /// matters because the decompiler otherwise derives it from world_width - often only
        /// known as a "height map width / 12" guess, which on wh3_main_combi_map_1 is 0.083
        /// against a fitted 0.024.
        /// </summary>
        public double? TexelSize { get; init; }

        /// <summary>
        /// Wavelength, in texels, below which the solve stops trusting either input and
        /// simply produces a smooth surface. 3 texels measured best across the synthetic
        /// scenes; it is a real optimum rather than a plateau, because raising it starts
        /// rounding off genuine relief and lowering it lets the normal map's compression
        /// noise back into the geometry.
        /// </summary>
        public double SmoothingCutoffTexels { get; init; } = 3.0;

        /// <summary>
        /// Wavelength, in texels, above which the height map rather than the gradient field
        /// determines shape. The result is remarkably insensitive to this - anything from 32
        /// to 128 gives the same answer to three digits - because the anchor term below
        /// already carries the height map's information. Its real job is pinning the
        /// additive constant that a gradient field alone leaves undetermined.
        /// </summary>
        public double HeightCrossoverTexels { get; init; } = 64.0;

        /// <summary>
        /// Pull toward the level-transition midpoint at anchor texels. Where two neighbouring
        /// texels decode to different quantized levels, the true surface crossed the boundary
        /// between them nearby, so their midpoint is a better estimate than either raw value -
        /// genuine extra information, and the one part of the height map that survives at fine
        /// scales.
        ///
        /// The optimum is broad and agrees between synthetic ground truth and real map data
        /// (anything from 0.25 to 0.6 lands within 0.25 degrees of best on wh3_main_combi_map_1).
        /// Dropping to zero costs a lot - the solve then has nothing holding it to the height
        /// map at fine scales and drifts - and so does raising it: at 5, which is what shipped
        /// before the smoothing term existed, real terrain is about 40% worse in normal-space.
        /// </summary>
        public double AnchorWeight { get; init; } = 0.5;

        /// <summary>
        /// Hard limit on how far a texel may move, in multiples of its BC6H block's level
        /// spacing. A safety net for the case where the normal map does not match the height
        /// map at all; non-binding on good data.
        /// </summary>
        public double SafetyClampSteps { get; init; } = 8.0;

        public int MaxIterations { get; init; } = 600;
    }

    private const double MinNormalZ = 0.05; // clamps the gradient magnitude near-vertical normals would otherwise blow up
    private const double RelativeTolerance = 1e-6;
    private const int LogEveryIterations = 25;

    /// <param name="quantizedHeight">BC6H-decoded height, row-major, length width*height.</param>
    /// <param name="normalRgba">RGBA8-decoded lf_normal.dds.</param>
    /// <param name="texelSizeHint">Fallback world-units-per-texel, used only when calibration
    /// is disabled or finds too little coherent signal to trust.</param>
    public static float[] Reconstruct(float[] quantizedHeight, int width, int height, byte[] normalRgba,
        double texelSizeHint, Action<string>? log = null, Action<double>? progress = null, Options? options = null)
    {
        var opt = options ?? new Options();
        int n = width * height;

        double texelSize = opt.TexelSize ?? CalibrateTexelSize(quantizedHeight, width, height, normalRgba, texelSizeHint, log);
        progress?.Invoke(0.05);

        // Convert the two crossover wavelengths into weights on the Laplacian's spectrum.
        // The gradient term contributes s, the smoothing term ws*s^2, and they balance at
        // s = 1/ws; the height term contributes wd and balances the gradient at s = wd.
        double wd = Math.Pow(2 * Math.PI / opt.HeightCrossoverTexels, 2);
        double ws = opt.SmoothingCutoffTexels <= 0 ? 0 : Math.Pow(opt.SmoothingCutoffTexels / (2 * Math.PI), 2);

        DetectAnchors(quantizedHeight, width, height, opt.AnchorWeight, out float[] anchorWeight, out float[] anchorOffset, log);
        progress?.Invoke(0.10);

        var b = new float[n];
        BuildCorrectionRhs(quantizedHeight, normalRgba, anchorWeight, anchorOffset, width, height, texelSize, ws, b);
        anchorOffset = null!; // large and no longer needed; let the solve have the memory
        progress?.Invoke(0.15);

        var correction = new float[n];
        SolveConjugateGradient(correction, b, anchorWeight, wd, ws, width, height, opt.MaxIterations, log,
            f => progress?.Invoke(0.15 + f * 0.80));

        var result = new float[n];
        for (int i = 0; i < n; i++)
            result[i] = quantizedHeight[i] + correction[i];

        ApplySafetyClamp(result, quantizedHeight, width, height, opt.SafetyClampSteps, log);
        progress?.Invoke(1.0);
        return result;
    }

    // ------------------------------------------------------------------ normal map

    /// <summary>
    /// Decodes one texel of the DXT5nm normal into a dimensionless surface slope.
    ///
    /// The channel assignment is easy to get backwards and silently catastrophic. CA ships
    /// the standard DXT5nm packing - <b>X in alpha, Y in green</b>, with R a constant 255
    /// and B a constant 0 - because BC3 keeps alpha in its own higher-precision block.
    /// Reading X from green instead drops the correlation between this slope and the decoded
    /// height map's finite differences from 0.65 to 0.001 on wh3_main_combi_map_1: the
    /// gradient field becomes noise, and the solve then integrates that noise. Synthetic
    /// round-trip tests cannot catch this on their own, since they pack the normal with
    /// whatever convention they also read back.
    /// </summary>
    private static (double SlopeX, double SlopeY) DecodeSlope(byte[] normalRgba, long i)
    {
        long o = i * 4;
        double nx = normalRgba[o + 3] / 127.5 - 1.0; // X in alpha
        double ny = normalRgba[o + 1] / 127.5 - 1.0; // Y in green
        double nz = Math.Sqrt(Math.Max(1.0 - nx * nx - ny * ny, MinNormalZ * MinNormalZ));
        return (-nx / nz, -ny / nz);
    }

    /// <summary>
    /// Fits world-units-per-texel by regressing the height map's own finite differences on
    /// the normal map's slopes, using the total-least-squares estimate because both are noisy.
    ///
    /// Measured over a long baseline rather than adjacent texels, which matters a great deal:
    /// at one texel the two agree about nothing (the height map is all BC6H staircase there),
    /// and including that band biases the fit high - on wh3_main_combi_map_1 an adjacent-texel
    /// fit gives 0.048 where the coherent wavelengths all agree on 0.022-0.027.
    /// </summary>
    public static double CalibrateTexelSize(float[] hq, int width, int height, byte[] normalRgba,
        double fallback, Action<string>? log)
    {
        const int baseline = 32;    // texels; comfortably inside the coherent band
        const double minSlope = 0.1, maxSlope = 5.0;
        int stride = Math.Max(1, height / 512);

        if (width <= baseline * 2 || height <= baseline * 2)
            return fallback;

        double sxx = 0, sxy = 0, syy = 0;
        long count = 0;
        for (int y = baseline; y < height - baseline; y += stride)
        {
            long row = (long)y * width;
            for (int x = baseline; x < width - baseline; x++)
            {
                long i = row + x;

                // Mean slope over the baseline, against the height change across it.
                double sumX = 0, sumY = 0;
                for (int k = 0; k < baseline; k++)
                {
                    sumX += DecodeSlope(normalRgba, i + k).SlopeX;
                    sumY += DecodeSlope(normalRgba, i + (long)k * width).SlopeY;
                }
                double slopeX = sumX / baseline, slopeY = sumY / baseline;

                if (Math.Abs(slopeX) > minSlope && Math.Abs(slopeX) < maxSlope)
                {
                    double dh = (hq[i + baseline] - hq[i]) / baseline;
                    sxx += slopeX * slopeX; sxy += slopeX * dh; syy += dh * dh; count++;
                }
                if (Math.Abs(slopeY) > minSlope && Math.Abs(slopeY) < maxSlope)
                {
                    double dh = (hq[i + (long)baseline * width] - hq[i]) / baseline;
                    sxx += slopeY * slopeY; sxy += slopeY * dh; syy += dh * dh; count++;
                }
            }
        }

        if (count < 1000 || sxx <= 0 || syy <= 0)
        {
            log?.Invoke($"height reconstruction: too little signal to calibrate texel size, using {fallback:0.#####}");
            return fallback;
        }

        double correlation = sxy / Math.Sqrt(sxx * syy);
        double fitted = Math.Sqrt(syy / sxx);
        if (correlation < 0.2)
        {
            log?.Invoke($"height reconstruction: lf_normal.dds correlates poorly with the height map (r={correlation:0.###}) - " +
                        $"they may not belong to the same bake; using texel size {fallback:0.#####} rather than the fitted {fitted:0.#####}");
            return fallback;
        }

        log?.Invoke($"height reconstruction: texel size fitted to {fitted:0.#####} over a {baseline}-texel baseline " +
                    $"(r={correlation:0.###}, {count:N0} samples; hint was {fallback:0.#####})");
        return fitted;
    }

    // ------------------------------------------------------------------ anchors

    /// <summary>
    /// A texel is an anchor when a neighbour decodes to a different quantized level; its
    /// target is the average of the level-transition midpoints touching it. Returns the
    /// per-texel weight and the offset of that target from the raw decoded value.
    /// </summary>
    private static void DetectAnchors(float[] hq, int width, int height, double anchorWeight,
        out float[] weight, out float[] anchorOffset, Action<string>? log)
    {
        int n = width * height;
        var sum = new double[n];
        var count = new byte[n];

        for (int y = 0; y < height; y++)
        {
            long row = (long)y * width;
            for (int x = 0; x < width - 1; x++)
            {
                long i = row + x;
                if (hq[i] != hq[i + 1])
                {
                    double mid = (hq[i] + hq[i + 1]) * 0.5;
                    sum[i] += mid; count[i]++;
                    sum[i + 1] += mid; count[i + 1]++;
                }
            }
        }
        for (int y = 0; y < height - 1; y++)
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                long i = row + x, j = i + width;
                if (hq[i] != hq[j])
                {
                    double mid = (hq[i] + hq[j]) * 0.5;
                    sum[i] += mid; count[i]++;
                    sum[j] += mid; count[j]++;
                }
            }
        }

        weight = new float[n];
        anchorOffset = new float[n];
        long anchors = 0;
        for (int i = 0; i < n; i++)
        {
            if (count[i] > 0)
            {
                weight[i] = (float)anchorWeight;
                anchorOffset[i] = (float)(sum[i] / count[i] - hq[i]);
                anchors++;
            }
        }
        log?.Invoke($"height reconstruction: {anchors:N0}/{n:N0} texels ({100.0 * anchors / n:0.#}%) anchored at quantization transitions");
    }

    /// <summary>Number of 4-neighbours a texel has, i.e. the graph Laplacian's diagonal.</summary>
    private static double Degree(int x, int y, int width, int height) =>
        (x > 0 ? 1 : 0) + (x < width - 1 ? 1 : 0) + (y > 0 ? 1 : 0) + (y < height - 1 ? 1 : 0);

    /// <summary>Graph Laplacian: result = degree*v - sum(neighbours).</summary>
    private static void Laplacian(float[] v, float[] result, int width, int height)
    {
        Parallel.For(0, height, y =>
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                long i = row + x;
                double neighbours = 0;
                if (x > 0) neighbours += v[i - 1];
                if (x < width - 1) neighbours += v[i + 1];
                if (y > 0) neighbours += v[i - width];
                if (y < height - 1) neighbours += v[i + width];
                result[i] = (float)(Degree(x, y, width, height) * v[i] - neighbours);
            }
        });
    }

    /// <summary>
    /// Builds the right-hand side in correction space:
    ///     b = div(g) - L*hq - ws*L^2*hq + anchorWeight*anchorOffset
    /// The uniform wd term contributes nothing here because its target is hq itself, so the
    /// correction it asks for is zero. The gradient field is decoded inline so a second
    /// full-resolution buffer for it never has to exist.
    /// </summary>
    private static void BuildCorrectionRhs(float[] hq, byte[] normalRgba, float[] anchorWeight, float[] anchorOffset,
        int width, int height, double texelSize, double ws, float[] b)
    {
        int n = width * height;

        Parallel.For(0, height, y =>
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                long i = row + x;
                double h = hq[i];
                double acc = 0;
                var (slopeX, slopeY) = DecodeSlope(normalRgba, i);

                if (x < width - 1)
                {
                    acc -= slopeX * texelSize;                  // -gx[i]
                    acc -= h - hq[i + 1];                       // -(L*hq)
                }
                if (x > 0)
                {
                    var (sxPrev, _) = DecodeSlope(normalRgba, i - 1);
                    acc += sxPrev * texelSize;                  // +gx[i-1]
                    acc -= h - hq[i - 1];
                }
                if (y < height - 1)
                {
                    acc -= slopeY * texelSize;                  // -gy[i]
                    acc -= h - hq[i + width];
                }
                if (y > 0)
                {
                    var (_, syPrev) = DecodeSlope(normalRgba, i - width);
                    acc += syPrev * texelSize;                  // +gy[i-width]
                    acc -= h - hq[i - width];
                }

                b[i] = (float)(acc + anchorWeight[i] * anchorOffset[i]);
            }
        });

        if (ws <= 0)
            return;

        // Subtract ws * L^2 * hq. Two Laplacian passes; the scratch buffers are released
        // before the solve allocates its own.
        var lhq = new float[n];
        Laplacian(hq, lhq, width, height);
        var l2hq = new float[n];
        Laplacian(lhq, l2hq, width, height);
        Parallel.For(0, n, i => b[i] = (float)(b[i] - ws * l2hq[i]));
    }

    // ------------------------------------------------------------------ solver

    /// <summary>
    /// Applies A = L + wd*I + ws*L^2 + diag(anchorWeight) to v.
    /// </summary>
    private static void ApplyA(float[] v, float[] result, float[] scratch, float[] anchorWeight,
        double wd, double ws, int width, int height)
    {
        Laplacian(v, result, width, height);
        if (ws > 0)
        {
            Laplacian(result, scratch, width, height); // scratch = L^2 v
            Parallel.For(0, v.Length, i => result[i] = (float)(result[i] + ws * scratch[i]));
        }
        Parallel.For(0, v.Length, i => result[i] = (float)(result[i] + (wd + anchorWeight[i]) * v[i]));
    }

    /// <summary>
    /// Jacobi-preconditioned conjugate gradients. Vectors are float32 - in correction space
    /// they hold small values around zero, so that costs no meaningful accuracy and roughly
    /// halves the working set on a full campaign map - while every reduction accumulates in
    /// double.
    /// </summary>
    private static void SolveConjugateGradient(float[] x, float[] b, float[] anchorWeight,
        double wd, double ws, int width, int height, int maxIterations, Action<string>? log, Action<double>? progress)
    {
        int n = x.Length;
        var r = (float[])b.Clone(); // x starts at zero, so the initial residual is just b
        var z = new float[n];
        var p = new float[n];
        var ap = new float[n];
        var scratch = ws > 0 ? new float[n] : [];

        // Diagonal of A. For the graph Laplacian, diag(L^2) = degree^2 + degree.
        var precond = new float[n];
        Parallel.For(0, height, y =>
        {
            long row = (long)y * width;
            for (int col = 0; col < width; col++)
            {
                long i = row + col;
                double deg = Degree(col, y, width, height);
                precond[i] = (float)(1.0 / (deg + wd + ws * (deg * deg + deg) + anchorWeight[i]));
            }
        });

        double bNorm = Math.Sqrt(Dot(b, b));
        if (bNorm < 1e-12)
        {
            log?.Invoke("height reconstruction: nothing to correct (empty right-hand side)");
            progress?.Invoke(1.0);
            return;
        }

        Parallel.For(0, n, i => z[i] = r[i] * precond[i]);
        Array.Copy(z, p, n);
        double rzOld = Dot(r, z);

        // The solve is the expensive part of the whole decompile - on a full campaign map it
        // is minutes of arithmetic over ~90M unknowns - so it says what it is doing.
        log?.Invoke($"height reconstruction: solving {n:N0} texels, up to {maxIterations} conjugate gradient iterations");

        int iter;
        for (iter = 0; iter < maxIterations; iter++)
        {
            ApplyA(p, ap, scratch, anchorWeight, wd, ws, width, height);
            double pAp = Dot(p, ap);
            if (Math.Abs(pAp) < 1e-300)
                break;
            double alpha = rzOld / pAp;

            Axpy(x, p, alpha);
            Axpy(r, ap, -alpha);

            progress?.Invoke(Math.Min(1.0, (iter + 1) / (double)maxIterations));

            double residual = Math.Sqrt(Dot(r, r)) / bNorm;
            if ((iter + 1) % LogEveryIterations == 0)
                log?.Invoke($"height reconstruction: iteration {iter + 1}/{maxIterations}, residual {residual:0.0##e+0}");
            if (residual < RelativeTolerance)
            {
                iter++;
                break;
            }

            Parallel.For(0, n, i => z[i] = r[i] * precond[i]);
            double rzNew = Dot(r, z);
            double beta = rzNew / rzOld;
            Parallel.For(0, n, i => p[i] = (float)(z[i] + beta * p[i]));
            rzOld = rzNew;
        }

        log?.Invoke($"height reconstruction: conjugate gradient stopped after {iter} iterations");
        progress?.Invoke(1.0);
    }

    private static void Axpy(float[] y, float[] x, double alpha)
    {
        Parallel.For(0, y.Length, i => y[i] = (float)(y[i] + alpha * x[i]));
    }

    private static double Dot(float[] a, float[] b)
    {
        int threads = Environment.ProcessorCount;
        var partial = new double[threads];
        Parallel.For(0, threads, t =>
        {
            long start = (long)a.Length * t / threads;
            long end = (long)a.Length * (t + 1) / threads;
            double sum = 0;
            for (long i = start; i < end; i++)
                sum += (double)a[i] * b[i];
            partial[t] = sum;
        });
        double total = 0;
        foreach (double v in partial)
            total += v;
        return total;
    }

    // ------------------------------------------------------------------ safety clamp

    /// <summary>
    /// Limits every texel to within <paramref name="clampSteps"/> multiples of its BC6H
    /// block's level spacing. Inside one 4x4 block the decoded values sit on a lattice
    /// between the block's two fitted endpoints, so the smallest positive gap among the
    /// block's values recovers the spacing straight from the decoded data. Blocks whose
    /// texels all decoded to a single value carry no spacing information and inherit the
    /// median of their populated neighbours.
    /// </summary>
    private static void ApplySafetyClamp(float[] result, float[] hq, int width, int height,
        double clampSteps, Action<string>? log)
    {
        if (clampSteps <= 0 || double.IsPositiveInfinity(clampSteps))
            return;

        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var blockStep = new float[bw * bh];

        Parallel.For(0, bh, by =>
        {
            Span<float> vals = stackalloc float[16];
            for (int bx = 0; bx < bw; bx++)
            {
                int count = 0;
                for (int y = by * 4; y < Math.Min(by * 4 + 4, height); y++)
                    for (int x = bx * 4; x < Math.Min(bx * 4 + 4, width); x++)
                        vals[count++] = hq[(long)y * width + x];

                var used = vals[..count];
                used.Sort();
                float step = 0;
                for (int i = 1; i < count; i++)
                {
                    float gap = used[i] - used[i - 1];
                    if (gap > 0 && (step == 0 || gap < step)) step = gap;
                }
                blockStep[by * bw + bx] = step;
            }
        });

        var known = new List<float>(blockStep.Length);
        foreach (float s in blockStep)
            if (s > 0) known.Add(s);
        if (known.Count == 0)
            return;
        known.Sort();
        float globalMedian = known[known.Count / 2];

        long clamped = 0;
        object gate = new();
        Parallel.For(0, bh, () => 0L, (by, _, local) =>
        {
            Span<float> neighbours = stackalloc float[8];
            for (int bx = 0; bx < bw; bx++)
            {
                float step = blockStep[by * bw + bx];
                if (step == 0)
                {
                    int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = bx + dx, ny = by + dy;
                            if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                            float s = blockStep[ny * bw + nx];
                            if (s > 0) neighbours[k++] = s;
                        }
                    if (k > 0)
                    {
                        var used = neighbours[..k];
                        used.Sort();
                        step = used[k / 2];
                    }
                    else
                    {
                        step = globalMedian;
                    }
                }

                float limit = (float)(step * clampSteps);
                for (int y = by * 4; y < Math.Min(by * 4 + 4, height); y++)
                    for (int x = bx * 4; x < Math.Min(bx * 4 + 4, width); x++)
                    {
                        long i = (long)y * width + x;
                        float lo = hq[i] - limit, hi = hq[i] + limit;
                        if (result[i] < lo) { result[i] = lo; local++; }
                        else if (result[i] > hi) { result[i] = hi; local++; }
                    }
            }
            return local;
        }, local => { lock (gate) clamped += local; });

        if (clamped > 0)
        {
            // A percent or two is normal: BC6H's endpoint fit is least-squares over the whole
            // block, so its true error routinely runs to several times the level spacing and
            // the solve legitimately moves those texels a long way. Only a large fraction
            // suggests the two textures do not belong together.
            double pct = 100.0 * clamped / ((long)width * height);
            log?.Invoke($"height reconstruction: {clamped:N0} texels ({pct:0.##}%) hit the +/-{clampSteps:0.#}-step safety clamp" +
                        (pct > 5.0 ? " - lf_normal.dds may not match this height map" : ""));
        }
    }
}
