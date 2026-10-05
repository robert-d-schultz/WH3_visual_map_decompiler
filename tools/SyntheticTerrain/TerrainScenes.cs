namespace SyntheticTerrain;

/// <summary>
/// A terrain patch with an exactly-known height field and its exact analytic
/// per-texel gradient (dh/dx, dh/dy in height-units-per-texel - i.e. already
/// "gx"/"gy" the way HeightReconstructor defines them: physical_slope * TexelSize).
/// </summary>
public sealed record Scene(string Name, Func<int, int, double> HeightAt, Func<int, int, (double Gx, double Gy)> GradientAt)
{
    public (double[] Heights, double[] Gx, double[] Gy) Evaluate(int width, int height)
    {
        var heights = new double[width * height];
        var gx = new double[width * height];
        var gy = new double[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                heights[i] = HeightAt(x, y);
                (gx[i], gy[i]) = GradientAt(x, y);
            }
        }
        return (heights, gx, gy);
    }
}

/// <summary>
/// Synthetic scenes covering the terrain shapes that stress the reconstruction differently.
/// The two fBm scenes are the realistic ones and carry the most weight when tuning; the
/// analytic shapes isolate specific failure modes: a pure plane has zero curvature so any
/// residual roughness is pure artifact, a ridge has a gradient-direction discontinuity that
/// smoothing terms tend to round off, and the near-flat patch is where the normal map's byte
/// quantization swamps the real signal.
///
/// <see cref="TexelSize"/> and the <c>terrain</c> scene's parameters are calibrated against
/// wh3_main_combi_map_1. The texel size comes from the wavelengths at which the height map
/// and lf_normal.dds actually agree (coherence 0.63-0.97 at 64-256 texels, all implying
/// 0.022-0.027). The terrain parameters were fitted so that after BC3 compression the
/// synthetic normal degrades the way the shipped one does - box-smoothing it at radius 1..4
/// gives 16.0/22.1/25.0/27.0% against the real 19.6/22.5/24.4/25.3%.
/// </summary>
public static class TerrainScenes
{
    public const int Width = 256;
    public const int Height = 256;
    public const double TexelSize = 0.024;

    public static readonly Scene[] All =
    [
        Fbm("terrain", amplitude: 0.8, exponent: 1.5, finestWavelength: 3.0, seed: 12345),
        Fbm("rolling", amplitude: 0.5, exponent: 2.0, finestWavelength: 6.0, seed: 777),

        new("ramp",
            (x, y) => 0.5 * x * TexelSize,
            (x, y) => (0.5 * TexelSize, 0.0)),

        new("hill",
            (x, y) =>
            {
                double a = 5, sigma = 50, cx = Width / 2.0, cy = Height / 2.0;
                double dx = x - cx, dy = y - cy;
                return a * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            },
            (x, y) =>
            {
                double a = 5, sigma = 50, cx = Width / 2.0, cy = Height / 2.0;
                double dx = x - cx, dy = y - cy;
                double h = a * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                return (-h * dx / (sigma * sigma), -h * dy / (sigma * sigma));
            }),

        new("ridge",
            (x, y) =>
            {
                double a = 6, cx = Width / 2.0, half = Width / 2.0;
                return a * (1.0 - Math.Abs(x - cx) / half);
            },
            (x, y) =>
            {
                double a = 6, cx = Width / 2.0, half = Width / 2.0;
                return ((x < cx ? 1.0 : -1.0) * a / half, 0.0);
            }),

        new("flat",
            (x, y) =>
            {
                double a = 0.3, l = 150;
                return 10.0 + a * Math.Sin(2 * Math.PI * x / l) * Math.Cos(2 * Math.PI * y / l);
            },
            (x, y) =>
            {
                double a = 0.3, l = 150;
                double gx = a * Math.Cos(2 * Math.PI * x / l) * Math.Cos(2 * Math.PI * y / l) * (2 * Math.PI / l);
                double gy = -a * Math.Sin(2 * Math.PI * x / l) * Math.Sin(2 * Math.PI * y / l) * (2 * Math.PI / l);
                return (gx, gy);
            }),
    ];

    /// <summary>
    /// Band-limited fBm built from a fixed set of randomly-oriented sinusoids, so the surface
    /// has energy at every scale from 256 texels down to <paramref name="finestWavelength"/>
    /// while its gradient stays exactly analytic. <paramref name="exponent"/> sets the
    /// spectral slope: higher is smoother, with less energy at fine scales.
    /// </summary>
    private static Scene Fbm(string name, double amplitude, double exponent, double finestWavelength, int seed)
    {
        var rng = new Random(seed);
        const int octaves = 30;
        var fx = new double[octaves];
        var fy = new double[octaves];
        var amp = new double[octaves];
        var phase = new double[octaves];
        for (int k = 0; k < octaves; k++)
        {
            double wavelength = 256.0 / Math.Pow(256.0 / finestWavelength, k / (double)(octaves - 1));
            double theta = rng.NextDouble() * 2 * Math.PI;
            fx[k] = Math.Cos(theta) * 2 * Math.PI / wavelength;
            fy[k] = Math.Sin(theta) * 2 * Math.PI / wavelength;
            amp[k] = amplitude * Math.Pow(wavelength / 256.0, exponent);
            phase[k] = rng.NextDouble() * 2 * Math.PI;
        }

        return new Scene(name,
            (x, y) =>
            {
                double h = 0;
                for (int k = 0; k < octaves; k++)
                    h += amp[k] * Math.Sin(fx[k] * x + fy[k] * y + phase[k]);
                return h;
            },
            (x, y) =>
            {
                double gx = 0, gy = 0;
                for (int k = 0; k < octaves; k++)
                {
                    double c = amp[k] * Math.Cos(fx[k] * x + fy[k] * y + phase[k]);
                    gx += c * fx[k];
                    gy += c * fy[k];
                }
                return (gx, gy);
            });
    }
}
