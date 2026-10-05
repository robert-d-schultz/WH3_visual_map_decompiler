using System.Text;
using TwMapDecompiler.Core.Terry;
using static TwMapDecompiler.Core.Terry.TerryFormat;

namespace TwMapDecompiler.Core.Bmd;

/// <summary>
/// Writes one Terry .layer XML file per layer (a province, an unmapped region or the
/// light probes), one entity per BMD object. The XML shape matches what Terry itself
/// saves (layer version 41).
///
/// Entities that belong together are additionally collected into logical layers
/// (an ECLayer entity plus Logical associations), so that what Terry would otherwise
/// scatter through the layer can be toggled as a group: everything carrying a culture
/// mask, the sound emitters, and in the catch-all layer the light probes, the sea water
/// planes and the full-shroud meshes. Membership is exclusive - a sea water plane sits in
/// the water plane layer, not in both. The catch-all layer's own cosmetic water planes match
/// none of these groups and stay loose in the layer. Sound emitters go into a sound layer
/// nested in the layer they would otherwise join: the culture-masked ones in the culture mask
/// layer's, those of a BMD export type (devastation_chaos, ...) in that type's, and the rest in
/// a top-level sound layer. Every sound layer has an empty bmd_export_type: BOB names a BMD after
/// its export type and appends "_sound" for the sound emitters by itself, so a "sound" type would
/// compile them into bmd_objects_sound_sound.bin / global_props_sound_sound.bin, which the game
/// never loads.
/// </summary>
public sealed class TerryLayerWriter(TerryLayerWriter.Options options, TerryIdGenerator ids)
{
    public sealed record Options
    {
        /// <summary>Name of the new (output) map; used in file names and river model paths.</summary>
        public required string MapName { get; init; }

        /// <summary>Terrain folder name of the source map (river models reference it).</summary>
        public required string VanillaMapName { get; init; }

        /// <summary>Campaign name of the new map (water plane material paths).</summary>
        public required string CampaignName { get; init; }

        /// <summary>Campaign name of the source map.</summary>
        public required string VanillaCampaignName { get; init; }

        /// <summary>The culture mask's bits (prefab_types_tables), for the culture_mask attributes.</summary>
        public required CultureMask Cultures { get; init; }

        /// <summary>Material every sea water plane is given (a cloned campaign's own copy); null keeps theirs.</summary>
        public string? SeaWaterPlaneMaterial { get; init; }

        /// <summary>
        /// Map scale (see <see cref="Pipeline.MapResizer"/>): every position is multiplied by it about the
        /// world's origin, and so is every size - a prop's scale, a light's radius or length, an outline.
        /// </summary>
        public double Scale { get; init; } = 1;

        /// <summary>Added to every entity's position after scaling: where map padding moves the world's origin (see <see cref="Pipeline.MapResizer"/>).</summary>
        public double OffsetX { get; init; }

        /// <inheritdoc cref="OffsetX"/>
        public double OffsetZ { get; init; }

        /// <summary>Turns river props into Terry river splines; null writes them as props.</summary>
        public Rivers.RiverModels? Rivers { get; init; }
    }

    public sealed record WrittenLayer(string LayerName, string LayerId, string FilePath, int EntityCount);

    /// <summary>Name of the logical layer collecting everything with a culture mask.</summary>
    public const string CultureMaskLayerName = "uses_culture_mask";

    /// <summary>Name of the catch-all layer's logical layer of light probes.</summary>
    public const string LightProbeLayerName = "light_probes";

    /// <summary>Name of the catch-all layer's logical layer of sea water planes.</summary>
    public const string WaterPlaneLayerName = "water_planes";

    /// <summary>Name of the catch-all layer's logical layer of full-shroud border meshes.</summary>
    public const string FullShroudLayerName = "full_shr";

    /// <summary>Name of the logical layers collecting the sound emitters (top level, and nested in the culture mask and export type layers).</summary>
    public const string SoundLayerName = "sound";

