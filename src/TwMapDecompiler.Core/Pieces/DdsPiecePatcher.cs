namespace TwMapDecompiler.Core.Pieces;

/// <summary>
/// Pastes pieces into a map texture. A piece's DDS is a byte-exact crop of the map texture's top
/// mip - checked on all 202 wh3_main_combi_map_1 pieces for every texture they carry - taken at
/// the rectangle texture_info gives, in the map texture's own row order (so a bottom-up height
/// map needs no flipping here). Pasting the top mip's rows back is therefore exact: no decoding,
/// no re-encoding. The lower mips are left as they were; nothing here reads them.
///
/// Overlapping pieces agree where they overlap (checked on the devastate map), so the order they
/// are pasted in does not matter.
/// </summary>
public static class DdsPiecePatcher
{
    public sealed record Result(int Pieces, long BlocksWritten, long BlocksChanged, int BlocksWide, int BlocksHigh, bool[]? Changed);

    /// <param name="textureName">The texture's name in texture_info, e.g. full_height_map.dds.</param>
    /// <param name="trackChanges">Also return which blocks now differ from the source, row-major in file order.</param>
    public static Result Patch(string sourceDds, string targetDds, string textureName, IEnumerable<EventPiece> pieces,
        Action<string> log, bool trackChanges = false)
    {
        byte[] data = File.ReadAllBytes(sourceDds);
        int height = BitConverter.ToInt32(data, 12);
        int width = BitConverter.ToInt32(data, 16);
        int dataStart = BitConverter.ToUInt32(data, 84) == 0x30315844 ? 148 : 128; // "DX10" header extension

        byte[]? original = trackChanges ? (byte[])data.Clone() : null;
        int blockSize = 0, blockBytes = 0, blocksWide = 0, blocksHigh = 0;
        int count = 0, clipped = 0;
        long written = 0;
        var changedSet = new List<(int Bx, int By, int W, int H)>();

        foreach (var piece in pieces)
        {
            if (!piece.Textures.TryGetValue(textureName, out var texture) || !piece.Has(textureName))
                continue;
            var mip = texture.Mips[0];
            if (blockBytes == 0)
            {
                blockSize = mip.BlockSize;
                blockBytes = mip.BlockBytes;
                blocksWide = (width + blockSize - 1) / blockSize;
                blocksHigh = (height + blockSize - 1) / blockSize;
            }
            else if (mip.BlockBytes != blockBytes || mip.BlockSize != blockSize)
            {
                throw new InvalidDataException($"{piece.Folder}: {textureName} has {mip.BlockBytes}-byte blocks of {mip.BlockSize}px, " +
                                               $"other pieces have {blockBytes}-byte blocks of {blockSize}px.");
            }

            byte[] pieceData = File.ReadAllBytes(piece.File(textureName));
            int bx = mip.SrcX / blockSize, by = mip.SrcY / blockSize;
            // Block-aligned rectangles can run past the texture's right and bottom edges.
            int rows = Math.Min(mip.BlocksH, blocksHigh - by);
            int cols = Math.Min(mip.BlocksW, blocksWide - bx);
            if (rows != mip.BlocksH || cols != mip.BlocksW)
                clipped++;
            if (rows <= 0 || cols <= 0)
                continue;

            for (int r = 0; r < rows; r++)
            {
                long target = dataStart + ((long)(by + r) * blocksWide + bx) * blockBytes;
                long source = mip.Offset + (long)r * mip.BlocksW * blockBytes;
                Array.Copy(pieceData, source, data, target, (long)cols * blockBytes);
            }
            written += (long)rows * cols;
            count++;
            if (trackChanges)
                changedSet.Add((bx, by, cols, rows));
        }

        if (count == 0)
        {
            log($"    {textureName}: no piece carries it, so it is unchanged");
            File.Copy(sourceDds, targetDds, overwrite: true);
            return new Result(0, 0, 0, blocksWide, blocksHigh, trackChanges ? new bool[0] : null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDds)!);
        File.WriteAllBytes(targetDds, data);

        // Changed blocks: those the pieces wrote whose bytes now differ from the source.
        bool[]? changed = null;
        long changedCount = 0;
        if (trackChanges)
        {
            changed = new bool[(long)blocksWide * blocksHigh];
            foreach (var (bx, by, w, h) in changedSet)
                for (int y = by; y < by + h; y++)
                    for (int x = bx; x < bx + w; x++)
                    {
                        long i = (long)y * blocksWide + x;
                        if (changed[i])
                            continue;
                        long o = dataStart + i * blockBytes;
                        if (!data.AsSpan((int)o, blockBytes).SequenceEqual(original.AsSpan((int)o, blockBytes)))
                        {
                            changed[i] = true;
                            changedCount++;
                        }
                    }
        }

        log($"    {textureName}: {count} pieces pasted ({written:N0} blocks" +
            (clipped > 0 ? $", {clipped} clipped at the texture edge" : "") +
            (trackChanges ? $", {changedCount:N0} differ from the source" : "") + ")");
        return new Result(count, written, changedCount, blocksWide, blocksHigh, changed);
    }
}
