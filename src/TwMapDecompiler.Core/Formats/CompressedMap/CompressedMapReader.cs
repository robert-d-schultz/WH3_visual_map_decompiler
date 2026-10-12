using TwMapDecompiler.Core.IO;

namespace TwMapDecompiler.Core.Formats.CompressedMap;

/// <summary>
/// Reads CA's .compressed_map block-compressed images. Two encodings are used
/// by campaign terrain: TABLE_INDEXED (16-bit values; logic maps, sea height)
/// and RLE_CARD32 (RGBA; battle catchment maps).
/// </summary>
public static class CompressedMapReader
{
    /// <param name="Values">The header's six floats; a logic map keeps its height range in [1] and [4].</param>
    public sealed record Map16(int Width, int Height, ushort[] Pixels, float[] Values);
    public sealed record MapRgba(int Width, int Height, byte[] Pixels);

    private sealed record Header(
        int MapWidth, int MapHeight, int BlockWidth, int BlockHeight, float[] Values,
        string Encoding, uint[] Offsets, long BlockDataStart, uint BlockTableLength);

    private static Header ReadHeader(BinaryReader reader)
    {
        reader.Skip(8); // magic
        reader.ReadUInt16(); // version
        int mapWidth = (int)reader.ReadUInt32();
        int mapHeight = (int)reader.ReadUInt32();
        int blockWidth = (int)reader.ReadUInt32();
        int blockHeight = (int)reader.ReadUInt32();
        var values = new float[6];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadSingle();
        string encoding = reader.ReadU16String();

        uint offsetCount = reader.ReadUInt32();
        var offsets = new uint[offsetCount];
        for (uint i = 0; i < offsetCount; i++)
            offsets[i] = reader.ReadUInt32();

        uint lengthCount = reader.ReadUInt32();
        reader.Skip((int)(lengthCount * 2));

        uint blockTableLength = reader.ReadUInt32();
        return new Header(mapWidth, mapHeight, blockWidth, blockHeight, values, encoding, offsets, reader.BaseStream.Position, blockTableLength);
    }

    /// <summary>16-bit TABLE_INDEXED maps (e.g. full_logic_map.compressed_map).</summary>
    public static Map16 ReadTableIndexed(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var reader = new BinaryReader(stream);
        var header = ReadHeader(reader);
        if (header.Encoding != "TABLE_INDEXED")
            throw new InvalidDataException($"Expected TABLE_INDEXED encoding, found '{header.Encoding}' in {path}");

        int pixelsPerBlock = header.BlockWidth * header.BlockHeight;
        var blockPixels = new ushort[pixelsPerBlock];
        var image = new ushort[(long)header.MapWidth * header.MapHeight];
        int blocksPerRow = (header.MapWidth + header.BlockWidth - 1) / header.BlockWidth;

        for (int blockIndex = 0; blockIndex < header.Offsets.Length; blockIndex++)
        {
            stream.Seek(header.BlockDataStart + header.Offsets[blockIndex], SeekOrigin.Begin);
            DecodeBlock(reader, blockPixels, pixelsPerBlock, path);
            PlaceBlock(blockPixels, image, blockIndex, blocksPerRow, header);
        }

        return new Map16(header.MapWidth, header.MapHeight, image, header.Values);
    }

