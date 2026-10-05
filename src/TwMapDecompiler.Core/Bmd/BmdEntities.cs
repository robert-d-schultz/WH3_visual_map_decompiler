namespace TwMapDecompiler.Core.Bmd;

public sealed record BmdTransform(
    double PosX, double PosY, double PosZ,
    double RotX = 0, double RotY = 0, double RotZ = 0,
    double ScaleX = 1, double ScaleY = 1, double ScaleZ = 1)
{
    private const double Epsilon = 0.00001;

    public bool AlmostEquals(BmdTransform other) =>
        Math.Abs(PosX - other.PosX) < Epsilon &&
        Math.Abs(PosY - other.PosY) < Epsilon &&
        Math.Abs(PosZ - other.PosZ) < Epsilon &&
        Math.Abs(RotX - other.RotX) < Epsilon &&
        Math.Abs(RotY - other.RotY) < Epsilon &&
        Math.Abs(RotZ - other.RotZ) < Epsilon &&
        Math.Abs(ScaleX - other.ScaleX) < Epsilon &&
        Math.Abs(ScaleY - other.ScaleY) < Epsilon &&
        Math.Abs(ScaleZ - other.ScaleZ) < Epsilon;
}

/// <summary>An entity whose duplicates (differing only by culture mask) can be merged.</summary>
public interface IMaskCombinable<in T>
{
    ulong CultureMask { get; set; }

    /// <summary>Grouping key for duplicate detection (exact-match fields only).</summary>
    string GroupKey { get; }

    /// <summary>Equality as the game sees it; duplicates get their culture masks OR-ed.</summary>
    bool SameAs(T other);
}

public sealed class BmdProp : IMaskCombinable<BmdProp>
{
    public required string Path { get; set; }
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public bool ApplyToTerrain { get; init; }
    public bool ApplyToObjects { get; init; }
    public bool VisibleInTactical { get; init; }
    public bool OnlyVisibleInTactical { get; init; }
    public bool ApplyHeightPatch { get; init; }
    public bool VisibleInShroud { get; init; }
    public bool NoCulling { get; init; }
    public bool IsDecal { get; init; }
    public bool VisibleInShroudOnly { get; init; }
    public bool VisibleInsideSnowRegion { get; init; }
    public bool VisibleOutsideSnowRegion { get; init; }
    public bool VisibleInsideDestructionRegion { get; init; }
    public bool VisibleOutsideDestructionRegion { get; init; }
    public bool CastsShadow { get; init; }
    public bool RenderIntoSkydomeFog { get; init; }
    public float BlendEdgesWithTerrain { get; init; }
    public float DecalFadeStart { get; init; }
    public float DecalAngleThreshold { get; init; }
    public bool DecalMirrorX { get; init; }
    public bool DecalMirrorZ { get; init; }
    public int DecalLayer { get; init; }
    public bool DecalTilingAlpha { get; init; }

    public string GroupKey => Path;
    public bool SameAs(BmdProp other) => Path == other.Path && Transform.AlmostEquals(other.Transform);
}

public sealed class BmdVfx : IMaskCombinable<BmdVfx>
{
    public required string Name { get; init; }
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public bool VisibleInShroud { get; init; }
    public bool VisibleInShroudOnly { get; init; }
    public bool NoCulling { get; init; }

    public string GroupKey => Name;
    public bool SameAs(BmdVfx other) => Name == other.Name && Transform.AlmostEquals(other.Transform);
}

public sealed class BmdLightProbe
{
    public required BmdTransform Transform { get; init; }
    public bool IsPrimary { get; init; }
    public double OuterRadius { get; init; }
    public double InnerRadius { get; init; }
}

public sealed class BmdTerrainHoleTri : IMaskCombinable<BmdTerrainHoleTri>
{
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    /// <summary>Second and third triangle vertices, relative to the transform position (first vertex is 0,0,0).</summary>
    public required BmdTransform Vertex2 { get; init; }
    public required BmdTransform Vertex3 { get; init; }

