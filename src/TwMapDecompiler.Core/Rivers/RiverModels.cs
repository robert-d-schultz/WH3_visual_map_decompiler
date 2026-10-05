using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TwMapDecompiler.Core.Rivers;

/// <summary>A river as Terry holds it: its material and spline, in the river entity's frame.</summary>
public sealed record RiverSpline(string Material, IReadOnlyList<RiverPoint> Points);

/// <summary>
/// Turns river props back into Terry river splines. A compiled map has no river entities. BOB
/// bakes each Terry river spline into terrain/campaigns/&lt;map&gt;/models/river_&lt;entity id&gt;.wsmodel
/// and places it as a prop whose transform is the river entity's. So a prop pointing at such a
/// model is a river: its spline is fitted to the baked mesh (<see cref="RiverSplineFitter"/>) and its
/// material is the .wsmodel's.
/// </summary>
public sealed partial class RiverModels(IReadOnlyDictionary<string, string> mapFolders, Action<string> log,
    double tolerance = RiverSplineFitter.DefaultTolerance)
{
    private readonly Dictionary<string, RiverSpline?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>River props written as splines.</summary>
    public int Converted { get; private set; }

    /// <summary>River props whose model could not be read: they stay props pointing at the baked mesh.</summary>
    public int Failed { get; private set; }

    /// <summary>Spline points written, over all converted rivers.</summary>
    public int PointCount { get; private set; }

    [GeneratedRegex(@"^terrain/campaigns/([^/]+)/(models/river_[^/]+\.wsmodel)$", RegexOptions.IgnoreCase)]
    private static partial Regex RiverModelPath();

    /// <summary>
    /// The spline of the river a prop places, or null when the prop is no river model of a map in
    /// <c>mapFolders</c> (map terrain folder name -> local folder) or its model can't be read.
    /// </summary>
    public RiverSpline? Find(string propPath)
    {
        var match = RiverModelPath().Match(propPath.Replace('\\', '/'));
        if (!match.Success || !mapFolders.TryGetValue(match.Groups[1].Value, out string? folder))
            return null;

        if (!_cache.TryGetValue(propPath, out var spline))
            _cache[propPath] = spline = Load(folder, match.Groups[2].Value, propPath);
        if (spline == null)
        {
            Failed++;
            return null;
        }
        Converted++;
        PointCount += spline.Points.Count;
        return spline;
    }

    private RiverSpline? Load(string mapFolder, string relative, string propPath)
    {
        try
        {
            string wsmodel = Path.Combine(mapFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            var model = XDocument.Load(wsmodel).Root!;
            string material = model.Element("materials")?.Elements("material").FirstOrDefault()?.Value.Trim()
                              ?? throw new InvalidDataException("the .wsmodel names no material");
            string geometry = model.Element("geometry")?.Value.Trim().Replace('\\', '/')
                              ?? throw new InvalidDataException("the .wsmodel names no geometry");
            var (centres, widths) = RiverMesh.ReadRows(GeometryFile(wsmodel, geometry));
            return new RiverSpline(material, RiverSplineFitter.Fit(centres, widths, tolerance));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            log($"Warning: river {propPath} stays a prop: {e.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"^terrain/campaigns/([^/]+)/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MapFilePath();

    /// <summary>
    /// The local file of a .wsmodel's geometry. BOB writes it beside the .wsmodel. A hand-copied
    /// .wsmodel can point into another map's folder instead (IEE's copies of the vanilla rivers point
    /// into wh3_main_combi_map_1), so a path into a map of <c>mapFolders</c> is followed there.
    /// </summary>
    private string GeometryFile(string wsmodel, string geometry)
    {
        var match = MapFilePath().Match(geometry);
        if (match.Success && mapFolders.TryGetValue(match.Groups[1].Value, out string? folder))
            return Path.Combine(folder, match.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(Path.GetDirectoryName(wsmodel)!, geometry.Split('/')[^1]);
    }
}
