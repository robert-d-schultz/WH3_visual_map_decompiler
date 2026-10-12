using TwMapDecompiler.Core.Bmd;
using TwMapDecompiler.Core.Pieces;
using TwMapDecompiler.Core.Tiles;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// Event-area pieces, on synthetic folders laid out like pieces/event_&lt;hex&gt;/ (docs/event-area-pieces.md).
/// The real formats were checked against every piece of wh3_main_combi_map_1 and its devastate map.
/// </summary>
public sealed class EventPieceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TwMapDecompilerTests", Guid.NewGuid().ToString("N"));

    public EventPieceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A piece with a 3x2 box at (1, 1) of a 6x4 event mask, carrying one 8-bit texture of the same size.</summary>
    private string WritePiece(string name, byte[] texture, string textureName = "corruption_mask.dds")
    {
        string folder = Path.Combine(_root, "map", "pieces", name);
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "texture_info"),
        [
            "mask 6 4 1 1 3 2",
            $"{textureName} 1 0",
            "1 1 3 2 1 3 2 128",
        ]);
        // Bits 0, 1 and 4 of the 6-pixel box: (0,0), (1,0) and (1,1).
        File.WriteAllBytes(Path.Combine(folder, "mask"), [0b0001_0011, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(folder, textureName), [.. L8Header(3, 2), .. texture]);
        return folder;
    }

    private static byte[] L8Header(int width, int height)
    {
        var header = new byte[128];
        BitConverter.GetBytes(0x20534444u).CopyTo(header, 0); // "DDS "
        BitConverter.GetBytes(height).CopyTo(header, 12);
        BitConverter.GetBytes(width).CopyTo(header, 16);
        BitConverter.GetBytes(8).CopyTo(header, 88); // 8 bits per pixel, no fourCC
        return header;
    }

    [Fact]
    public void Reads_texture_info_mask_and_colour()
    {
        var piece = EventPiece.Load(WritePiece("event_0006DC", new byte[6]));

        Assert.Equal("0006dc", piece.ColourHex);
        Assert.Equal((1, 1, 3, 2), (piece.X, piece.Y, piece.Width, piece.Height));
        Assert.Equal((6, 4), (piece.MaskWidth, piece.MaskHeight));
        var mip = Assert.Single(piece.Textures["corruption_mask.dds"].Mips);
        Assert.Equal(new PieceMip(1, 1, 3, 2, 1, 3, 2, 128), mip);
        Assert.Equal(1, mip.BlockSize);
        Assert.Equal([true, true, false, false, true, false], piece.ReadMask());
    }

    [Fact]
    public void Pastes_a_pieces_top_mip_into_the_map_texture()
    {
        var piece = EventPiece.Load(WritePiece("event_000001", [1, 2, 3, 4, 5, 6]));
        string map = Path.Combine(_root, "corruption_mask.dds");
        File.WriteAllBytes(map, [.. L8Header(6, 4), .. new byte[24]]);
        string patched = Path.Combine(_root, "patched.dds");

        var result = DdsPiecePatcher.Patch(map, patched, "corruption_mask.dds", [piece], _ => { }, trackChanges: true);

        byte[] pixels = File.ReadAllBytes(patched)[128..];
        Assert.Equal(new byte[]
        {
            0, 0, 0, 0, 0, 0,
            0, 1, 2, 3, 0, 0,
            0, 4, 5, 6, 0, 0,
            0, 0, 0, 0, 0, 0,
        }, pixels);
        Assert.Equal(6, result.BlocksChanged);
        Assert.True(result.Changed![1 * 6 + 1]);
        Assert.False(result.Changed[0]);
    }

    [Fact]
    public void Clips_a_piece_running_past_the_texture_edge()
    {
        string folder = WritePiece("event_000002", [1, 2, 3, 4, 5, 6]);
        File.WriteAllLines(Path.Combine(folder, "texture_info"), ["mask 6 4 4 3 3 2", "corruption_mask.dds 1 0", "4 3 3 2 1 3 2 128"]);
        var piece = EventPiece.Load(folder);
        string map = Path.Combine(_root, "corruption_mask.dds");
        File.WriteAllBytes(map, [.. L8Header(6, 4), .. new byte[24]]);
        string patched = Path.Combine(_root, "patched.dds");

        DdsPiecePatcher.Patch(map, patched, "corruption_mask.dds", [piece], _ => { });

        // Only the piece's first row and first two columns fit.
        byte[] pixels = File.ReadAllBytes(patched)[128..];
        Assert.Equal(new byte[] { 1, 2 }, pixels[(3 * 6 + 4)..(3 * 6 + 6)]);
        Assert.Equal(22, pixels.Count(b => b == 0));
    }

    [Fact]
    public void Culture_sections_give_each_entity_its_mask()
    {
        string path = Path.Combine(_root, "objects.culture");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(2u); writer.Write("p\0\0\0"u8); writer.Write(0x4UL); writer.Write(0x20000000UL);
            writer.Write(1u); writer.Write("ht\0\0"u8); writer.Write(0x8UL);
        }
        var sections = PieceCulture.Read(path);

        var bmd = new CompiledBmd("area");
        bmd.Props.Add(new BmdProp { Path = "a", Transform = new BmdTransform(0, 0, 0), CultureMask = 1 });
        bmd.Props.Add(new BmdProp { Path = "b", Transform = new BmdTransform(1, 0, 0), CultureMask = 1 });
        bmd.Vfx.Add(new BmdVfx { Name = "v", Transform = new BmdTransform(0, 0, 0), CultureMask = 1 });
        var hole = new BmdTransform(0, 0, 0);
        bmd.TerrainHoleTris.Add(new BmdTerrainHoleTri { Transform = hole, Vertex2 = new BmdTransform(1, 0, 0), Vertex3 = new BmdTransform(0, 0, 1) });
        bmd.TerrainHoleTris.Add(new BmdTerrainHoleTri { Transform = hole, Vertex2 = new BmdTransform(0, 0, 1), Vertex3 = new BmdTransform(-1, 0, 0) });

        var warnings = new List<string>();
        bool holesApplied = PieceCulture.Apply(bmd, sections, "test", warnings.Add);

        Assert.Equal([0x4UL, 0x20000000UL], bmd.Props.Select(p => p.CultureMask));
        // No 'v' section: the placeholder 1 would read as culture bit 0, so it becomes unrestricted.
        Assert.Equal(ulong.MaxValue, bmd.Vfx[0].CultureMask);
        Assert.Single(warnings);
        // One 'ht' mask for two triangles: left to the caller.
        Assert.False(holesApplied);
        Assert.Equal(0x8UL, PieceCulture.HoleMasks.Combined(sections));
    }

    [Fact]
    public void Devastated_roads_take_the_rotation_of_the_vanilla_road_they_replace()
    {
        var normal = new PieceSet
        {
            MapFolder = "", EventTiles = [@"terrain\tiles\campaign\roads\2x2_a\"], EventTrees = [],
            Pieces = [EventPiece.Load(WriteTiles("n", [(10, 20, 0)]))],
        };
        var devastate = new PieceSet
        {
            MapFolder = "", EventTiles = [@"terrain\tiles\campaign\roads_grey\2x2_a\"], EventTrees = [],
            Pieces = [EventPiece.Load(WriteTiles("d", [(10, 20, 0), (30, 40, 0)]))],
        };
        var vanilla = new TileMapRenderer.TileList(64, 64, ["temperate"], new float[7],
        [
            new(@"terrain\tiles\campaign\grass\a\", 0, 0, 0, 16),
            new(@"TERRAIN\TILES\CAMPAIGN\ROADS\2X2_A", 3, 10, 20, 128),
        ]);

        var merged = DevastatedMap.MergeRoads(vanilla, normal, devastate, _ => { });

        Assert.Equal(3, merged.Tiles.Count);
        Assert.Contains(merged.Tiles, t => t.TilePath.Contains("grass"));
        var replaced = Assert.Single(merged.Tiles, t => t.X == 10);
        Assert.Equal((@"terrain\tiles\campaign\roads_grey\2x2_a\", (byte)128, (byte)3), (replaced.TilePath, replaced.Direction, replaced.Climate));
        Assert.Equal(16, merged.Tiles.Single(t => t.X == 30).Direction);
    }

    [Fact]
    public void A_world_position_maps_to_its_event_mask_pixel_by_bobs_rule()
    {
        // world 100 wide on a 100 x 50 mask: one pixel per unit across, 1.15476 units per row
        int At(float x, float z) => DevastatedMap.MaskPixelAt(x, z, 100, 100, 50);

        Assert.Equal(49 * 100 + 10, At(10.4f, 0));          // rows top-down: z 0 is the bottom row
        Assert.Equal(49 * 100 + 11, At(10.5f, 0));          // halves round away from zero
        Assert.Equal(0, At(-0.4f, 49 * DevastatedMap.HexRowScale));
        Assert.Equal(-1, At(-0.6f, 0));                     // off the mask
        Assert.Equal(-1, At(10, 50 * DevastatedMap.HexRowScale));
    }

    private string WriteTiles(string name, (ushort X, ushort Y, uint Tile)[] tiles)
    {
        string folder = WritePiece($"event_{name}", new byte[6]);
        using var writer = new BinaryWriter(File.Create(Path.Combine(folder, "tile_list")));
        writer.Write(1u);
        foreach (var (x, y, tile) in tiles)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(tile);
        }
        return folder;
    }
}