    /// <summary>The logical layers of one .layer file, in the order they are written.</summary>
    private sealed class LogicalLayers(bool isGlobal, CultureMask cultures)
    {
        /// <summary>The catch-all layer splits out probes, sea planes and shroud meshes; the others do not.</summary>
        public bool IsGlobal { get; } = isGlobal;

        private readonly List<string> _probes = [];
        private readonly List<string> _waterPlanes = [];
        private readonly List<string> _fullShroud = [];
        private readonly List<string> _sounds = [];
        private readonly List<string> _cultureMasked = [];
        private readonly List<string> _cultureMaskedSounds = [];
        private readonly Dictionary<string, (List<string> Members, List<string> Sounds)> _exports = new(StringComparer.Ordinal);

        /// <summary>
        /// While set, every entity written goes into the logical layer of this BMD export type
        /// instead of any other: its export type is what decides which BMD it compiles into.
        /// </summary>
        public string? CurrentExport { get; set; }

        private bool AddToExport(string entityId, bool isSound = false)
        {
            if (CurrentExport == null)
                return false;
            if (!_exports.TryGetValue(CurrentExport, out var group))
                _exports[CurrentExport] = group = ([], []);
            (isSound ? group.Sounds : group.Members).Add(entityId);
            return true;
        }

        public void AddProbe(string entityId)
        {
            if (!AddToExport(entityId))
                _probes.Add(entityId);
        }

        public void AddWaterPlane(string entityId)
        {
            if (!AddToExport(entityId))
                _waterPlanes.Add(entityId);
        }

        public void AddFullShroud(string entityId)
        {
            if (!AddToExport(entityId))
                _fullShroud.Add(entityId);
        }

        /// <summary>Adds a sound emitter to the sound layer of its export type, the culture mask layer or the layer itself.</summary>
        public void AddSound(string entityId, ulong cultureMask)
        {
            if (AddToExport(entityId, isSound: true))
                return;
            (cultures.IsUnrestricted(cultureMask) ? _sounds : _cultureMaskedSounds).Add(entityId);
        }

        /// <summary>Adds the entity to the culture mask layer, unless its mask is unrestricted.</summary>
        public void AddIfMasked(string entityId, ulong cultureMask)
        {
            if (!AddToExport(entityId) && !cultures.IsUnrestricted(cultureMask))
                _cultureMasked.Add(entityId);
        }

        /// <summary>For an entity that joins no logical layer of its own (a light probe outside the catch-all layer).</summary>
        public void AddLoose(string entityId) => AddToExport(entityId);

        public int CultureMaskedCount => _cultureMasked.Count + _cultureMaskedSounds.Count;

        /// <summary>
        /// The non-empty logical layers: name, the BMD export type Terry shows for it, members, and
        /// the sound emitters that go into a sound layer nested in it.
        /// </summary>
        public IEnumerable<(string Name, string ExportType, List<string> Ids, List<string> Sounds)> NonEmpty =>
            new[]
            {
                (LightProbeLayerName, "", _probes, new List<string>()),
                (WaterPlaneLayerName, "", _waterPlanes, []),
                (FullShroudLayerName, "", _fullShroud, []),
                (SoundLayerName, "", _sounds, []),
                (CultureMaskLayerName, "", _cultureMasked, _cultureMaskedSounds),
            }
            .Concat(_exports.Select(e => (e.Key, e.Key, e.Value.Members, e.Value.Sounds)))
            .Where(g => g.Item3.Count + g.Item4.Count > 0);
    }

    /// <summary>Writes each layer as a .layer file and returns the layer registry for the .terry project.</summary>
    public List<WrittenLayer> WriteLayers(IEnumerable<CompiledBmd> layers, string outputFolder, Action<string>? log = null)
    {
        var written = new List<WrittenLayer>();
        foreach (var layer in layers)
        {
            string id = ids.Next();
            string path = Path.Combine(outputFolder, $"{options.MapName}.{id}.layer");
            int masked = WriteLayerFile(layer, path);
            log?.Invoke($"Wrote {Path.GetFileName(path)} ({layer.LayerName}, {layer.EntityCount} entities" +
                        (masked > 0 ? $", {masked} with a culture mask)" : ")"));
            written.Add(new WrittenLayer(layer.LayerName, id, path, layer.EntityCount));
        }
        return written;
    }

