using System.Buffers.Binary;
using System.Text;

namespace TwMapDecompiler.Core.Tiles;

public readonly record struct TileColor(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>One TILE_SET record: a tile category and the colour it takes on the tile map.</summary>
public sealed record TileSetInfo(
    string Name, string LinkingTile, string SharedGeometry, string AlsoPlaceTileSet, string LinkAsSet,
    TileColor Color, bool ExcludeFromGlobalMesh);

/// <summary>One CLIMATE record: a climate name and the colour it takes on the climate map.</summary>
public sealed record ClimateInfo(string Name, TileColor Color);

/// <summary>
/// Reads terrain/tiles/&lt;campaign|battle&gt;/_tile_database/_settings.bin, the authoritative
/// source for tile-map and climate-map colours. The Assembly Kit ships an XML twin of this
/// file for battle (_settings.xml) but not for campaign, so the binary is the only source.
///
/// Layout (little-endian; every string is a UInt16 length followed by that many bytes):
///
///   "FASTBIN0" | u16 version
///   RENDER_PARAMS, CONVERSION_PARAMS, ...     version-dependent, not a fixed size
///   u32 climate_count
///     CLIMATE   u16 ver | name | u8 r,g,b
///               u32 texture_count
///                 TEXTURE  u16 ver | name | &lt;fixed payload&gt;
///               | vampire_creep_climate | chaos_creep_climate | f32 grass_..._saturation
///   u32 tile_set_count
///     TILE_SET  u16 ver | name | linking_tile | shared_geometry | also_place_tile_set | link_as_set
///               | u8 red, green, blue, exclude_from_global_mesh
///
/// The leading parameter blocks vary by title and serialise version, so rather than skipping a
/// fixed number of bytes this seeks the two lists from the back: TILE_SETS is the last structure
/// in the file, and CLIMATES is the last one before it. A candidate offset is only accepted when
/// its records parse to exactly the byte the next structure starts at, which makes a wrong guess
/// self-detecting. Verified against the Assembly Kit XML: all 163 battle TILE_SETs round-trip on
/// every field.
/// </summary>
public sealed class TileSettings
{
    /// <summary>The TILE_SET serialise version WH3 uses, and the layout this reader knows.</summary>
    private const int KnownTileSetVersion = 3;

    private readonly Dictionary<string, TileSetInfo> _tileSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClimateInfo> _climates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<TileSetInfo> TileSets => _tileSets.Values;
    public IReadOnlyCollection<ClimateInfo> Climates => _climates.Values;

    public bool TryGetTileSet(string name, out TileSetInfo info) => _tileSets.TryGetValue(name, out info!);
    public bool TryGetClimate(string name, out ClimateInfo info) => _climates.TryGetValue(name, out info!);

    /// <summary>
    /// Locates _settings.bin for a tile database. <paramref name="tileDirectory"/> is the
    /// "tiles" folder the tile shapes live in; _settings.bin sits beside it in _tile_database.
    /// </summary>
    public static string? FindBeside(string tileDirectory)
    {
        foreach (string? dir in new[] { tileDirectory, Path.GetDirectoryName(tileDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) })
        {
            if (dir == null)
                continue;
            string candidate = Path.Combine(dir, "_settings.bin");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static TileSettings Load(string settingsPath, Action<string>? log = null)
    {
        byte[] data = File.ReadAllBytes(settingsPath);
        if (data.Length < 10 || Encoding.ASCII.GetString(data, 0, 8) != "FASTBIN0")
            throw new InvalidDataException($"'{Path.GetFileName(settingsPath)}' is not a FASTBIN0 file");

        var settings = new TileSettings();

        // A title that bumped the record version without changing its shape still parses, since
        // a genuinely different layout cannot end exactly on the end of the file.
        int? tileSetsAt = FindTileSets(data, settings._tileSets, KnownTileSetVersion, out _);
        if (tileSetsAt == null)
        {
            tileSetsAt = FindTileSets(data, settings._tileSets, requiredVersion: null, out int foundVersion);
            if (tileSetsAt != null)
                log?.Invoke($"Note: TILE_SET serialise version {foundVersion}, expected {KnownTileSetVersion}; " +
                            "the record layout is unchanged, so the colours were read anyway.");
        }
        if (tileSetsAt == null)
            throw new InvalidDataException(
                $"Could not locate the TILE_SETS list in '{Path.GetFileName(settingsPath)}'. " +
                "The file's tile set records do not match the layout this build knows " +
                "(u16 version, 5 strings, then red, green, blue and exclude_from_global_mesh).");
        log?.Invoke($"Tile settings: {settings._tileSets.Count} tile sets");

        if (FindClimates(data, tileSetsAt.Value, settings._climates))
            log?.Invoke($"Tile settings: {settings._climates.Count} climates");
        else
            log?.Invoke("Warning: could not locate the CLIMATES list in _settings.bin; climate map will be blank");

        return settings;
    }

    /// <summary>
    /// TILE_SETS is the final structure in the file: scan back for a count whose records end
    /// exactly at EOF. Returns the offset of the count field. <paramref name="requiredVersion"/>
    /// pins the record serialise version, or null to accept any version the records agree on.
    /// </summary>
    private static int? FindTileSets(byte[] data, Dictionary<string, TileSetInfo> into,
        int? requiredVersion, out int version)
    {
        version = 0;
        for (int offset = data.Length - 4; offset >= 10; offset--)
        {
            var cursor = new Cursor(data, offset);
            if (!cursor.TryReadCount(out int count) || count == 0)
                continue;

            var parsed = new List<TileSetInfo>(count);
            int listVersion = requiredVersion ?? 0;
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
            {
                ok = TryReadTileSet(ref cursor, i == 0 ? requiredVersion : listVersion, out var tileSet, out listVersion);
                if (ok)
                    parsed.Add(tileSet);
            }

            if (!ok || cursor.Position != data.Length)
                continue;
            version = listVersion;

            foreach (var tileSet in parsed)
                into[tileSet.Name] = tileSet;
            return offset;
        }
        return null;
    }

    /// <summary>
    /// CLIMATES is the structure immediately before TILE_SETS. Each climate embeds a TEXTURE
    /// list whose records carry a fixed payload whose size depends on the TEXTURE serialise
    /// version, so both the list offset and that payload size are solved for together, accepting
    /// only the combination that ends exactly where TILE_SETS begins.
    /// </summary>
    private static bool FindClimates(byte[] data, int tileSetsAt, Dictionary<string, ClimateInfo> into)
    {
        for (int offset = tileSetsAt - 4; offset >= 10; offset--)
        {
            if (!new Cursor(data, offset).TryReadCount(out int count))
                continue;

            for (int texturePayload = 0; texturePayload <= 256; texturePayload += 4)
            {
                var cursor = new Cursor(data, offset);
                cursor.TryReadCount(out _);

                var parsed = new List<ClimateInfo>(count);
                bool ok = true;
                for (int i = 0; i < count && ok; i++)
                {
                    ok = TryReadClimate(ref cursor, texturePayload, out var climate);
                    if (ok)
                        parsed.Add(climate);
                }

                if (!ok || cursor.Position != tileSetsAt)
                    continue;

                foreach (var climate in parsed)
                    into[climate.Name] = climate;
                return true;
            }
        }
        return false;
    }

    private static bool TryReadTileSet(ref Cursor cursor, int? requiredVersion,
        out TileSetInfo tileSet, out int version)
    {
        tileSet = null!;
        if (!cursor.TryReadU16(out version))
            return false;
        // An unconstrained first record fixes the version the rest of the list must share.
        if (requiredVersion is { } required ? version != required : version is 0 or > 64)
            return false;
        if (!cursor.TryReadString(out string name) || name.Length == 0)
            return false;
        if (!cursor.TryReadString(out string linkingTile))
            return false;
        if (!cursor.TryReadString(out string sharedGeometry))
            return false;
        if (!cursor.TryReadString(out string alsoPlace))
            return false;
        if (!cursor.TryReadString(out string linkAsSet))
            return false;
        if (!cursor.TryReadBytes(4, out var rgbx))
            return false;

        tileSet = new TileSetInfo(name, linkingTile, sharedGeometry, alsoPlace, linkAsSet,
            new TileColor(rgbx[0], rgbx[1], rgbx[2]), rgbx[3] != 0);
        return true;
    }

    private static bool TryReadClimate(ref Cursor cursor, int texturePayload, out ClimateInfo climate)
    {
        climate = null!;
        if (!cursor.TryReadU16(out _))                                  // serialise version
            return false;
        if (!cursor.TryReadString(out string name) || name.Length == 0)
            return false;
        if (!cursor.TryReadBytes(3, out var rgb))
            return false;
        if (!cursor.TryReadCount(out int textureCount))
            return false;

        for (int t = 0; t < textureCount; t++)
        {
            if (!cursor.TryReadU16(out _) || !cursor.TryReadString(out _) || !cursor.Skip(texturePayload))
                return false;
        }

        if (!cursor.TryReadString(out _))                               // vampire_creep_climate
            return false;
        if (!cursor.TryReadString(out _))                               // chaos_creep_climate
            return false;
        if (!cursor.Skip(4))                                            // grass_..._saturation
            return false;

        climate = new ClimateInfo(name, new TileColor(rgb[0], rgb[1], rgb[2]));
        return true;
    }

    /// <summary>Bounds-checked forward reader; every read returns false rather than throwing.</summary>
    private struct Cursor(byte[] data, int position)
    {
        private readonly byte[] _data = data;

        public int Position { get; private set; } = position;

        public bool Skip(int count)
        {
            if (count < 0 || Position + count > _data.Length)
                return false;
            Position += count;
            return true;
        }

        public bool TryReadU16(out int value)
        {
            value = 0;
            if (Position + 2 > _data.Length)
                return false;
            value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position));
            Position += 2;
            return true;
        }

        /// <summary>Reads a list length, rejecting values too large to be a real record count.</summary>
        public bool TryReadCount(out int value)
        {
            value = 0;
            if (Position + 4 > _data.Length)
                return false;
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
            if (raw > (uint)_data.Length)
                return false;
            value = (int)raw;
            Position += 4;
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadU16(out int length))
                return false;
            if (Position + length > _data.Length)
                return false;
            var span = _data.AsSpan(Position, length);
            foreach (byte b in span)
                if (b < 0x20 || b > 0x7E)   // names and paths are plain ASCII
                    return false;
            value = Encoding.UTF8.GetString(span);
            Position += length;
            return true;
        }

        public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (Position + count > _data.Length)
                return false;
            value = _data.AsSpan(Position, count);
            Position += count;
            return true;
        }
    }
}
