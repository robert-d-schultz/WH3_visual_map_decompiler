using System.Globalization;
using System.Xml.Linq;

namespace TwMapDecompiler.Core.Terry;

/// <summary>
/// Reads the terrain maps of a .terry project: each QTU::TerrainMap's type and size, and its
/// QTU::TerrainMapLayer entries in file order, which is the order Terry composites them in
/// (TerrainMap::data_composited walks the layer list front to back).
/// </summary>
public static class TerryProjectReader
{
    /// <summary>The project's terrain_setup (e.g. terrain/campaigns/wh3_main_combi_map_1/), or null.</summary>
    public static string? ReadTerrainSetup(string terryPath) =>
        (string?)XDocument.Load(terryPath).Root?.Elements("pc")
            .FirstOrDefault(e => (string?)e.Attribute("type") == "QTU::ProjectTileMap")?
            .Element("data")?.Attribute("terrain_setup");

    public static List<TerryProjectWriter.TerrainMapRef> ReadTerrainMaps(string terryPath)
    {
        var maps = new List<TerryProjectWriter.TerrainMapRef>();
        var terrain = XDocument.Load(terryPath).Root?.Elements("pc").FirstOrDefault(e => (string?)e.Attribute("type") == "QTU::Terrain");
        if (terrain == null)
            return maps;

        foreach (var map in terrain.Elements("pc").Where(e => (string?)e.Attribute("type") == "QTU::TerrainMap"))
        {
            var data = map.Element("data");
            string? type = (string?)data?.Attribute("type");
            string? size = (string?)data?.Attribute("size");
            if (type == null || size == null)
                continue;
            var wh = size.Split('x');
            var layers = map.Elements("pc")
                .Where(e => (string?)e.Attribute("type") == "QTU::TerrainMapLayer")
                .Select(e => e.Element("data"))
                .Where(d => d?.Attribute("id") != null)
                .Select(d => new TerryProjectWriter.TerrainLayerRef(
                    (string)d!.Attribute("id")!,
                    (string?)d.Attribute("name") ?? "base",
                    (string?)d.Attribute("visible") ?? "1",
                    (string?)d.Attribute("serializable") ?? "1",
                    (string?)d.Attribute("opacity") ?? "1"))
                .ToList();
            maps.Add(new TerryProjectWriter.TerrainMapRef(type,
                int.Parse(wh[0], CultureInfo.InvariantCulture), int.Parse(wh[1], CultureInfo.InvariantCulture), layers));
        }
        return maps;
    }
}
