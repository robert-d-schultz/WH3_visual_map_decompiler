using TwMapDecompiler.Core.IO;
using TwMapDecompiler.Core.Maths;

namespace TwMapDecompiler.Core.Bmd;

/// <summary>
/// Parses a compiled global_props.bin (FASTBIN0 container of per-region BMD blobs)
/// into entity collections, ready to be written out as Terry layers. Regions are
/// grouped into provinces; the regions with no province (seas, rivers, lakes,
/// wastelands) share one "_global" layer, which also collects the map's light
/// probes, its sea water planes and its full-shroud border meshes. The map's sound
/// emitters live in a separate global_props_sound.bin and are merged into the same
/// province layers.
///
/// The binary layout is a direct port of the reverse-engineered reference script,
/// with two fixes: spot lights now use their own position/rotation (the script
/// accidentally reused variables from the previous section), and sphere sound
/// emitters get a real radius value.
/// </summary>
public sealed class GlobalPropsParser
{
    private readonly BinaryReader _reader;
    private readonly Dictionary<string, uint> _entityOffsets = [];
    private long _bodyStart;
    private readonly Action<string> _log;
    private readonly IReadOnlyDictionary<string, string> _regionToProvince;
    private readonly CultureMask _cultures;

    /// <summary>
    /// Name of the catch-all layer: province-less regions, light probes, sea water planes,
    /// full-shroud meshes. The leading underscore sorts it above the province layers.
    /// </summary>
    public const string GlobalLayerName = "_global";

    private GlobalPropsParser(BinaryReader reader, CultureMask cultures, IReadOnlyDictionary<string, string> regionToProvince, Action<string> log)
    {
        _reader = reader;
        _cultures = cultures;
        _regionToProvince = regionToProvince;
        _log = log;
    }

    /// <param name="Layers">One entry per output layer: a province or the catch-all layer.</param>
    public sealed record Result(List<CompiledBmd> Layers);

    /// <param name="cultures">The culture mask's bits (prefab_types_tables); decides which masks are restricted.</param>
    /// <param name="regionToProvince">region -> province (region_to_province_junctions); when empty, every region becomes its own layer.</param>
    /// <param name="globalPropsSoundPath">
    /// Optional global_props_sound.bin: its sound emitters are merged into the layer of the
    /// province they belong to.
    /// </param>
    public static Result Parse(string globalPropsPath, string vanillaMapName, CultureMask cultures,
        IReadOnlyDictionary<string, string>? regionToProvince = null, Action<string>? log = null,
        string? globalPropsSoundPath = null)
    {
        regionToProvince ??= new Dictionary<string, string>();
        log ??= _ => { };

        Result result;
        using (var stream = new BufferedStream(File.OpenRead(globalPropsPath), 1 << 20))
        using (var reader = new BinaryReader(stream))
            result = new GlobalPropsParser(reader, cultures, regionToProvince, log).ParseFile(vanillaMapName);

        if (globalPropsSoundPath != null)
        {
            using var stream = new BufferedStream(File.OpenRead(globalPropsSoundPath), 1 << 20);
            using var reader = new BinaryReader(stream);
            new GlobalPropsParser(reader, cultures, regionToProvince, log).MergeSoundFile(result.Layers);
        }

        return result;
    }

    /// <summary>
    /// Parses one event-area piece BMD (pieces/event_*/objects*.bin or bmd_objects_sound*.bin):
    /// a bare FASTBIN0 body with no container around it and no child entries. Its culture mask
    /// fields are placeholders; the real masks are in the .culture file beside it
    /// (<see cref="Pieces.PieceCulture"/>), applied by the caller.
    /// </summary>
    public static CompiledBmd ParsePiece(string binPath, string layerName, CultureMask cultures, Action<string>? log = null)
    {
        const string entity = "piece";
        using var stream = new BufferedStream(File.OpenRead(binPath), 1 << 20);
        using var reader = new BinaryReader(stream);
        var parser = new GlobalPropsParser(reader, cultures, new Dictionary<string, string>(), log ?? (_ => { }));
        parser._entityOffsets[entity] = 0;
        parser._bodyStart = 0;

        var result = new CompiledBmd(layerName);
        result.Add(parser.ParseBmd(entity, ulong.MaxValue));
        return result;
    }

