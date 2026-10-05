using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace TwMapDecompiler.Core.Formats.Dds;

public enum DdsFormat
{
    Bc1,
    Bc3,
    Bc4,
    Bc6HSigned,
    Bc6HUnsigned,
    R8,
    R32Float,
    L8,
}

/// <summary>
/// Minimal DDS reader for the formats CA uses in campaign terrain folders.
/// Only the top mip level is decoded.
/// </summary>
public sealed class DdsTexture
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public DdsFormat Format { get; private set; }

    private byte[] _data = [];

    /// <summary>Width and height from the header alone.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x20534444) // "DDS "
            throw new InvalidDataException($"Not a DDS file: {path}");
        stream.Seek(8, SeekOrigin.Current); // dwSize, dwFlags
        int height = (int)reader.ReadUInt32();
        int width = (int)reader.ReadUInt32();
        return (width, height);
    }

    public static DdsTexture Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (reader.ReadUInt32() != 0x20534444) // "DDS "
            throw new InvalidDataException($"Not a DDS file: {path}");

        reader.ReadUInt32(); // dwSize
        reader.ReadUInt32(); // dwFlags
        int height = (int)reader.ReadUInt32();
        int width = (int)reader.ReadUInt32();
        stream.Seek(4 + 4 + 4 + 44, SeekOrigin.Current); // pitch, depth, mipCount, reserved

        reader.ReadUInt32(); // pf.dwSize
        uint pfFlags = reader.ReadUInt32();
        uint fourCc = reader.ReadUInt32();
        uint rgbBitCount = reader.ReadUInt32();
        stream.Seek(16, SeekOrigin.Current); // RGBA bit masks
        stream.Seek(20, SeekOrigin.Current); // caps1-4, reserved2

        DdsFormat format;
        if (fourCc == 0x30315844) // "DX10"
        {
            uint dxgiFormat = reader.ReadUInt32();
            stream.Seek(16, SeekOrigin.Current); // resourceDimension, miscFlag, arraySize, miscFlags2
            format = dxgiFormat switch
            {
                41 => DdsFormat.R32Float,   // DXGI_FORMAT_R32_FLOAT
                61 => DdsFormat.R8,         // DXGI_FORMAT_R8_UNORM
                62 => DdsFormat.R8,         // DXGI_FORMAT_R8_UINT
                71 or 72 => DdsFormat.Bc1,  // BC1_UNORM(_SRGB)
                77 or 78 => DdsFormat.Bc3,  // BC3_UNORM(_SRGB)
                80 => DdsFormat.Bc4,        // BC4_UNORM
                95 => DdsFormat.Bc6HUnsigned,
                96 => DdsFormat.Bc6HSigned,
                _ => throw new NotSupportedException($"Unsupported DXGI format {dxgiFormat} in {path}"),
            };
        }
        else if (fourCc == 0x31545844) format = DdsFormat.Bc1; // "DXT1"
        else if (fourCc == 0x35545844) format = DdsFormat.Bc3; // "DXT5"
        else if ((pfFlags & 0x4) == 0 && rgbBitCount == 8) format = DdsFormat.L8;
        else throw new NotSupportedException($"Unsupported DDS pixel format (fourCC=0x{fourCc:X8}, bits={rgbBitCount}) in {path}");

        var tex = new DdsTexture { Width = width, Height = height, Format = format };
        tex._data = reader.ReadBytes(tex.Mip0ByteCount());
        if (tex._data.Length < tex.Mip0ByteCount())
            throw new InvalidDataException($"DDS file truncated: {path}");
        return tex;
    }

    private int Mip0ByteCount() => Format switch
    {
        DdsFormat.Bc1 or DdsFormat.Bc4 => BlocksX * BlocksY * 8,
        DdsFormat.Bc3 or DdsFormat.Bc6HSigned or DdsFormat.Bc6HUnsigned => BlocksX * BlocksY * 16,
        DdsFormat.R32Float => Width * Height * 4,
        _ => Width * Height,
    };

    private int BlocksX => (Width + 3) / 4;
    private int BlocksY => (Height + 3) / 4;

    public bool IsBlockCompressed => Format is DdsFormat.Bc1 or DdsFormat.Bc3 or DdsFormat.Bc4 or DdsFormat.Bc6HSigned or DdsFormat.Bc6HUnsigned;

    /// <summary>Red channel as float32 (for BC6H height maps and R32F shroud heights).</summary>
    public float[] DecodeRedFloat(Action<double>? progress = null) => DecodeChannelFloat(0, progress);

    /// <summary>
    /// One channel of a BC6H (or R32F) texture as float32. CA bakes two surfaces into
    /// full_height_map.dds: the land height in red and the sea bed in green.
    /// </summary>
    /// <param name="channel">0 = red, 1 = green, 2 = blue.</param>
    public float[] DecodeChannelFloat(int channel, Action<double>? progress = null)
        => DecodeChannelsFloat([channel], progress)[0];

    /// <summary>
    /// Several channels in one decode, which for a full campaign map saves repeating ~10
    /// seconds of BC6H work per channel. One array per requested channel, in the order given.
    /// </summary>
    public float[][] DecodeChannelsFloat(int[] channels, Action<double>? progress = null)
    {
        var outputs = channels.Select(_ => new float[Width * Height]).ToArray();

        if (Format == DdsFormat.R32Float)
        {
            if (channels.Any(c => c != 0))
                throw new NotSupportedException("R32F textures only have a red channel.");
            // shroud_heights.dds is baked bottom-to-top like the BC6H height map (unflipped it
            // correlates -0.06 with the land height, flipped 0.87), so it gets the same flip.
            foreach (var output in outputs)
            {
                Buffer.BlockCopy(_data, 0, output, 0, Width * Height * 4);
                FlipRowsVertically(output, Width, Height);
            }
            progress?.Invoke(1.0);
            return outputs;
        }

        if (Format is not (DdsFormat.Bc6HSigned or DdsFormat.Bc6HUnsigned))
            throw new NotSupportedException($"DecodeChannelFloat not supported for {Format}");

        var compressionFormat = Format == DdsFormat.Bc6HSigned ? CompressionFormat.Bc6S : CompressionFormat.Bc6U;
        DecodeBlockStrips(16, stripBlockRows: 64, (strip, pixelRows, input) =>
        {
            var decoder = new BcDecoder();
            ColorRgbFloat[] pixels = decoder.DecodeRawHdr(input, Width, pixelRows, compressionFormat);
            int yStart = strip * 64 * 4;
            // Decoded strip is BlocksX*4 wide even if Width isn't a multiple of 4; Width from
            // the header is used directly since decoder output is Width*pixelRows.
            for (int i = 0; i < Width * pixelRows; i++)
            {
                int y = yStart + i / Width;
                if (y >= Height)
                    continue;
                long o = (long)y * Width + i % Width;
                for (int c = 0; c < channels.Length; c++)
                    outputs[c][o] = channels[c] switch
                    {
                        0 => pixels[i].r,
                        1 => pixels[i].g,
                        _ => pixels[i].b,
                    };
            }
        }, progress);

        // CA's BC6H height bake (and the R32F shroud above) stores rows bottom-to-top (confirmed against lf_normal.dds
        // for the same map, which decodes top-down); flip so row 0 is the top row like every
        // other decoded layer.
        foreach (var output in outputs)
            FlipRowsVertically(output, Width, Height);
        return outputs;
    }

    private static void FlipRowsVertically(float[] data, int width, int height)
    {
        var row = new float[width];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * width;
            int bottom = (height - 1 - y) * width;
            Array.Copy(data, top, row, 0, width);
            Array.Copy(data, bottom, data, top, width);
            Array.Copy(row, 0, data, bottom, width);
        }
    }

    /// <summary>Single 8-bit channel (R8/L8 raw data, or the red channel of BC4).</summary>
    public byte[] DecodeGray8(Action<double>? progress = null)
    {
        if (Format is DdsFormat.R8 or DdsFormat.L8)
        {
            progress?.Invoke(1.0);
            return _data;
        }

        if (Format != DdsFormat.Bc4)
            throw new NotSupportedException($"DecodeGray8 not supported for {Format}");

        var output = new byte[Width * Height];
        DecodeBlockStrips(8, stripBlockRows: 64, (strip, pixelRows, input) =>
        {
            var decoder = new BcDecoder();
            ColorRgba32[] pixels = decoder.DecodeRaw(input, Width, pixelRows, CompressionFormat.Bc4);
            int yStart = strip * 64 * 4;
            for (int i = 0; i < Width * pixelRows; i++)
            {
                int y = yStart + i / Width;
                if (y < Height)
                    output[y * Width + i % Width] = pixels[i].r;
            }
        }, progress);
        return output;
    }

    /// <summary>Full RGBA8 decode (BC1/BC3 colour maps).</summary>
    public byte[] DecodeRgba8(Action<double>? progress = null)
    {
        if (!IsBlockCompressed)
            throw new NotSupportedException($"DecodeRgba8 not supported for {Format}");

        int blockBytes = Format == DdsFormat.Bc1 ? 8 : 16;
        var compressionFormat = Format switch
        {
            DdsFormat.Bc1 => CompressionFormat.Bc1,
            DdsFormat.Bc3 => CompressionFormat.Bc3,
            DdsFormat.Bc4 => CompressionFormat.Bc4,
            _ => throw new NotSupportedException($"DecodeRgba8 not supported for {Format}"),
        };

        var output = new byte[(long)Width * Height * 4];
        DecodeBlockStrips(blockBytes, stripBlockRows: 64, (strip, pixelRows, input) =>
        {
            var decoder = new BcDecoder();
            ColorRgba32[] pixels = decoder.DecodeRaw(input, Width, pixelRows, compressionFormat);
            long yStart = strip * 64 * 4;
            for (int i = 0; i < Width * pixelRows; i++)
            {
                long y = yStart + i / Width;
                if (y >= Height)
                    break;
                long o = (y * Width + i % Width) * 4;
                output[o] = pixels[i].r;
                output[o + 1] = pixels[i].g;
                output[o + 2] = pixels[i].b;
                output[o + 3] = pixels[i].a;
            }
        }, progress);
        return output;
    }

    /// <summary>Decodes block-compressed data in strips of block rows to bound peak memory.</summary>
    private void DecodeBlockStrips(int blockBytes, int stripBlockRows, Action<int, int, byte[]> decodeStrip, Action<double>? progress)
    {
        int stripCount = (BlocksY + stripBlockRows - 1) / stripBlockRows;
        for (int strip = 0; strip < stripCount; strip++)
        {
            int blockRows = Math.Min(stripBlockRows, BlocksY - strip * stripBlockRows);
            int byteStart = strip * stripBlockRows * BlocksX * blockBytes;
            int byteCount = blockRows * BlocksX * blockBytes;
            var input = new byte[byteCount];
            Array.Copy(_data, byteStart, input, 0, byteCount);
            decodeStrip(strip, blockRows * 4, input);
            progress?.Invoke((strip + 1) / (double)stripCount);
        }
    }
}