    public string GroupKey => string.Empty;
    public bool SameAs(BmdTerrainHoleTri other) =>
        Transform.AlmostEquals(other.Transform) && Vertex2.AlmostEquals(other.Vertex2) && Vertex3.AlmostEquals(other.Vertex3);
}

public sealed class BmdPointLight : IMaskCombinable<BmdPointLight>
{
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public float R { get; init; }
    public float G { get; init; }
    public float B { get; init; }
    public required string AnimType { get; init; }
    public float AnimScale1 { get; init; }
    public float AnimScale2 { get; init; }
    public float ColorMin { get; init; }
    public float RandOffset { get; init; }
    public required string FalloffType { get; init; }
    public bool LightProbesOnly { get; init; }
    public float ColorScale { get; init; }
    public float Radius { get; init; }

    public string GroupKey => $"{R:R}|{G:R}|{B:R}";
    public bool SameAs(BmdPointLight other) =>
        R == other.R && G == other.G && B == other.B && Transform.AlmostEquals(other.Transform);
}

public sealed class BmdPolyMesh : IMaskCombinable<BmdPolyMesh>
{
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public required string Material { get; init; }
    public required List<(float X, float Y, float Z)> Vertices { get; init; }
    public bool VisibleInTactical { get; init; }
    public bool OnlyVisibleInTactical { get; init; }
    public bool VisibleInShroud { get; init; }

    public string GroupKey => Material;
    public bool SameAs(BmdPolyMesh other) =>
        Material == other.Material && Transform.AlmostEquals(other.Transform) && Vertices.SequenceEqual(other.Vertices);

    /// <summary>
    /// Outline area (world units squared) from which a water plane counts as sea whatever
    /// its visibility flags say. The two populations are far apart on the combi map: the
    /// cosmetic lake and river planes top out around 290, the sea pieces start at 1470.
    /// </summary>
    public const double SeaWaterPlaneArea = 1000;

    /// <summary>How far off y = 0 a water plane may sit and still count as being at sea level.</summary>
    public const double SeaLevelTolerance = 0.01;

