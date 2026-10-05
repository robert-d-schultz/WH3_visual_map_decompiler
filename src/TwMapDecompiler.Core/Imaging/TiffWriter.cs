using BitMiracle.LibTiff.Classic;

namespace TwMapDecompiler.Core.Imaging;

/// <summary>
/// Writes the TIFF flavours Terry expects (verified against The Old World's
/// cr_oldworld_map_1 project): LZW-compressed, top-left orientation, single strip layouts of
/// 8-bit gray / 8-bit palette / 32-bit float gray / 8-bit RGBA.
/// </summary>
public static class TiffWriter
{
    public static void WriteGray8(string path, int width, int height, byte[] data, Action<double>? progress = null)
    {
        using var tif = Open(path, width, height, bitsPerSample: 8, samplesPerPixel: 1, Photometric.MINISBLACK, SampleFormat.UINT);
        var row = new byte[width];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(data, (long)y * width, row, 0, width);
            WriteRow(tif, row, y, path);
            Report(progress, y, height);
        }
    }

    public static void WritePalette8(string path, int width, int height, byte[] data, byte[] paletteRgb768, Action<double>? progress = null)
    {
        using var tif = Open(path, width, height, bitsPerSample: 8, samplesPerPixel: 1, Photometric.PALETTE, SampleFormat.UINT);
        var r = new ushort[256];
        var g = new ushort[256];
        var b = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            r[i] = (ushort)(paletteRgb768[i * 3] * 257);
            g[i] = (ushort)(paletteRgb768[i * 3 + 1] * 257);
            b[i] = (ushort)(paletteRgb768[i * 3 + 2] * 257);
        }
        tif.SetField(TiffTag.COLORMAP, r, g, b);

        var row = new byte[width];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(data, (long)y * width, row, 0, width);
            WriteRow(tif, row, y, path);
            Report(progress, y, height);
        }
    }

    public static void WriteFloat32(string path, int width, int height, float[] data, Action<double>? progress = null)
    {
        using var tif = Open(path, width, height, bitsPerSample: 32, samplesPerPixel: 1, Photometric.MINISBLACK, SampleFormat.IEEEFP);
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(data, y * width * 4, row, 0, width * 4);
            WriteRow(tif, row, y, path);
            Report(progress, y, height);
        }
    }

    public static void WriteRgba8(string path, int width, int height, byte[] data, Action<double>? progress = null)
    {
        using var tif = Open(path, width, height, bitsPerSample: 8, samplesPerPixel: 4, Photometric.RGB, SampleFormat.UINT);
        tif.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(data, (long)y * width * 4, row, 0, width * 4);
            WriteRow(tif, row, y, path);
            Report(progress, y, height);
        }
    }

    private static Tiff Open(string path, int width, int height, int bitsPerSample, int samplesPerPixel, Photometric photometric, SampleFormat sampleFormat)
    {
        Tiff tif = Tiff.Open(path, "w") ?? throw new IOException($"Could not create TIFF file: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, samplesPerPixel);
        tif.SetField(TiffTag.PHOTOMETRIC, photometric);
        tif.SetField(TiffTag.SAMPLEFORMAT, sampleFormat);
        tif.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.ROWSPERSTRIP, tif.DefaultStripSize(0));
        return tif;
    }

    private static void WriteRow(Tiff tif, byte[] row, int y, string path)
    {
        if (!tif.WriteScanline(row, y))
            throw new IOException($"Failed writing scanline {y} of {path}");
    }

    private static void Report(Action<double>? progress, int y, int height)
    {
        if (progress != null && (y % 512 == 0 || y == height - 1))
            progress((y + 1) / (double)height);
    }
}