    /// <summary>
    /// Writes one layer whose entities are split by BMD export type: <paramref name="baseObjects"/>
    /// as usual, and each of <paramref name="exportGroups"/> wholly into a logical layer named after
    /// its export type, with that bmd_export_type (devastation_chaos, ...) - the group's sound
    /// emitters in a sound layer nested inside it. An event-area piece is written this way: its
    /// objects.bin plus the objects_devastation_&lt;type&gt;.bin and bmd_objects_sound_devastation_&lt;type&gt;.bin overlays.
    /// </summary>
    public WrittenLayer WriteLayer(CompiledBmd baseObjects, IReadOnlyList<(string ExportType, CompiledBmd Objects)> exportGroups,
        string outputFolder, Action<string>? log = null)
    {
        string id = ids.Next();
        string path = Path.Combine(outputFolder, $"{options.MapName}.{id}.layer");
        int masked = WriteLayerFile(baseObjects, path, exportGroups);
        int total = baseObjects.EntityCount + exportGroups.Sum(g => g.Objects.EntityCount);
        string groups = string.Join(", ", exportGroups.Where(g => g.Objects.EntityCount > 0).Select(g => $"{g.Objects.EntityCount} {g.ExportType}"));
        log?.Invoke($"Wrote {Path.GetFileName(path)} ({baseObjects.LayerName}, {total} entities" +
                    (groups.Length > 0 ? $": {groups}" : "") +
                    (masked > 0 ? $"; {masked} with a culture mask)" : ")"));
        return new WrittenLayer(baseObjects.LayerName, id, path, total);
    }

    /// <summary>Writes one .layer file; returns how many entities went into the culture mask layer.</summary>
    private int WriteLayerFile(CompiledBmd layer, string filePath,
        IReadOnlyList<(string ExportType, CompiledBmd Objects)>? exportGroups = null)
    {
        var sb = new StringBuilder(1 << 20);
        var logical = new LogicalLayers(layer.IsGlobal, options.Cultures);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!-- ").Append(layer.LayerName).Append(" -->\n");
        sb.Append("<layer version=\"41\">\n\t<entities>");

        WriteEntities(sb, layer, logical);
        foreach (var (exportType, objects) in exportGroups ?? [])
        {
            logical.CurrentExport = exportType;
            WriteEntities(sb, objects, logical);
        }
        logical.CurrentExport = null;

        // One ECLayer entity per non-empty logical layer, wired up in the associations below. A nested
        // sound layer follows its parent, whose members include it.
        var groups = new List<(string Name, string ExportType, string Id, List<string> Ids)>();
        foreach (var (name, exportType, members, sounds) in logical.NonEmpty)
        {
            string id = ids.Next();
            if (sounds.Count == 0)
            {
                groups.Add((name, exportType, id, members));
                continue;
            }
            string soundId = ids.Next();
            groups.Add((name, exportType, id, [.. members, soundId]));
            groups.Add((SoundLayerName, "", soundId, sounds));
        }
        foreach (var (name, exportType, id, _) in groups)
        {
            sb.Append($"\n\t\t<entity id=\"{id}\" name=\"{Xml(name)}\">");
            sb.Append($"\n\t\t\t<ECLayer export=\"true\" bmd_export_type=\"{Xml(exportType)}\"/>");
            CloseEntity(sb);
        }

        sb.Append("\n\t</entities>\n\t<associations>\n\t\t<Logical");
        if (groups.Count == 0)
        {
            sb.Append("/>");
        }
        else
        {
            sb.Append(">");
            foreach (var (_, _, id, members) in groups)
            {
                sb.Append($"\n\t\t\t<from id=\"{id}\">");
                foreach (string member in members)
                    sb.Append($"\n\t\t\t\t<to id=\"{member}\"/>");
                sb.Append("\n\t\t\t</from>");
            }
            sb.Append("\n\t\t</Logical>");
        }
        sb.Append("\n\t\t<Transform/>\n\t</associations>\n</layer>");
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
        return logical.CultureMaskedCount;
    }