    /// <summary>True for the campaign water plane materials (seas, lakes, cosmetic water).</summary>
    public bool IsWaterPlane => Material.Contains("water_plane", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the full-shroud material that blanks out the map's unreachable border.</summary>
    public bool IsFullShroud => Material.Contains("full_shr.xml.material", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a water plane that makes up the sea itself, as opposed to the cosmetic water
    /// that ornaments a region - a pond, a waterfall pool, the surface of a fountain. The sea
    /// gives itself away by sitting at sea level (y = 0) and by being drawn where the rest of
    /// the map is not: in tactical view and through the shroud. A handful of sea pieces have
    /// those two flags off, so an outright sea-sized outline counts on its own.
    /// </summary>
    public bool IsSeaWaterPlane =>
        IsWaterPlane &&
        Math.Abs(Transform.PosY) <= SeaLevelTolerance &&
        ((VisibleInTactical && VisibleInShroud) || Area >= SeaWaterPlaneArea);

    /// <summary>Outline area in world units squared (shoelace over the XZ polygon Terry stores).</summary>
    public double Area
    {
        get
        {
            double sum = 0;
            for (int i = 0; i < Vertices.Count; i++)
            {
                var a = Vertices[i];
                var b = Vertices[(i + 1) % Vertices.Count];
                sum += a.X * (double)b.Z - b.X * (double)a.Z;
            }
            return Math.Abs(sum) / 2;
        }
    }
}

public sealed class BmdSpotLight : IMaskCombinable<BmdSpotLight>
{
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public int R { get; init; }
    public int G { get; init; }
    public int B { get; init; }
    public float Intensity { get; init; }
    public float Length { get; init; }
    /// <summary>Radians, converted to degrees when written.</summary>
    public float InnerAngle { get; init; }
    public float OuterAngle { get; init; }
    public float Falloff { get; init; }
    public bool Volumetric { get; init; }
    public required string Gobo { get; init; }

    public string GroupKey => $"{R}|{G}|{B}";
    public bool SameAs(BmdSpotLight other) =>
        R == other.R && G == other.G && B == other.B && Transform.AlmostEquals(other.Transform);
}

public sealed class BmdSoundEmitter : IMaskCombinable<BmdSoundEmitter>
{
    public required string SoundName { get; init; }
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public required string SstType { get; init; }
    public required List<(float X, float Y, float Z)> Coords { get; init; }
    public float Radius { get; init; }

    public string GroupKey => SoundName;
    public bool SameAs(BmdSoundEmitter other) => SoundName == other.SoundName && Transform.AlmostEquals(other.Transform);
}

public sealed class BmdCompositeScene : IMaskCombinable<BmdCompositeScene>
{
    public required string Name { get; init; }
    public required BmdTransform Transform { get; init; }
    public ulong CultureMask { get; set; }
    public bool VisibleInShroud { get; init; }
    public bool NoCulling { get; init; }
    public bool VisibleInShroudOnly { get; init; }

    public string GroupKey => Name;
    public bool SameAs(BmdCompositeScene other) => Name == other.Name && Transform.AlmostEquals(other.Transform);
}

/// <summary>All entities of one BMD (or of one output layer after merging).</summary>
public sealed class CompiledBmd(string layerName)
{
    /// <summary>Province or catch-all layer this collection is written to.</summary>
    public string LayerName { get; } = layerName;

    /// <summary>
    /// True for the one catch-all layer: the province-less regions plus the light probes,
    /// sea water planes and full-shroud meshes gathered from everywhere. Its contents are
    /// split into logical layers when written.
    /// </summary>
    public bool IsGlobal { get; init; }
    public List<BmdProp> Props { get; } = [];
    public List<BmdVfx> Vfx { get; } = [];
    public List<BmdLightProbe> LightProbes { get; } = [];
    public List<BmdTerrainHoleTri> TerrainHoleTris { get; } = [];
    public List<BmdPointLight> PointLights { get; } = [];
    public List<BmdPolyMesh> PolyMeshes { get; } = [];
    public List<BmdSpotLight> SpotLights { get; } = [];
    public List<BmdSoundEmitter> SoundEmitters { get; } = [];
    public List<BmdCompositeScene> CompositeScenes { get; } = [];

    public int EntityCount =>
        Props.Count + Vfx.Count + LightProbes.Count + TerrainHoleTris.Count + PointLights.Count +
        PolyMeshes.Count + SpotLights.Count + SoundEmitters.Count + CompositeScenes.Count;

    public void Add(CompiledBmd other)
    {
        Props.AddRange(other.Props);
        Vfx.AddRange(other.Vfx);
        LightProbes.AddRange(other.LightProbes);
        TerrainHoleTris.AddRange(other.TerrainHoleTris);
        PointLights.AddRange(other.PointLights);
        PolyMeshes.AddRange(other.PolyMeshes);
        SpotLights.AddRange(other.SpotLights);
        SoundEmitters.AddRange(other.SoundEmitters);
        CompositeScenes.AddRange(other.CompositeScenes);
    }

    /// <summary>
    /// Merges entities that are identical apart from culture mask (the compiled map
    /// duplicates an entity per culture; Terry wants one entity with a combined mask).
    /// </summary>
    public void CombineDuplicates()
    {
        Combine(Props);
        Combine(Vfx);
        Combine(TerrainHoleTris);
        Combine(PointLights);
        Combine(PolyMeshes);
        Combine(SpotLights);
        Combine(SoundEmitters);
        Combine(CompositeScenes);
        // Light probes carry no culture mask and are never merged.
    }

    private static void Combine<T>(List<T> items) where T : class, IMaskCombinable<T>
    {
        var kept = new List<T>(items.Count);
        var buckets = new Dictionary<string, List<T>>();
        foreach (var item in items)
        {
            if (!buckets.TryGetValue(item.GroupKey, out var bucket))
                buckets[item.GroupKey] = bucket = [];

            var existing = bucket.Find(e => e.SameAs(item));
            if (existing != null)
                existing.CultureMask |= item.CultureMask;
            else
            {
                bucket.Add(item);
                kept.Add(item);
            }
        }
        items.Clear();
        items.AddRange(kept);
    }
}