    private static void DecodeBlock(BinaryReader reader, ushort[] block, int pixelsPerBlock, string path)
    {
        byte control = reader.ReadByte();

        if (control == 143)
        {
            // Uncompressed 16-bit block.
            for (int i = 0; i < pixelsPerBlock; i++)
                block[i] = reader.ReadUInt16();
        }
        else if (control >= 128)
        {
            // Delta compression: base value + N-bit unsigned deltas.
            int bitsPerPixel = control - 127;
            ushort baseValue = reader.ReadUInt16();
            var bits = new BitReader(reader.ReadBytes((pixelsPerBlock * bitsPerPixel + 7) / 8));
            for (int i = 0; i < pixelsPerBlock; i++)
                block[i] = (ushort)(baseValue + bits.Read(bitsPerPixel));
        }
        else
        {
            // Table compression: value table + N-bit indices.
            int valueCount = control + 1;
            var values = new ushort[valueCount];
            for (int i = 0; i < valueCount; i++)
                values[i] = reader.ReadUInt16();

            int bitsPerIndex = valueCount <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(valueCount));
            if (bitsPerIndex == 0)
            {
                Array.Fill(block, values[0]);
                return;
            }

            var bits = new BitReader(reader.ReadBytes((pixelsPerBlock * bitsPerIndex + 7) / 8));
            for (int i = 0; i < pixelsPerBlock; i++)
            {
                uint index = bits.Read(bitsPerIndex);
                if (index >= valueCount)
                    throw new InvalidDataException($"Table index {index} out of range ({valueCount} values) in {path}");
                block[i] = values[index];
            }
        }
    }

    /// <summary>RGBA RLE_CARD32 maps (e.g. blm_catchment_override.compressed_map).</summary>
    public static MapRgba ReadRleCard32(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var reader = new BinaryReader(stream);
        var header = ReadHeader(reader);
        if (header.Encoding != "RLE_CARD32")
            throw new InvalidDataException($"Expected RLE_CARD32 encoding, found '{header.Encoding}' in {path}");

        int pixelsPerBlock = header.BlockWidth * header.BlockHeight;
        var blockPixels = new uint[pixelsPerBlock];
        var image = new byte[(long)header.MapWidth * header.MapHeight * 4];
        int blocksPerRow = (header.MapWidth + header.BlockWidth - 1) / header.BlockWidth;

        int runCount = (int)(header.BlockTableLength / 6);
        int filled = 0;
        int blockIndex = 0;
        for (int run = 0; run < runCount; run++)
        {
            int runLength = reader.ReadUInt16();
            byte b = reader.ReadByte();
            byte g = reader.ReadByte();
            byte r = reader.ReadByte();
            byte a = reader.ReadByte();
            uint rgba = (uint)(r | g << 8 | b << 16 | a << 24);

            for (int i = 0; i < runLength; i++)
            {
                if (filled >= pixelsPerBlock)
                    throw new InvalidDataException($"RLE run crosses block boundary in {path}");
                blockPixels[filled++] = rgba;

                if (filled == pixelsPerBlock)
                {
                    PlaceBlockRgba(blockPixels, image, blockIndex++, blocksPerRow, header);
                    filled = 0;
                }
            }
        }

        return new MapRgba(header.MapWidth, header.MapHeight, image);
    }

    private static void PlaceBlock(ushort[] block, ushort[] image, int blockIndex, int blocksPerRow, Header header)
    {
        int blockX = blockIndex % blocksPerRow * header.BlockWidth;
        int blockY = blockIndex / blocksPerRow * header.BlockHeight;
        for (int y = 0; y < header.BlockHeight; y++)
        {
            int imageY = blockY + y;
            if (imageY >= header.MapHeight)
                break;
            for (int x = 0; x < header.BlockWidth; x++)
            {
                int imageX = blockX + x;
                if (imageX < header.MapWidth)
                    image[(long)imageY * header.MapWidth + imageX] = block[y * header.BlockWidth + x];
            }
        }
    }

    private static void PlaceBlockRgba(uint[] block, byte[] image, int blockIndex, int blocksPerRow, Header header)
    {
        int blockX = blockIndex % blocksPerRow * header.BlockWidth;
        int blockY = blockIndex / blocksPerRow * header.BlockHeight;
        for (int y = 0; y < header.BlockHeight; y++)
        {
            int imageY = blockY + y;
            if (imageY >= header.MapHeight)
                break;
            for (int x = 0; x < header.BlockWidth; x++)
            {
                int imageX = blockX + x;
                if (imageX >= header.MapWidth)
                    continue;
                uint rgba = block[y * header.BlockWidth + x];
                long o = ((long)imageY * header.MapWidth + imageX) * 4;
                image[o] = (byte)rgba;
                image[o + 1] = (byte)(rgba >> 8);
                image[o + 2] = (byte)(rgba >> 16);
                image[o + 3] = (byte)(rgba >> 24);
            }
        }
    }

    /// <summary>LSB-first bit reader matching the game's bit packing.</summary>
    private ref struct BitReader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _byteIndex;
        private ulong _buffer;
        private int _bitsInBuffer;

        public uint Read(int bitCount)
        {
            while (_bitsInBuffer < bitCount)
            {
                _buffer |= (ulong)(_byteIndex < _data.Length ? _data[_byteIndex++] : 0) << _bitsInBuffer;
                _bitsInBuffer += 8;
            }
            uint value = (uint)(_buffer & ((1UL << bitCount) - 1));
            _buffer >>= bitCount;
            _bitsInBuffer -= bitCount;
            return value;
        }
    }
}
