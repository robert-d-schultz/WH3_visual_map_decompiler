using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;

namespace SyntheticTerrain;

/// <summary>
/// Round-trips synthetic data through the *real* BC6H/BC3 codecs (not an approximation
/// of their quantization) so tests see authentic block-compression artifacts.
/// </summary>
public static class RoundTrip
{
    /// <summary>
    /// Uniform dither, in 8-bit levels, applied to the packed normal before BC3 compression.
    ///
    /// A real lf_normal.dds is not band-limited - it is *noisy*. Box-smoothing the shipped
    /// normal at radius 1 changes it by 19.6% of its RMS but radius 4 only reaches 25.3%,
    /// which is the signature of white noise on top of a smooth field rather than a blurred
    /// field (a blurred one would barely change at radius 1). Its curl, 0.73, also exceeds
    /// its own gradient variation, 0.42, so at texel scale it is not a consistent surface at
    /// all. BCnEncoder's BC3 is cleaner than whatever CA encoded with, so this dither makes
    /// up the difference: at 3 the synthetic normal degrades to 16.0/22.1/25.0/27.0 against
    /// the real 19.6/22.5/24.4/25.3.
    /// </summary>
    public const double NormalDither = 3.0;

    /// <summary>
    /// Packs an exact gradient field into DXT5nm-layout RGBA8: R=255, B=0, <b>X in
    /// alpha, Y in green</b>. That channel assignment is not arbitrary - it is the
    /// layout CA actually ships (verified against lf_normal.dds on
    /// wh3_main_combi_map_1, where reading X from green instead drops the correlation
    /// with the height map from 0.65 to 0.001) and the reason for it is that BC3 stores
    /// alpha in its own higher-precision block, so the convention puts a channel there
    /// and reconstructs Z analytically.
    /// </summary>
    public static byte[] PackNormalDxt5nm(double[] gx, double[] gy, int width, int height, double texelSize,
        double dither = NormalDither, int seed = 99)
    {
        var rng = new Random(seed);
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            double slopeX = gx[i] / texelSize;
            double slopeY = gy[i] / texelSize;
            double nz = 1.0 / Math.Sqrt(1.0 + slopeX * slopeX + slopeY * slopeY);

            int o = i * 4;
            rgba[o + 0] = 255;
            rgba[o + 1] = Encode(-slopeY * nz, rng, dither); // Y -> G
            rgba[o + 2] = 0;
            rgba[o + 3] = Encode(-slopeX * nz, rng, dither); // X -> A
        }
        return rgba;

        static byte Encode(double v, Random rng, double dither) =>
            (byte)Math.Clamp(Math.Round((v + 1.0) * 127.5 + dither * (rng.NextDouble() * 2 - 1)), 0, 255);
    }

    /// <summary>Encodes RGBA8 to BC3 and decodes it back, matching lf_normal.dds's real compression.</summary>
    public static byte[] Bc3RoundTrip(byte[] rgba, int width, int height)
    {
        var px = new ColorRgba32[width * height];
        for (int i = 0; i < px.Length; i++)
            px[i] = new ColorRgba32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
        var mem = new Memory<ColorRgba32>(px).AsMemory2D(height, width);

        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = CompressionFormat.Bc3;
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        encoder.OutputOptions.GenerateMipMaps = false;
        byte[] encoded = encoder.EncodeToRawBytes(mem)[0];

        var decoder = new BcDecoder();
        ColorRgba32[] decoded = decoder.DecodeRaw(encoded, width, height, CompressionFormat.Bc3);

        var outRgba = new byte[width * height * 4];
        for (int i = 0; i < decoded.Length; i++)
        {
            outRgba[i * 4 + 0] = decoded[i].r;
            outRgba[i * 4 + 1] = decoded[i].g;
            outRgba[i * 4 + 2] = decoded[i].b;
            outRgba[i * 4 + 3] = decoded[i].a;
        }
        return outRgba;
    }

    /// <summary>Encodes float heights to BC6H signed and decodes them back, matching full_height_map.dds.</summary>
    public static float[] Bc6RoundTrip(double[] heights, int width, int height)
    {
        var rgbFloat = new ColorRgbFloat[width * height];
        for (int i = 0; i < rgbFloat.Length; i++)
            rgbFloat[i] = new ColorRgbFloat((float)heights[i], (float)heights[i], (float)heights[i]);
        var mem = new Memory<ColorRgbFloat>(rgbFloat).AsMemory2D(height, width);

        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = CompressionFormat.Bc6S;
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        encoder.OutputOptions.GenerateMipMaps = false;
        byte[] encoded = encoder.EncodeToRawBytesHdr(mem)[0];

        var decoder = new BcDecoder();
        ColorRgbFloat[] decoded = decoder.DecodeRawHdr(encoded, width, height, CompressionFormat.Bc6S);

        var outArr = new float[width * height];
        for (int i = 0; i < outArr.Length; i++)
            outArr[i] = decoded[i].r;
        return outArr;
    }
}