    private void WriteEntities(StringBuilder sb, CompiledBmd layer, LogicalLayers logical)
    {
        foreach (var prop in layer.Props) WriteProp(sb, prop, logical);
        foreach (var vfx in layer.Vfx) WriteVfx(sb, vfx, logical);
        foreach (var probe in layer.LightProbes) WriteLightProbe(sb, probe, logical);
        foreach (var hole in layer.TerrainHoleTris) WriteTerrainHole(sb, hole, logical);
        foreach (var light in layer.PointLights) WritePointLight(sb, light, logical);
        foreach (var mesh in layer.PolyMeshes) WritePolyMesh(sb, mesh, logical);
        foreach (var light in layer.SpotLights) WriteSpotLight(sb, light, logical);
        foreach (var emitter in layer.SoundEmitters) WriteSoundEmitter(sb, emitter, logical);
        foreach (var scene in layer.CompositeScenes) WriteCompositeScene(sb, scene, logical);
    }

    private string OpenEntity(StringBuilder sb)
    {
        string id = ids.Next();
        sb.Append("\n\t\t<entity id=\"").Append(id).Append("\">");
        return id;
    }

    private static void CloseEntity(StringBuilder sb) => sb.Append("\n\t\t</entity>");

    /// <summary>
    /// Every entity's geometry (polylines, point clouds) is relative to its transform, so moving the
    /// position moves the whole entity. Only meshes, VFX and scenes (<paramref name="scaled"/>) take
    /// the map scale in their transform's scale; the rest have it applied to their own sizes and
    /// geometry, which comes out right whether or not Terry applies the transform's scale to it.
    /// </summary>
    private void WriteTransform(StringBuilder sb, BmdTransform t, bool scaled = false)
    {
        double s = options.Scale;
        double k = scaled ? s : 1;
        sb.Append("\n\t\t\t<ECTransform");
        sb.Append($" position=\"{F5(t.PosX * s + options.OffsetX)} {F5(t.PosY * s)} {F5(t.PosZ * s + options.OffsetZ)}\"");
        sb.Append($" rotation=\"{F5(t.RotX)} {F5(t.RotY)} {F5(t.RotZ)}\"");
        sb.Append($" scale=\"{F5(t.ScaleX * k)} {F5(t.ScaleY * k)} {F5(t.ScaleZ * k)}\"");
        sb.Append(" pivot=\"0 0 0\"/>");
    }

    /// <summary>A length in the world, scaled with the map (still a float, as the compiled map stores it).</summary>
    private float Scaled(float value) => options.Scale == 1 ? value : (float)(value * options.Scale);

    /// <inheritdoc cref="Scaled(float)"/>
    private double Scaled(double value) => value * options.Scale;

