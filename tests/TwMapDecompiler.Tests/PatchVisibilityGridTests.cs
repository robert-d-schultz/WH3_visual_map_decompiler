using TwMapDecompiler.Core.Terry;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>PatchVisibilityMask sizes Terry expects, checked against maps it opens and compiled masks.</summary>
public sealed class PatchVisibilityGridTests
{
    [Theory]
    [InlineData(2880, 1941, 125, 84)] // combi: its patch_mask.dds is on the same grid
    [InlineData(3200, 1941, 128, 77)] // combi padded to 3200 wide (cr_combi_expanded_map_1, set by hand in Terry)
    [InlineData(2216, 1445, 123, 80)] // chaos: its terrain_visibility_mask.dds
    public void MatchesTerry(int tileWidth, int tileHeight, int width, int height)
    {
        Assert.Equal((width, height), PatchVisibilityGrid.Size(tileWidth, tileHeight));
    }
}