    /// <summary>
    /// Parses every tile of a sound-only container (global_props_sound*.bin, see
    /// <see cref="MergeSoundFile"/>) into one collection, regardless of region.
    /// </summary>
    public static CompiledBmd ParseSoundTiles(string path, string layerName, CultureMask cultures, Action<string>? log = null)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var reader = new BinaryReader(stream);
        var parser = new GlobalPropsParser(reader, cultures, new Dictionary<string, string>(), log ?? (_ => { }));
        parser.ReadHeader();

        var result = new CompiledBmd(layerName);
        foreach (string entityName in parser._entityOffsets.Keys)
            result.Add(parser.ParseBmd(entityName, ulong.MaxValue));
        result.CombineDuplicates();
        return result;
    }

    /// <summary>
    /// global_props_sound.bin uses the same container, but has no root entry: every entry
    /// is a top-level quadtree tile named after its region (bmd_objects.&lt;region&gt;.&lt;n&gt;.&lt;depth&gt;.bin)
    /// with no culture mask of its own. The tiles only carry sound emitters; they join the
    /// layer of the region's province, which is created when global_props.bin had none.
    /// </summary>
    private void MergeSoundFile(List<CompiledBmd> layers)
    {
        ReadHeader();

        var byLayer = new Dictionary<string, CompiledBmd>(StringComparer.Ordinal);
        foreach (string entityName in _entityOffsets.Keys)
        {
            var tile = ParseBmd(entityName, ulong.MaxValue);
            string layerName = LayerNameFor(tile.LayerName);
            if (!byLayer.TryGetValue(layerName, out var sounds))
                byLayer[layerName] = sounds = new CompiledBmd(layerName);
            sounds.Add(tile);
        }

        int total = 0, added = 0;
        foreach (var (layerName, sounds) in byLayer.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sounds.CombineDuplicates();
            if (sounds.EntityCount != sounds.SoundEmitters.Count)
                _log($"Warning: {layerName}: global_props_sound.bin holds {sounds.EntityCount - sounds.SoundEmitters.Count} non-sound entities, ignored.");
            if (sounds.SoundEmitters.Count == 0)
                continue;

            var layer = layers.Find(l => l.LayerName == layerName);
            if (layer == null)
            {
                layer = new CompiledBmd(layerName) { IsGlobal = layerName == GlobalLayerName };
                layers.Add(layer);
                added++;
            }
            layer.SoundEmitters.AddRange(sounds.SoundEmitters);
            total += sounds.SoundEmitters.Count;
        }

        _log($"global_props_sound.bin: {total} sound emitters from {_entityOffsets.Count} tiles into {byLayer.Count} layers" +
             (added > 0 ? $" ({added} layers had no props)" : ""));
    }

    private void ReadHeader()
    {
        uint entityCount = _reader.ReadUInt32();
        for (uint i = 0; i < entityCount; i++)
        {
            string name = _reader.ReadNullTerminatedString();
            _entityOffsets[name] = _reader.ReadUInt32();
        }
        _bodyStart = _reader.BaseStream.Position;
    }

    private Result ParseFile(string vanillaMapName)
    {
        // Header: entity name -> offset table.
        ReadHeader();

        string rootKey = $"terrain/campaigns/{vanillaMapName}/bmd_objects.bin";
        if (!_entityOffsets.TryGetValue(rootKey, out uint rootOffset))
        {
            var candidates = _entityOffsets.Keys.Where(k => k.EndsWith("/bmd_objects.bin")).ToList();
            throw new InvalidDataException(
                $"Root entry '{rootKey}' not found in global_props.bin. " +
                (candidates.Count > 0
                    ? $"Did you mean vanilla map name '{candidates[0].Split('/')[^2]}'?"
                    : "No bmd_objects.bin entries present."));
        }

        // Root BMD: only the list of child BMD entries matters.
        _reader.BaseStream.Seek(_bodyStart + rootOffset, SeekOrigin.Begin);
        _reader.Skip(8);  // "FASTBIN0"
        _reader.Skip(40);

        var rootBmds = new List<(string Region, string BmdName, ulong Mask)>();
        uint bmdCount = _reader.ReadUInt32();
        for (uint i = 0; i < bmdCount; i++)
        {
            var entry = ReadBmdEntry(descendIntoChild: false, out _);
            rootBmds.Add((entry.RegionName, entry.BmdName, entry.CultureMask));
        }

        // Group by province; the regions the junction table does not cover (seas, rivers,
        // lakes, wastelands, unknown terrain) all land in one catch-all layer.
        var regions = rootBmds.Select(e => e.Region).Distinct(StringComparer.Ordinal).ToList();
        var mappedRegions = regions.Where(_regionToProvince.ContainsKey).ToList();
        int unmappedRegions = _regionToProvince.Count == 0 ? 0 : regions.Count - mappedRegions.Count;
        if (_regionToProvince.Count > 0)
            _log($"{mappedRegions.Count} regions in {mappedRegions.Select(r => _regionToProvince[r]).Distinct(StringComparer.Ordinal).Count()} provinces");

        var layers = new List<CompiledBmd>();
        foreach (var group in rootBmds.GroupBy(e => LayerNameFor(e.Region), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var layer = new CompiledBmd(group.Key) { IsGlobal = group.Key == GlobalLayerName };
            foreach (var (_, bmdName, mask) in group)
            {
                var compiled = ParseBmd(bmdName, mask);
                compiled.CombineDuplicates();
                layer.Add(compiled);
            }
            _log($"{group.Key}: {layer.EntityCount} entities");
            layers.Add(layer);
        }

        if (_regionToProvince.Count == 0)
            _log("No region -> province mapping available: grouping by region instead.");
        else if (unmappedRegions > 0)
            _log($"{unmappedRegions} regions have no province (seas, rivers, lakes, wastelands): merged into '{GlobalLayerName}'.");

        FillGlobalLayer(layers);

        return new Result(layers);
    }

    /// <summary>The region's province; the catch-all layer when the junction table has no row for it.</summary>
    private string LayerNameFor(string region) =>
        _regionToProvince.Count == 0 ? region : _regionToProvince.GetValueOrDefault(region, GlobalLayerName);

    /// <summary>
    /// Moves what does not belong to a province into the catch-all layer: the light probes,
    /// which cover the map rather than a province; the sea water planes, which a province
    /// layer only holds because the sea happens to touch one of its regions; and the
    /// full-shroud meshes, which blank out the map's unreachable border.
    /// </summary>
    private void FillGlobalLayer(List<CompiledBmd> layers)
    {
        var global = layers.Find(l => l.IsGlobal);
        bool anythingToMove = layers.Any(l => !l.IsGlobal &&
            (l.LightProbes.Count > 0 || l.PolyMeshes.Any(IsMoveable)));
        if (global == null && anythingToMove)
        {
            global = new CompiledBmd(GlobalLayerName) { IsGlobal = true };
            layers.Add(global);
        }
        if (global == null)
            return;

        int probes = 0, seas = 0, shrouds = 0;
        foreach (var layer in layers)
        {
            if (layer.IsGlobal)
                continue;

            probes += layer.LightProbes.Count;
            global.LightProbes.AddRange(layer.LightProbes);
            layer.LightProbes.Clear();

            var meshes = layer.PolyMeshes.Where(IsMoveable).ToList();
            if (meshes.Count == 0)
                continue;
            layer.PolyMeshes.RemoveAll(IsMoveable);
            global.PolyMeshes.AddRange(meshes);
            seas += meshes.Count(m => m.IsSeaWaterPlane);
            shrouds += meshes.Count(m => m.IsFullShroud);
        }

        _log($"{GlobalLayerName}: {global.EntityCount} entities " +
             $"({global.LightProbes.Count} light probes, {global.PolyMeshes.Count(m => m.IsSeaWaterPlane)} sea water planes, " +
             $"{global.PolyMeshes.Count(m => m.IsFullShroud)} full-shroud meshes; " +
             $"moved in {probes} probes, {seas} sea water planes and {shrouds} full-shroud meshes from province layers)");
    }

    /// <summary>Poly meshes a province layer only holds incidentally; they belong to the map.</summary>
    private static bool IsMoveable(BmdPolyMesh mesh) => mesh.IsSeaWaterPlane || mesh.IsFullShroud;

    private sealed record BmdEntry(string BmdName, string RegionName, ulong CultureMask);

    /// <summary>
    /// Reads one BMD child entry (version, name, mask, region, bhm). When
    /// <paramref name="descendIntoChild"/> is set, the referenced child BMD is
    /// parsed recursively and returned via <paramref name="child"/>.
    /// </summary>
    private BmdEntry ReadBmdEntry(bool descendIntoChild, out CompiledBmd? child)
    {
        child = null;
        ushort version = _reader.ReadUInt16();
        string bmdName = _reader.ReadU16String();
        _reader.Skip(68);

        ulong mask = version <= 6
            ? CultureMask.FromInt32(_reader.ReadInt32())
            : _reader.ReadUInt64();

        if (descendIntoChild)
        {
            long resume = _reader.BaseStream.Position;
            child = ParseBmd(bmdName, mask);
            _reader.BaseStream.Seek(resume, SeekOrigin.Begin);
        }

        string regionName = _reader.ReadU16String();
        if (version <= 6)
            _reader.Skip(1);

        _reader.ReadU16String(); // bhm name

        if (version > 6)
            _reader.Skip(8);

        return new BmdEntry(bmdName, regionName, mask);
    }

    private CompiledBmd ParseBmd(string entityName, ulong parentMask)
    {
        string regionName = entityName.Split('.').Length > 1 ? entityName.Split('.')[1] : entityName;

        if (!_entityOffsets.TryGetValue(entityName, out uint offset))
            throw new InvalidDataException($"BMD entry '{entityName}' referenced but not present in header table.");

        _reader.BaseStream.Seek(_bodyStart + offset, SeekOrigin.Begin);
        _reader.Skip(8);  // "FASTBIN0"
        _reader.Skip(40);

        var compiled = new CompiledBmd(regionName);

        // Nested child BMDs (quadtree subdivision).
        uint childCount = _reader.ReadUInt32();
        for (uint i = 0; i < childCount; i++)
        {
            ReadBmdEntry(descendIntoChild: true, out var child);
            if (child != null)
                compiled.Add(child);
        }

        _reader.Skip(28);

        ParseProps(compiled, parentMask);
        ParseVfx(compiled, parentMask);
        _reader.Skip(26);
        ParseLightProbes(compiled);
        ParseTerrainHoles(compiled, parentMask);
        ParsePointLights(compiled, parentMask);
        _reader.Skip(31);
        ParsePolyMeshes(compiled, parentMask);
        _reader.Skip(6);
        ParseSpotLights(compiled, parentMask);
        ParseSoundEmitters(compiled, parentMask);
        ParseCompositeScenes(compiled, parentMask);

        return compiled;
    }

    private (BmdTransform Transform, Mat3 Matrix) ReadMatrixTransform()
    {
        var matrix = Mat3.Read(_reader);
        var (rx, ry, rz) = matrix.ToEulerXyzDegrees();
        var (sx, sy, sz) = matrix.ToScale();
        float px = _reader.ReadSingle();
        float py = _reader.ReadSingle();
        float pz = _reader.ReadSingle();
        return (new BmdTransform(px, py, pz, rx, ry, rz, sx, sy, sz), matrix);
    }

    /// <summary>Common trailing flag block (seasons + tactical view visibility).</summary>
    private (bool VisibleInTactical, bool OnlyVisibleInTactical) ReadFlagsBlock()
    {
        ushort flagsVersion = _reader.ReadUInt16();
        _reader.Skip(2);
        if (flagsVersion < 4)
            _reader.Skip(1);
        _reader.Skip(4); // per-season visibility, always true
        if (flagsVersion >= 4)
            return (_reader.ReadBool8(), _reader.ReadBool8());
        return (false, false);
    }

    private void ParseProps(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);

        var propTypes = new List<string>();
        uint typeCount = _reader.ReadUInt32();
        for (uint i = 0; i < typeCount; i++)
            propTypes.Add(_reader.ReadU16String());

        uint propCount = _reader.ReadUInt32();
        for (uint i = 0; i < propCount; i++)
        {
            ushort version = _reader.ReadUInt16();
            string path = propTypes[(int)_reader.ReadUInt32()];
            var (transform, _) = ReadMatrixTransform();

            bool isDecal = _reader.ReadBool8();
            _reader.ReadBool8(); // logical_decal
            _reader.ReadBool8(); // is_fauna
            bool visibleInsideSnow = _reader.ReadBool8();
            bool visibleOutsideSnow = _reader.ReadBool8();
            bool visibleInsideDestruction = _reader.ReadBool8();
            bool visibleOutsideDestruction = _reader.ReadBool8();
            _reader.ReadBool8(); // animated

            _reader.ReadSingle(); // decal_parallax_scale
            _reader.ReadSingle(); // decal_tiling
            _reader.ReadBool8();  // decal_override_gbuffer_normal

            var (visibleInTactical, onlyVisibleInTactical) = ReadFlagsBlock();

            bool visibleInShroud = _reader.ReadBool8();
            bool applyToTerrain = _reader.ReadBool8();
            bool applyToObjects = _reader.ReadBool8();
            _reader.ReadBool8(); // decal_render_above_snow - still written by the game, but no longer surfaced in the UI (superseded by decal_layer); dead end, not carried into Terry output

            _reader.ReadU16String(); // bhm name

            ulong ownMask = _reader.ReadUInt64();
            ulong mask = _cultures.ResolveInherited(parentMask, ownMask);

            bool castsShadow = _reader.ReadBool8();
            bool noCulling = _reader.ReadBool8();

            bool applyHeightPatch = false;
            if (version > 15)
            {
                bool hasHeightPatch = _reader.ReadBool8();
                bool apply = _reader.ReadBool8();
                applyHeightPatch = hasHeightPatch && apply;
            }
            bool renderIntoSkydomeFog = false;
            if (version > 18)
                renderIntoSkydomeFog = _reader.ReadBool8(); // include_in_fog
            bool visibleInShroudOnly = version > 19 && !_reader.ReadBool8();
            if (version == 21)
                _reader.Skip(1); // only present in v21, gone by v23
            if (version > 23)
                _reader.ReadBool8(); // use_dynamic_shadows
            if (version > 24)
                _reader.ReadBool8(); // uses_terrain_vertex_offset

            float blendEdgesWithTerrain = 0f;
            float decalFadeStart = 0f;
            float decalAngleThreshold = 0f;
            bool decalMirrorX = false;
            bool decalMirrorZ = false;
            int decalLayer = 0;
            bool decalTilingAlpha = false;
            if (version > 25)
            {
                blendEdgesWithTerrain = _reader.ReadSingle();
                decalFadeStart = _reader.ReadSingle();
                decalAngleThreshold = _reader.ReadSingle();
                decalMirrorX = _reader.ReadBool8();
                decalMirrorZ = _reader.ReadBool8();
                decalLayer = _reader.ReadInt32();
                decalTilingAlpha = _reader.ReadBool8();
            }

            compiled.Props.Add(new BmdProp
            {
                Path = path,
                Transform = transform,
                CultureMask = mask,
                ApplyToTerrain = applyToTerrain,
                ApplyToObjects = applyToObjects,
                VisibleInTactical = visibleInTactical,
                OnlyVisibleInTactical = onlyVisibleInTactical,
                ApplyHeightPatch = applyHeightPatch,
                VisibleInShroud = visibleInShroud,
                NoCulling = noCulling,
                IsDecal = isDecal,
                VisibleInShroudOnly = visibleInShroudOnly,
                VisibleInsideSnowRegion = visibleInsideSnow,
                VisibleOutsideSnowRegion = visibleOutsideSnow,
                VisibleInsideDestructionRegion = visibleInsideDestruction,
                VisibleOutsideDestructionRegion = visibleOutsideDestruction,
                CastsShadow = castsShadow,
                RenderIntoSkydomeFog = renderIntoSkydomeFog,
                BlendEdgesWithTerrain = blendEdgesWithTerrain,
                DecalFadeStart = decalFadeStart,
                DecalAngleThreshold = decalAngleThreshold,
                DecalMirrorX = decalMirrorX,
                DecalMirrorZ = decalMirrorZ,
                DecalLayer = decalLayer,
                DecalTilingAlpha = decalTilingAlpha,
            });
        }
    }

    private void ParseVfx(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            string name = _reader.ReadU16String();
            var (transform, _) = ReadMatrixTransform();

            _reader.ReadSingle();     // emission_rate
            _reader.ReadU16String();  // instance_name

            ReadFlagsBlock();

            _reader.ReadU16String(); // bhm name

            ulong mask = _cultures.ResolveInheritedOverride(parentMask, _reader.ReadUInt64());

            _reader.ReadBool8(); // autoplay
            bool visibleInShroud = _reader.ReadBool8();

            bool visibleInShroudOnly = false;
            bool noCulling = false;
            if (version > 7)
            {
                _reader.Skip(4); // parent id
                visibleInShroudOnly = !_reader.ReadBool8();
                noCulling = _reader.ReadBool8();
            }

            compiled.Vfx.Add(new BmdVfx
            {
                Name = name,
                Transform = transform,
                CultureMask = mask,
                VisibleInShroud = visibleInShroud,
                VisibleInShroudOnly = visibleInShroudOnly,
                NoCulling = noCulling,
            });
        }
    }

    private void ParseLightProbes(CompiledBmd compiled)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            float x = _reader.ReadSingle();
            float y = _reader.ReadSingle();
            float z = _reader.ReadSingle();
            float outerRadius = _reader.ReadSingle();
            float innerRadius = version > 2 ? _reader.ReadSingle() : outerRadius;
            _reader.Skip(1);
            bool isPrimary = version > 2 && _reader.ReadBool8();
            _reader.ReadU16String(); // bhm name

            compiled.LightProbes.Add(new BmdLightProbe
            {
                Transform = new BmdTransform(x, y, z),
                IsPrimary = isPrimary,
                OuterRadius = outerRadius,
                InnerRadius = innerRadius,
            });
        }
    }

    private void ParseTerrainHoles(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            var v1 = ReadVec3();
            var v2 = ReadVec3();
            var v3 = ReadVec3();
            _reader.ReadU16String(); // bhm name
            if (version > 2)
                _reader.Skip(10);

            // Terry stores holes as a 2D polyline plus a rotation: flatten the
            // triangle onto the XZ plane and keep the inverse rotation as eulers.
            var hole = TerrainHoleMath.Flatten(v1, v2, v3);

            compiled.TerrainHoleTris.Add(new BmdTerrainHoleTri
            {
                Transform = hole.Transform,
                CultureMask = parentMask,
                Vertex2 = hole.Vertex2,
                Vertex3 = hole.Vertex3,
            });
        }
    }

    private void ParsePointLights(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            var (x, y, z) = ReadVec3();
            float radius = _reader.ReadSingle();
            float r = _reader.ReadSingle();
            float g = _reader.ReadSingle();
            float b = _reader.ReadSingle();
            float colorScale = _reader.ReadSingle();

            string animType = _reader.ReadByte() switch
            {
                1 => "LAT_RADIUS_SIN",
                2 => "LAT_RADIUS_SIN_SIN",
                _ => "LAT_NONE",
            };

            float animScale1 = _reader.ReadSingle();
            float animScale2 = _reader.ReadSingle();
            float colorMin = _reader.ReadSingle();
            float randOffset = _reader.ReadSingle();
            string falloffType = _reader.ReadU16String();
            _reader.Skip(1);
            _reader.ReadU16String(); // bhm name
            bool lightProbesOnly = _reader.ReadBool8();

            ulong mask = _cultures.ResolveInheritedOverride(parentMask, _reader.ReadUInt64());

            if (version > 6)
                _reader.Skip(10);

            compiled.PointLights.Add(new BmdPointLight
            {
                Transform = new BmdTransform(x, y, z),
                CultureMask = mask,
                R = r, G = g, B = b,
                AnimType = animType,
                AnimScale1 = animScale1,
                AnimScale2 = animScale2,
                ColorMin = colorMin,
                RandOffset = randOffset,
                FalloffType = falloffType,
                LightProbesOnly = lightProbesOnly,
                ColorScale = colorScale,
                Radius = radius,
            });
        }
    }

    private void ParsePolyMeshes(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();

            var vertices = new List<(float, float, float)>();
            uint vertexCount = _reader.ReadUInt32();
            for (uint v = 0; v < vertexCount; v++)
                vertices.Add(ReadVec3());

            uint triangleCount = _reader.ReadUInt32();
            _reader.Skip((int)(triangleCount * 2)); // triangle indices, not needed by Terry

            string material = _reader.ReadU16String();
            _reader.ReadU16String(); // bhm name

            bool visibleInTactical = false, onlyVisibleInTactical = false;
            if (version > 2)
            {
                ushort flagsVersion = _reader.ReadUInt16();
                _reader.Skip(6);
                if (flagsVersion >= 4)
                {
                    visibleInTactical = _reader.ReadBool8();
                    onlyVisibleInTactical = _reader.ReadBool8();
                }
            }

            BmdTransform transform;
            bool visibleInShroud;
            if (version >= 4)
            {
                (transform, _) = ReadMatrixTransform();
                _reader.Skip(4);
                visibleInShroud = _reader.ReadBool8();
                _reader.Skip(1);
            }
            else
            {
                transform = new BmdTransform(0, 0, 0);
                visibleInShroud = true;
            }

            compiled.PolyMeshes.Add(new BmdPolyMesh
            {
                Transform = transform,
                CultureMask = parentMask,
                Material = material,
                Vertices = vertices,
                VisibleInTactical = visibleInTactical,
                OnlyVisibleInTactical = onlyVisibleInTactical,
                VisibleInShroud = visibleInShroud,
            });
        }
    }

    private void ParseSpotLights(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            var (x, y, z) = ReadVec3();

            float qx = _reader.ReadSingle();
            float qy = _reader.ReadSingle();
            float qz = _reader.ReadSingle();
            float qw = _reader.ReadSingle();
            var (ex, ey, ez) = QuaternionToEulerDegrees(qx, qy, qz, qw);

            float length = _reader.ReadSingle();
            float innerAngle = _reader.ReadSingle();
            float outerAngle = _reader.ReadSingle();
            float r = _reader.ReadSingle();
            float g = _reader.ReadSingle();
            float b = _reader.ReadSingle();
            float intensity = Math.Max(r, Math.Max(g, b));
            int r8 = intensity > 0 ? (int)(r / intensity * 255) : 0;
            int g8 = intensity > 0 ? (int)(g / intensity * 255) : 0;
            int b8 = intensity > 0 ? (int)(b / intensity * 255) : 0;

            float falloff = _reader.ReadSingle();
            string gobo = _reader.ReadU16String();
            bool volumetric = _reader.ReadBool8();
            _reader.ReadU16String(); // bhm name

            ulong mask = _cultures.ResolveInheritedOverride(parentMask, _reader.ReadUInt64());

            if (version > 6)
                _reader.Skip(10);

            compiled.SpotLights.Add(new BmdSpotLight
            {
                Transform = new BmdTransform(x, y, z, ex, ey, ez),
                CultureMask = mask,
                R = r8, G = g8, B = b8,
                Intensity = intensity,
                Length = length,
                InnerAngle = innerAngle,
                OuterAngle = outerAngle,
                Falloff = falloff,
                Volumetric = volumetric,
                Gobo = gobo,
            });
        }
    }

    private void ParseSoundEmitters(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            string soundName = _reader.ReadU16String();
            string sstType = _reader.ReadU16String();

            var coords = new List<(float, float, float)>();
            uint coordCount = _reader.ReadUInt32();
            for (uint c = 0; c < coordCount; c++)
                coords.Add(ReadVec3());

            _reader.Skip(4);
            float radius = _reader.ReadSingle();
            _reader.Skip(53);
            _reader.ReadU16String(); // bhm name

            ulong ownMask = version <= 7
                ? CultureMask.FromInt32(_reader.ReadInt32())
                : _reader.ReadUInt64();
            ulong mask = _cultures.ResolveInheritedOverride(parentMask, ownMask);

            _reader.Skip(8);

            if (version > 7)
            {
                _reader.Skip(24);
                _reader.ReadU16String(); // sound marker name
            }

            if (coords.Count == 0)
            {
                _log($"Warning: sound emitter '{soundName}' has no coordinates, skipped.");
                continue;
            }

            compiled.SoundEmitters.Add(new BmdSoundEmitter
            {
                SoundName = soundName,
                Transform = new BmdTransform(coords[0].Item1, coords[0].Item2, coords[0].Item3),
                CultureMask = mask,
                SstType = sstType,
                Coords = coords,
                Radius = radius,
            });
        }
    }

    private void ParseCompositeScenes(CompiledBmd compiled, ulong parentMask)
    {
        _reader.Skip(2);
        uint count = _reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            ushort version = _reader.ReadUInt16();
            var (transform, _) = ReadMatrixTransform();
            string name = _reader.ReadU16String();
            _reader.ReadU16String(); // bhm name

            ulong mask = _cultures.ResolveInheritedOverride(parentMask, _reader.ReadUInt64());

            _reader.ReadBool8(); // autoplay
            bool visibleInShroud = _reader.ReadBool8();
            bool noCulling = _reader.ReadBool8();

            bool visibleInShroudOnly = false;
            if (version > 7)
            {
                _reader.ReadU16String(); // script id
                _reader.ReadU16String(); // parent script id
                visibleInShroudOnly = !_reader.ReadBool8();
                _reader.ReadBool8(); // visible_in_tactical_view
                _reader.ReadBool8(); // visible_in_tactical_view_only
                if (version > 11)
                    _reader.Skip(2); // two more (probable) booleans
            }

            compiled.CompositeScenes.Add(new BmdCompositeScene
            {
                Name = name,
                Transform = transform,
                CultureMask = mask,
                VisibleInShroud = visibleInShroud,
                NoCulling = noCulling,
                VisibleInShroudOnly = visibleInShroudOnly,
            });
        }
    }

    private (float X, float Y, float Z) ReadVec3()
        => (_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

    private static (double X, double Y, double Z) QuaternionToEulerDegrees(float x, float y, float z, float w)
    {
        double ysqr = y * (double)y;

        double t0 = 2.0 * (w * (double)x + y * (double)z);
        double t1 = 1.0 - 2.0 * (x * (double)x + ysqr);
        double ex = Math.Atan2(t0, t1);

        double t2 = 2.0 * (w * (double)y - z * (double)x);
        t2 = Math.Clamp(t2, -1.0, 1.0);
        double ey = Math.Asin(t2);

        double t3 = 2.0 * (w * (double)z + x * (double)y);
        double t4 = 1.0 - 2.0 * (ysqr + z * (double)z);
        double ez = Math.Atan2(t3, t4);

        return (Mat3.Degrees(ex), Mat3.Degrees(ey), Mat3.Degrees(ez));
    }
}
