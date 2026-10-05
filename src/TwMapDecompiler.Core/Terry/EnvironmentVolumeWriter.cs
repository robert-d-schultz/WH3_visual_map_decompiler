using System.Globalization;
using System.Text;
using System.Xml.Linq;
using static TwMapDecompiler.Core.Terry.TerryFormat;

namespace TwMapDecompiler.Core.Terry;

/// <summary>
/// Reads the compiled environment_collection.xml and writes its volumes out as one Terry
/// .layer file of their own.
///
/// An environment volume swaps the map's lighting over a patch of the map - Skavenblight's
/// green murk, Cathay's haze - by fading from the global environment to its own between an
/// inner and an outer radius. The compiled file stores them as flat SPHERE / CYLINDER
/// elements; Terry wants one entity each, carrying ECEnvironmentVolume plus ECDoubleSphere
/// or ECDoubleCylinder, which is what this writes.
///
/// As in CA's own projects the volumes are collected into logical layers by environment
/// name (badlands, bretonnia, cathay, ...), so one lighting theme can be toggled at a time.
/// </summary>
public static class EnvironmentVolumeWriter
{
    /// <summary>Name of the layer the volumes are written to.</summary>
    public const string LayerName = "_environments";

    /// <summary>
    /// Height of a cylinder volume. The compiled file drops the attribute, so it cannot be
    /// recovered; 0 is what CA's cr_oldworld_map_1 uses for 205 of its 247 cylinders.
    /// </summary>
    public const double CylinderHeight = 0;

    /// <param name="LightingFile">Pack path of the .environment this volume fades to.</param>
    /// <param name="IsSphere">A sphere; otherwise a cylinder, which ignores the y extent.</param>
    public sealed record Volume(
        string LightingFile, double X, double Y, double Z,
        double InnerRadius, double OuterRadius, bool IsSphere)
    {
        /// <summary>The .environment's file name, which is how the volumes are grouped in Terry.</summary>
        public string EnvironmentName => Path.GetFileNameWithoutExtension(LightingFile.Replace('\\', '/'));
    }

    /// <summary>Reads the spheres and cylinders of a compiled environment_collection.xml.</summary>
    public static List<Volume> Read(string collectionPath)
    {
        var doc = XDocument.Load(collectionPath);
        return doc.Descendants("SPHERE").Select(e => Parse(e, isSphere: true))
            .Concat(doc.Descendants("CYLINDER").Select(e => Parse(e, isSphere: false)))
            .ToList();
    }

    /// <param name="Name">e.g. devastation_nagash.</param>
    /// <param name="LightingFile">Pack path of the .environment, e.g. Weather/et_nagash.environment.</param>
    public sealed record Devastation(string Name, string LightingFile);

    /// <summary>
    /// Reads the DEVASTATION elements newer compiled files carry after the volumes: the lighting
    /// a map switches to wholesale in an end-times event (Nagash, the green moon, the apocalypse).
    /// </summary>
    public static List<Devastation> ReadDevastations(string collectionPath) =>
        XDocument.Load(collectionPath).Descendants("DEVASTATION")
            .Select(e => new Devastation((string?)e.Attribute("name") ?? "", (string?)e.Attribute("lighting") ?? ""))
            .ToList();

    private static Volume Parse(XElement element, bool isSphere) =>
        new(
            (string?)element.Attribute("lighting") ?? "",
            Number(element, "x"), Number(element, "y"), Number(element, "z"),
            Number(element, "inner_radius"), Number(element, "outer_radius"),
            isSphere);

    private static double Number(XElement element, string name) =>
        double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;

    /// <summary>
    /// Writes the volumes as a .layer file; returns how many entities it holds.
    /// </summary>
    public static int Write(string filePath, IReadOnlyList<Volume> volumes, TerryIdGenerator ids)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!-- ").Append(LayerName).Append(" -->\n");
        sb.Append("<layer version=\"41\">\n\t<entities>");

        // One logical layer per environment, in the order the volumes appear under each.
        var groups = volumes
            .GroupBy(v => v.EnvironmentName, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Id: ids.Next(), Members: new List<string>()))
            .ToList();
        var memberIds = groups.ToDictionary(g => g.Name, g => g.Members, StringComparer.Ordinal);

        foreach (var volume in volumes)
        {
            string id = ids.Next();
            memberIds[volume.EnvironmentName].Add(id);

            sb.Append($"\n\t\t<entity id=\"{id}\">");
            sb.Append($"\n\t\t\t<ECEnvironmentVolume lighting_file=\"{Xml(volume.LightingFile)}\"/>");
            sb.Append($"\n\t\t\t<ECTransform position=\"{F5(volume.X)} {F5(volume.Y)} {F5(volume.Z)}\"" +
                      " rotation=\"0 0 0\" scale=\"1 1 1\" pivot=\"0 0 0\"/>");
            sb.Append(volume.IsSphere
                ? $"\n\t\t\t<ECDoubleSphere inner_radius=\"{R(volume.InnerRadius)}\" outer_radius=\"{R(volume.OuterRadius)}\"/>"
                : $"\n\t\t\t<ECDoubleCylinder inner_radius=\"{R(volume.InnerRadius)}\" outer_radius=\"{R(volume.OuterRadius)}\" height=\"{R(CylinderHeight)}\"/>");
            sb.Append("\n\t\t</entity>");
        }

        foreach (var (name, id, _) in groups)
        {
            sb.Append($"\n\t\t<entity id=\"{id}\" name=\"{Xml(name)}\">");
            sb.Append("\n\t\t\t<ECLayer export=\"true\" bmd_export_type=\"\"/>");
            sb.Append("\n\t\t</entity>");
        }

        sb.Append("\n\t</entities>\n\t<associations>\n\t\t<Logical");
        if (groups.Count == 0)
        {
            sb.Append("/>");
        }
        else
        {
            sb.Append(">");
            foreach (var (_, id, members) in groups)
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
        return volumes.Count;
    }
}
