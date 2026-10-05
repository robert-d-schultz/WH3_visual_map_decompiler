using TwMapDecompiler.Core.Terry;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>The post_processes and material edits a cloned campaign's lighting gets.</summary>
public class CampaignCloneTests
{
    // wh3_main_combi_map_1's default.environment, verbatim.
    private const string CombiPostProcesses =
        "materials/environment/snow/combi_campaign_snow.xml.material;materials/environment/campaign/campaign_combi_corruption.xml.material";

    [Fact]
    public void FindsTheSnowMaterialNotTheCorruption() =>
        Assert.Equal("materials/environment/snow/combi_campaign_snow.xml.material", CampaignClone.FindSnowMaterial(CombiPostProcesses));

    [Fact]
    public void ReplacesOnlyTheSnowMaterial() =>
        Assert.Equal(
            "materials/environment/snow/new_campaign_snow.xml.material;materials/environment/campaign/campaign_combi_corruption.xml.material",
            CampaignClone.ReplaceSnowMaterial(CombiPostProcesses, "materials/environment/snow/new_campaign_snow.xml.material"));

    [Fact]
    public void NoSnowMaterialInCorruptionOnlyList() =>
        Assert.Null(CampaignClone.FindSnowMaterial("materials/environment/campaign/campaign_combi_corruption.xml.material"));

    [Fact]
    public void ReplaceAttributeTouchesOnlyThatAttribute()
    {
        const string xml = "<material snow_mask='x' t_xml_snow_mask='terrain/campaigns/old/snow_mask.dds' other=\"1\"/>";
        Assert.Equal(
            "<material snow_mask='x' t_xml_snow_mask='terrain/campaigns/new_map/snow_mask.dds' other=\"1\"/>",
            EnvironmentWriter.ReplaceAttribute(xml, "t_xml_snow_mask", "terrain/campaigns/new_map/snow_mask.dds"));
        Assert.Equal("x", EnvironmentWriter.Attribute(xml, "snow_mask"));
        Assert.Null(EnvironmentWriter.ReplaceAttribute(xml, "missing", "v"));
    }

    [Fact]
    public void CloneEnvironmentSwapsSnowAndKeepsTheRest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CampaignCloneTests", Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(dir, "in", "default.environment");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, $"<environment_file serialise_version=\"11\" post_processes=\"{CombiPostProcesses}\" time_of_day=\"12.000000\">\n</environment_file>");

            string target = Path.Combine(dir, "out", "lighting", "default.environment");
            string? old = CampaignClone.CloneEnvironment(source, target, "materials/environment/snow/new_campaign_snow.xml.material");

            Assert.Equal("materials/environment/snow/combi_campaign_snow.xml.material", old);
            Assert.Equal(
                "<environment_file serialise_version=\"11\" post_processes=\"materials/environment/snow/new_campaign_snow.xml.material;" +
                "materials/environment/campaign/campaign_combi_corruption.xml.material\" time_of_day=\"12.000000\">\n</environment_file>",
                File.ReadAllText(target));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
