namespace TwMapDecompiler.Core.Terry;

/// <summary>
/// Text edits for giving a cloned campaign its own lighting materials.
///
/// An .environment's post_processes is a ';'-separated list of overlay materials - on the
/// combi map materials/environment/snow/combi_campaign_snow.xml.material and a corruption
/// material. The snow one carries t_xml_snow_mask, the map's snow mask texture, so a clone
/// needs its own copy; the corruption one is left alone.
/// </summary>
public static class CampaignClone
{
    public static bool IsSnowMaterial(string entry) =>
        Path.GetFileName(entry.Replace('\\', '/')).Contains("snow", StringComparison.OrdinalIgnoreCase);

    private static string[] Entries(string postProcesses) =>
        postProcesses.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The snow material in a post_processes list, or null.</summary>
    public static string? FindSnowMaterial(string postProcesses) => Entries(postProcesses).FirstOrDefault(IsSnowMaterial);

    /// <summary>post_processes with every snow material swapped for <paramref name="snowMaterial"/>.</summary>
    public static string ReplaceSnowMaterial(string postProcesses, string snowMaterial) =>
        string.Join(';', Entries(postProcesses).Select(e => IsSnowMaterial(e) ? snowMaterial : e));

    /// <summary>
    /// Copies an .environment to <paramref name="targetPath"/> with its snow material swapped;
    /// returns the snow material it had (null when it had none, in which case it is copied as is).
    /// </summary>
    public static string? CloneEnvironment(string sourcePath, string targetPath, string snowMaterial)
    {
        string text = File.ReadAllText(sourcePath);
        string? postProcesses = EnvironmentWriter.Attribute(text, "post_processes");
        string? oldSnow = postProcesses == null ? null : FindSnowMaterial(postProcesses);
        if (oldSnow != null)
            text = EnvironmentWriter.ReplaceAttribute(text, "post_processes", ReplaceSnowMaterial(postProcesses!, snowMaterial))!;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, text);
        return oldSnow;
    }
}
