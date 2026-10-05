using System.Text;
using TwMapDecompiler.Core.Tiles;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// Regression tests for the _settings.bin reader, built on synthetic files with exactly-known
/// contents. TileSettings locates the CLIMATES and TILE_SETS lists by seeking back from the end
/// of the file rather than skipping a fixed-size header, so these fix the behaviour that matters:
/// it finds the real lists, and it is not fooled by header bytes that happen to look like a list.
///
/// The real files are covered separately: parsing the shipped battle _settings.bin reproduces all
/// 163 TILE_SETs and 41 CLIMATEs in the Assembly Kit's _settings.xml twin, field for field.
/// </summary>
public class TileSettingsTests
{
    private sealed class Builder
    {
        private readonly List<byte> _bytes = [];

        public Builder Header(params byte[] junk)
        {
            _bytes.AddRange(Encoding.ASCII.GetBytes("FASTBIN0"));
            _bytes.AddRange([1, 0]);            // file serialise version
            _bytes.AddRange(junk);              // stand-in for RENDER_PARAMS etc.
            return this;
        }

        public Builder U16(int v) { _bytes.AddRange([(byte)(v & 0xFF), (byte)(v >> 8)]); return this; }

        public Builder U32(int v)
        {
            _bytes.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
            return this;
        }

        public Builder Str(string s)
        {
            U16(s.Length);
            _bytes.AddRange(Encoding.ASCII.GetBytes(s));
            return this;
        }

        public Builder Bytes(params byte[] b) { _bytes.AddRange(b); return this; }

        public Builder Climate(string name, byte r, byte g, byte b, int textureCount, int texturePayload)
        {
            U16(8).Str(name).Bytes(r, g, b).U32(textureCount);
            for (int i = 0; i < textureCount; i++)
                U16(10).Str($"tex{i}").Bytes(new byte[texturePayload]);
            return Str("").Str("").Bytes(0, 0, 0x80, 0x3F);  // creep climates + f32 1.0
        }

        public Builder TileSet(string name, byte r, byte g, byte b, bool exclude = true, string linkAsSet = "",
            int version = 3)
            => U16(version).Str(name).Str("").Str("").Str("").Str(linkAsSet).Bytes(r, g, b, (byte)(exclude ? 1 : 0));

        public byte[] Raw() => [.. _bytes];

        public string Write()
        {
            string path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, [.. _bytes]);
            return path;
        }
    }

    private static TileSettings Load(Builder builder)
    {
        string path = builder.Write();
        try { return TileSettings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsTileSetColoursAndFields()
    {
        var settings = Load(new Builder()
            .Header(0xAA, 0xBB, 0xCC, 0xDD)
            .U32(1).Climate("default", 0xB9, 0x9A, 0x6B, textureCount: 3, texturePayload: 56)
            .U32(3)
                .TileSet("generic", 0xDF, 0xB4, 0x91, exclude: false)
                .TileSet("sea", 0x53, 0x8D, 0xD5, exclude: false)
                .TileSet("cliff_gen", 0xFD, 0x03, 0x01, linkAsSet: "sea_coast"));

        Assert.Equal(3, settings.TileSets.Count);

        Assert.True(settings.TryGetTileSet("generic", out var generic));
        Assert.Equal(new TileColor(0xDF, 0xB4, 0x91), generic.Color);
        Assert.False(generic.ExcludeFromGlobalMesh);

        Assert.True(settings.TryGetTileSet("cliff_gen", out var cliff));
        Assert.Equal("sea_coast", cliff.LinkAsSet);
        Assert.True(cliff.ExcludeFromGlobalMesh);

        Assert.True(settings.TryGetClimate("default", out var climate));
        Assert.Equal(new TileColor(0xB9, 0x9A, 0x6B), climate.Color);
    }

    /// <summary>
    /// The TEXTURE payload size follows that record's serialise version, so the reader solves for
    /// it rather than assuming the version the shipped files happen to use.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(56)]
    [InlineData(120)]
    public void FindsClimatesWhateverTheTexturePayloadSize(int texturePayload)
    {
        var settings = Load(new Builder()
            .Header(0x01, 0x02)
            .U32(2)
                .Climate("cold", 0, 85, 85, textureCount: 2, texturePayload: texturePayload)
                .Climate("arid", 255, 170, 0, textureCount: 5, texturePayload: texturePayload)
            .U32(1).TileSet("generic", 1, 2, 3));

        Assert.Equal(2, settings.Climates.Count);
        Assert.True(settings.TryGetClimate("arid", out var arid));
        Assert.Equal(new TileColor(255, 170, 0), arid.Color);
    }

    /// <summary>
    /// A header that happens to contain plausible-looking counts and strings must not be mistaken
    /// for the lists: only an offset whose records end exactly on the next structure is accepted.
    /// </summary>
    [Fact]
    public void IgnoresHeaderBytesThatLookLikeAList()
    {
        // A well-formed TILE_SETS list buried in the header, which a backwards scan would reach
        // only if it accepted records that stop short of the next structure.
        byte[] decoy = new Builder().U32(1).TileSet("decoy", 9, 9, 9).Raw();

        var settings = Load(new Builder()
            .Header(decoy)
            .U32(1).Climate("default", 1, 1, 1, textureCount: 1, texturePayload: 56)
            .U32(1).TileSet("generic", 0xDF, 0xB4, 0x91));

        Assert.Single(settings.TileSets);
        Assert.False(settings.TryGetTileSet("decoy", out _));
        Assert.True(settings.TryGetTileSet("generic", out var generic));
        Assert.Equal(new TileColor(0xDF, 0xB4, 0x91), generic.Color);
    }

    /// <summary>
    /// A title that bumps the TILE_SET version without changing the record's shape should still
    /// read, since only a genuinely different layout fails to end exactly on the end of the file.
    /// </summary>
    [Fact]
    public void ReadsTileSetsWhoseVersionIsNotTheOneWarhammerUses()
    {
        var settings = Load(new Builder()
            .Header(0x00, 0x00)
            .U32(1).Climate("default", 1, 1, 1, textureCount: 1, texturePayload: 56)
            .U32(2)
                .TileSet("generic", 10, 20, 30, version: 4)
                .TileSet("sea", 40, 50, 60, version: 4));

        Assert.Equal(2, settings.TileSets.Count);
        Assert.True(settings.TryGetTileSet("sea", out var sea));
        Assert.Equal(new TileColor(40, 50, 60), sea.Color);
    }

    /// <summary>Records must agree on a version; a list that does not is not a list.</summary>
    [Fact]
    public void RejectsTileSetsWithInconsistentVersions()
    {
        string path = new Builder()
            .Header(0x00, 0x00)
            .U32(2)
                .TileSet("generic", 10, 20, 30, version: 4)
                .TileSet("sea", 40, 50, 60, version: 5)
            .Write();
        try { Assert.Throws<InvalidDataException>(() => TileSettings.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsAFileThatIsNotFastbin()
    {
        string path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("NOTFASTBIN0000000"));
        try { Assert.Throws<InvalidDataException>(() => TileSettings.Load(path)); }
        finally { File.Delete(path); }
    }
}
