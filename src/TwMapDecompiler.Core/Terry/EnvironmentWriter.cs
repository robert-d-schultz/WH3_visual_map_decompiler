using System.Text.RegularExpressions;

namespace TwMapDecompiler.Core.Terry;

/// <summary>
/// Builds the Weather/.environment file the campaign Terry project points at.
///
/// CA ships one per map next to the templates (assembly_kit_tow has
/// default_battle_oldworld.environment, assembly_kit_iee has default_battle_iee.environment).
/// This makes the same thing: a copy of working_data/Weather/terry_default_battle.environment
/// with its empty post_processes attribute filled in from the compiled map's own lighting -
/// the snow and corruption overlay materials, which is what makes those effects show up in Terry.
///
/// The value is found by following the map's environment_collection.xml global_lighting
/// attribute to an .environment file and copying its post_processes. That attribute is a
/// pack path which has moved between game updates (terrain/campaigns/&lt;map&gt;/lighting/
/// default.environment, later Weather/campaign/combi/default.environment), so it is
/// resolved against the packs rather than assumed to sit in the campaign folder.
/// </summary>
public static class EnvironmentWriter
{
    /// <summary>The global_lighting pack path from the campaign folder's environment_collection.xml, or null.</summary>
    /// <param name="campaignFolder">Compiled terrain/campaigns/&lt;map&gt; folder.</param>
    public static string? ReadGlobalLighting(string campaignFolder)
    {
        string collectionPath = Path.Combine(campaignFolder, "environment_collection.xml");
        if (!File.Exists(collectionPath))
            return null;
        return Attribute(File.ReadAllText(collectionPath), "global_lighting")?.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Local copy of the global_lighting file when the campaign folder is all there is
    /// (an explicit input folder, no packs): only paths inside the map folder resolve.
    /// </summary>
    public static string? FindInCampaignFolder(string campaignFolder, string lighting)
    {
        int index = lighting.IndexOf("/lighting/", StringComparison.OrdinalIgnoreCase);
        string localPath = index >= 0
            ? Path.Combine(campaignFolder, lighting[(index + 1)..].Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(campaignFolder, Path.GetFileName(lighting));
        return File.Exists(localPath) ? localPath : null;
    }

    /// <summary>post_processes of the .environment at <paramref name="lightingPath"/>.</summary>
    public static string? ReadPostProcesses(string lightingPath, Action<string> log)
    {
        string? postProcesses = Attribute(File.ReadAllText(lightingPath), "post_processes");
        log($"post_processes: {(string.IsNullOrEmpty(postProcesses) ? "(empty)" : postProcesses)}");
        return postProcesses;
    }

    /// <summary>
    /// Writes <paramref name="targetPath"/> as the template with post_processes replaced.
    /// </summary>
    public static void Write(string templatePath, string targetPath, string postProcesses)
    {
        string output = ReplaceAttribute(File.ReadAllText(templatePath), "post_processes", postProcesses)
            ?? throw new InvalidDataException($"{Path.GetFileName(templatePath)} has no post_processes attribute to fill in.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, output);
    }

    /// <summary>Reads an XML attribute from the first element that carries it (single or double quoted).</summary>
    public static string? Attribute(string xml, string name)
    {
        var match = AttributeRegex(name).Match(xml);
        return match.Success ? match.Groups[2].Value : null;
    }

    /// <summary>
    /// <paramref name="xml"/> with the first <paramref name="name"/> attribute set to <paramref name="value"/>,
    /// the rest of the text untouched; null when no element carries it.
    /// </summary>
    public static string? ReplaceAttribute(string xml, string name, string value)
    {
        var match = AttributeRegex(name).Match(xml);
        if (!match.Success)
            return null;
        char quote = match.Groups[1].Value[0];
        string escaped = quote == '"' ? value.Replace("\"", "&quot;") : value.Replace("'", "&apos;");
        return xml[..match.Index] + $"{name}={quote}{escaped}{quote}" + xml[(match.Index + match.Length)..];
    }

    // The leading \b keeps e.g. "snow_mask" from matching inside "t_xml_snow_mask".
    private static Regex AttributeRegex(string name) => new("\\b" + Regex.Escape(name) + "\\s*=\\s*([\"'])(.*?)\\1");
}