    private void WriteProp(StringBuilder sb, BmdProp prop, LogicalLayers logical)
    {
        if (!prop.IsDecal && options.Rivers?.Find(prop.Path) is { } river)
        {
            WriteRiver(sb, prop, river, logical);
            return;
        }

        // A river model that could not be read back into a spline stays a prop. The compiled map
        // references it in the source map's terrain folder; point it at the new map's folder instead.
        if (prop.Path.Contains(options.VanillaMapName) && prop.Path.Contains("river"))
            prop.Path = prop.Path.Replace(options.VanillaMapName, options.MapName);

        logical.AddIfMasked(OpenEntity(sb), prop.CultureMask);

        if (prop.IsDecal)
        {
            sb.Append($"\n\t\t\t<ECDecal model_path=\"{Xml(prop.Path)}\" parallax_scale=\"0\" tiling=\"0\" tiling_alpha_enabled=\"{B(prop.DecalTilingAlpha)}\" normal_mode=\"DNM_BLEND\"");
            sb.Append($" apply_to_terrain=\"{B(prop.ApplyToTerrain)}\" apply_to_objects=\"{B(prop.ApplyToObjects)}\"" +
                      $" fade_start=\"{R(prop.DecalFadeStart)}\" mirror_x=\"{B(prop.DecalMirrorX)}\" mirror_z=\"{B(prop.DecalMirrorZ)}\"" +
                      $" angle_threshold=\"{R(prop.DecalAngleThreshold)}\" layer=\"{prop.DecalLayer}\"/>");
        }
        else
        {
            sb.Append("\n\t\t\t<ECPropMesh/>");
            sb.Append($"\n\t\t\t<ECMesh model_path=\"{Xml(prop.Path)}\" opacity=\"1\"/>");
            sb.Append($"\n\t\t\t<ECMeshRenderSettings cast_shadow=\"{B(prop.CastsShadow)}\" receive_decals=\"{B(prop.ApplyToObjects)}\"" +
                      $" render_into_skydome_fog=\"{B(prop.RenderIntoSkydomeFog)}\" blend_edges_with_terrain=\"{R(prop.BlendEdgesWithTerrain)}\"/>");
        }

        sb.Append($"\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"{B(prop.VisibleInTactical)}\" visible_in_tactical_view_only=\"{B(prop.OnlyVisibleInTactical)}\"/>");
        sb.Append($"\n\t\t\t<ECPropHeightPatch apply_height_patch=\"{B(prop.ApplyHeightPatch)}\" for_camera_height_map_only=\"false\"/>");
        sb.Append($"\n\t\t\t<ECCampaignProperties visible_inside_snow_region=\"{B(prop.VisibleInsideSnowRegion)}\" visible_outside_snow_region=\"{B(prop.VisibleOutsideSnowRegion)}\"" +
                  $" visible_inside_destruction_region=\"{B(prop.VisibleInsideDestructionRegion)}\" visible_outside_destruction_region=\"{B(prop.VisibleOutsideDestructionRegion)}\"" +
                  $" visible_in_shroud=\"{B(prop.VisibleInShroud)}\" visible_in_shroud_only=\"{B(prop.VisibleInShroudOnly)}\" no_culling=\"{B(prop.NoCulling)}\"" +
                  $" culture_mask=\"{options.Cultures.Format(prop.CultureMask)}\" season_mask=\"\"/>");

        WriteTransform(sb, prop.Transform, scaled: true);
        CloseEntity(sb);
    }

    /// <summary>
    /// A river prop as the Terry river it was baked from (see <see cref="Rivers.RiverModels"/>): the prop's
    /// transform is the river entity's, and the spline is in its frame. Point order follows the baked
    /// mesh's flow, so reverse_direction is always false. Nothing of flow_speed, alpha_fade, foam_amount
    /// or terrain_offset survives the bake, so they get the values Terry gives a new point.
    /// </summary>
    private void WriteRiver(StringBuilder sb, BmdProp prop, Rivers.RiverSpline river, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), prop.CultureMask);
        sb.Append("\n\t\t\t<ECRiver/>");
        sb.Append($"\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"{B(prop.VisibleInTactical)}\" visible_in_tactical_view_only=\"{B(prop.OnlyVisibleInTactical)}\"/>");
        WriteTransform(sb, prop.Transform);
        sb.Append($"\n\t\t\t<ECRiverSpline terrain_relative=\"false\" reverse_direction=\"false\" material=\"{Xml(RetargetWaterMaterial(river.Material))}\">");
        sb.Append("\n\t\t\t\t<spline closed=\"false\">");
        foreach (var p in river.Points)
        {
            sb.Append($"\n\t\t\t\t\t<point position=\"{Vector(p.Position)}\" tangent_in=\"{Vector(p.TangentIn)}\" tangent_out=\"{Vector(p.TangentOut)}\"" +
                      $" width=\"{F6(Scaled(p.Width))}\" terrain_offset=\"0\" alpha_fade=\"1\" flow_speed=\"0.5\" foam_amount=\"0.100000001\"/>");
        }
        sb.Append("\n\t\t\t\t</spline>");
        sb.Append("\n\t\t\t</ECRiverSpline>");
        sb.Append($"\n\t\t\t<ECCampaignProperties visible_inside_snow_region=\"{B(prop.VisibleInsideSnowRegion)}\" visible_outside_snow_region=\"{B(prop.VisibleOutsideSnowRegion)}\"" +
                  $" visible_inside_destruction_region=\"{B(prop.VisibleInsideDestructionRegion)}\" visible_outside_destruction_region=\"{B(prop.VisibleOutsideDestructionRegion)}\"" +
                  $" visible_in_shroud=\"{B(prop.VisibleInShroud)}\" visible_in_shroud_only=\"{B(prop.VisibleInShroudOnly)}\" no_culling=\"{B(prop.NoCulling)}\"" +
                  $" culture_mask=\"{options.Cultures.Format(prop.CultureMask)}\" season_mask=\"\"/>");
        CloseEntity(sb);

        // Terry writes spline vectors comma-separated. The fit is good to a centimetre, so 6 decimals lose nothing.
        string Vector(Maths.Vec3 v) => $"{F6(Scaled(v.X))},{F6(Scaled(v.Y))},{F6(Scaled(v.Z))}";
    }

    /// <summary>Water plane materials belong to the campaign, not the map: point them at the new campaign's.</summary>
    private string RetargetWaterMaterial(string material)
    {
        if (material.Contains($"{options.VanillaCampaignName}_campaign_blood_river.xml.material"))
            return $"materials/environment/campaign_sea/{options.CampaignName}_campaign_blood_river.xml.material";
        if (material.Contains($"{options.VanillaCampaignName}_campaign_water_plane.xml.material"))
            return $"materials/environment/campaign_sea/{options.CampaignName}_campaign_water_plane.xml.material";
        return material;
    }

    private void WriteVfx(StringBuilder sb, BmdVfx vfx, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), vfx.CultureMask);
        sb.Append("\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"false\" visible_in_tactical_view_only=\"false\"/>");
        sb.Append($"\n\t\t\t<ECVFX vfx=\"{Xml(vfx.Name)}\" autoplay=\"true\" scale=\"1\" instance_name=\"\"/>");
        sb.Append("\n\t\t\t<ECCampaignProperties visible_inside_snow_region=\"true\" visible_outside_snow_region=\"true\"" +
                  " visible_inside_destruction_region=\"true\" visible_outside_destruction_region=\"true\"" +
                  $" visible_in_shroud=\"{B(vfx.VisibleInShroud)}\" visible_in_shroud_only=\"{B(vfx.VisibleInShroudOnly)}\" no_culling=\"{B(vfx.NoCulling)}\"" +
                  $" culture_mask=\"{options.Cultures.Format(vfx.CultureMask)}\" season_mask=\"\"/>");
        WriteTransform(sb, vfx.Transform, scaled: true);
        CloseEntity(sb);
    }

    private void WriteLightProbe(StringBuilder sb, BmdLightProbe probe, LogicalLayers logical)
    {
        string id = OpenEntity(sb);
        if (logical.IsGlobal)
            logical.AddProbe(id);
        else
            logical.AddLoose(id);
        sb.Append($"\n\t\t\t<ECLightProbe primary=\"{B(probe.IsPrimary)}\"/>");
        WriteTransform(sb, probe.Transform);
        sb.Append($"\n\t\t\t<ECDoubleSphere inner_radius=\"{R(Scaled(probe.InnerRadius))}\" outer_radius=\"{R(Scaled(probe.OuterRadius))}\"/>");
        CloseEntity(sb);
    }

    private void WriteTerrainHole(StringBuilder sb, BmdTerrainHoleTri hole, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), hole.CultureMask);
        sb.Append("\n\t\t\t<ECTerrainHole/>");
        WriteTransform(sb, hole.Transform);
        sb.Append("\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"false\" visible_in_tactical_view_only=\"false\"/>");
        sb.Append($"\n\t\t\t<ECCampaignProperties visible_in_shroud=\"false\" no_culling=\"false\" culture_mask=\"{options.Cultures.Format(hole.CultureMask)}\"/>");

        sb.Append("\n\t\t\t<ECPolyline>");
        sb.Append("\n\t\t\t\t<polyline closed=\"true\">");
        sb.Append($"\n\t\t\t\t\t<point x=\"{F5(0)}\" y=\"{F5(0)}\" />");
        sb.Append($"\n\t\t\t\t\t<point x=\"{F5(Scaled(hole.Vertex2.PosX))}\" y=\"{F5(Scaled(hole.Vertex2.PosZ))}\" />");
        sb.Append($"\n\t\t\t\t\t<point x=\"{F5(Scaled(hole.Vertex3.PosX))}\" y=\"{F5(Scaled(hole.Vertex3.PosZ))}\" />");
        sb.Append("\n\t\t\t\t</polyline>");
        sb.Append("\n\t\t\t</ECPolyline>");
        CloseEntity(sb);
    }

    private void WritePointLight(StringBuilder sb, BmdPointLight light, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), light.CultureMask);
        sb.Append($"\n\t\t\t<ECPointLight colour=\"{(int)(light.R * 255)} {(int)(light.G * 255)} {(int)(light.B * 255)} 255\"" +
                  $" colour_scale=\"{R(light.ColorScale)}\" radius=\"{R(Scaled(light.Radius))}\" animation_type=\"{light.AnimType}\"" +
                  $" animation_speed_scale=\"{R(light.AnimScale1)} {R(light.AnimScale2)}\" colour_min=\"{R(light.ColorMin)}\"" +
                  $" random_offset=\"{R(light.RandOffset)}\" falloff_type=\"{Xml(light.FalloffType)}\" for_light_probes_only=\"{B(light.LightProbesOnly)}\"/>");
        sb.Append("\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"false\" visible_in_tactical_view_only=\"false\"/>");
        sb.Append($"\n\t\t\t<ECCampaignProperties culture_mask=\"{options.Cultures.Format(light.CultureMask)}\"/>");
        WriteTransform(sb, light.Transform);
        CloseEntity(sb);
    }

    private void WritePolyMesh(StringBuilder sb, BmdPolyMesh mesh, LogicalLayers logical)
    {
        string material = RetargetWaterMaterial(mesh.Material);
        if (options.SeaWaterPlaneMaterial != null && mesh.IsSeaWaterPlane)
            material = options.SeaWaterPlaneMaterial;

        // The catch-all layer groups the meshes that make up the map itself; its cosmetic
        // water - the ponds and waterfalls of the sea and lake regions it absorbed - is not
        // part of the sea and stays loose, as it would in a province layer.
        string id = OpenEntity(sb);
        if (logical.IsGlobal && mesh.IsSeaWaterPlane)
            logical.AddWaterPlane(id);
        else if (logical.IsGlobal && mesh.IsFullShroud)
            logical.AddFullShroud(id);
        else
            logical.AddIfMasked(id, mesh.CultureMask);
        sb.Append($"\n\t\t\t<ECPolygonMesh material=\"{Xml(material)}\" affects_mesh_optimization=\"false\"/>");
        sb.Append($"\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"{B(mesh.VisibleInTactical)}\" visible_in_tactical_view_only=\"{B(mesh.OnlyVisibleInTactical)}\"/>");
        sb.Append($"\n\t\t\t<ECCampaignProperties visible_in_shroud=\"{B(mesh.VisibleInShroud)}\" no_culling=\"true\" culture_mask=\"{options.Cultures.Format(mesh.CultureMask)}\"/>");
        WriteTransform(sb, mesh.Transform);

        sb.Append("\n\t\t\t<ECPolyline>");
        sb.Append("\n\t\t\t\t<polyline closed=\"true\">");
        foreach (var v in mesh.Vertices)
            sb.Append($"\n\t\t\t\t\t<point x=\"{R(Scaled(v.X))}\" y=\"{R(Scaled(v.Z))}\"/>");
        sb.Append("\n\t\t\t\t</polyline>");
        sb.Append("\n\t\t\t</ECPolyline>");
        CloseEntity(sb);
    }

    private void WriteSpotLight(StringBuilder sb, BmdSpotLight light, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), light.CultureMask);
        sb.Append($"\n\t\t\t<ECSpotLight colour=\"{light.R} {light.G} {light.B} 255\" intensity=\"{R(light.Intensity)}\" length=\"{R(Scaled(light.Length))}\"" +
                  $" inner_angle=\"{R(Maths.Mat3.Degrees(light.InnerAngle))}\" outer_angle=\"{R(Maths.Mat3.Degrees(light.OuterAngle))}\"" +
                  $" falloff=\"{R(light.Falloff)}\" volumetric=\"{B(light.Volumetric)}\" gobo=\"{Xml(light.Gobo)}\"/>");
        sb.Append("\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"false\" visible_in_tactical_view_only=\"false\"/>");
        sb.Append($"\n\t\t\t<ECCampaignProperties culture_mask=\"{options.Cultures.Format(light.CultureMask)}\"/>");
        WriteTransform(sb, light.Transform);
        CloseEntity(sb);
    }

    private void WriteSoundEmitter(StringBuilder sb, BmdSoundEmitter emitter, LogicalLayers logical)
    {
        logical.AddSound(OpenEntity(sb), emitter.CultureMask);
        sb.Append($"\n\t\t\t<ECSoundMarker key=\"{Xml(emitter.SoundName)}\" />");
        WriteTransform(sb, emitter.Transform);
        sb.Append($"\n\t\t\t<ECCampaignProperties culture_mask=\"{options.Cultures.Format(emitter.CultureMask)}\"/>");

        var first = emitter.Coords[0];
        switch (emitter.SstType)
        {
            case "SST_LINE_LIST":
                sb.Append("\n\t\t\t<ECPolyline3D>");
                sb.Append("\n\t\t\t\t<polyline3d closed=\"false\">");
                foreach (var c in emitter.Coords)
                    sb.Append($"\n\t\t\t\t\t<point x=\"{R(Scaled(c.X - (double)first.X))}\" y=\"{R(Scaled(c.Y - (double)first.Y))}\" z=\"{R(Scaled(c.Z - (double)first.Z))}\"/>");
                sb.Append("\n\t\t\t\t</polyline3d>");
                sb.Append("\n\t\t\t</ECPolyline3D>");
                break;

            case "SST_MULTI_POINT":
                sb.Append("\n\t\t\t<ECPointCloud>");
                sb.Append("\n\t\t\t\t<point_cloud>");
                foreach (var c in emitter.Coords)
                    sb.Append($"\n\t\t\t\t\t<point x=\"{R(Scaled(c.X - (double)first.X))}\" y=\"{R(Scaled(c.Y - (double)first.Y))}\" z=\"{R(Scaled(c.Z - (double)first.Z))}\"/>");
                sb.Append("\n\t\t\t\t</point_cloud>");
                sb.Append("\n\t\t\t</ECPointCloud>");
                break;

            case "SST_SPHERE":
                sb.Append($"\n\t\t\t<ECSphere radius=\"{R(Scaled(emitter.Radius))}\"/>");
                break;
        }
        CloseEntity(sb);
    }

    private void WriteCompositeScene(StringBuilder sb, BmdCompositeScene scene, LogicalLayers logical)
    {
        logical.AddIfMasked(OpenEntity(sb), scene.CultureMask);
        sb.Append($"\n\t\t\t<ECCompositeScene path=\"{Xml(scene.Name)}\" script_id=\"\" autoplay=\"true\"/>");
        sb.Append("\n\t\t\t<ECVisibilitySettingsCampaign visible_in_tactical_view=\"false\" visible_in_tactical_view_only=\"false\"/>");
        WriteTransform(sb, scene.Transform, scaled: true);
        sb.Append($"\n\t\t\t<ECCampaignProperties visible_in_shroud=\"{B(scene.VisibleInShroud)}\" visible_in_shroud_only=\"{B(scene.VisibleInShroudOnly)}\" no_culling=\"{B(scene.NoCulling)}\" culture_mask=\"{options.Cultures.Format(scene.CultureMask)}\"/>");
        CloseEntity(sb);
    }
}
