namespace TwMapDecompiler.Core.Terry;

/// <summary>
/// The size of Terry's PatchVisibilityMask: one pixel per patch of the campaign terrain's
/// control-point grid. Terry sizes the map from the tile map through warscape's
/// TERRAIN_DIMENSIONS::campaign_control_points (warscape.modder.x64.dll), and with a
/// PatchVisibilityMask of any other size in the .terry it shows only the tiles in the global
/// mesh (roads, cliffs, coast), not the rest of the tile map.
/// </summary>
public static class PatchVisibilityGrid
{
    /// <summary>
    /// The grid for a tile map of <paramref name="tileWidth"/> x <paramref name="tileHeight"/> tiles:
    /// 125x84 for combi's 2880x1941, 128x77 for 3200x1941, 123x80 for chaos's 2216x1445.
    /// Single precision throughout, as warscape computes it.
    /// </summary>
    public static (int Width, int Height) Size(int tileWidth, int tileHeight)
    {
        float width = tileWidth, height = tileHeight;
        // About 128 patches across; the rows keep the map's aspect, rounded half away from zero.
        float rows = (int)(height / width * 128f + 0.5f);
        float across = MathF.Ceiling(width * (1f / 128f));
        float down = MathF.Ceiling(height / rows);
        return ((int)(width / (across * 128f) * 128f), (int)(height / (down * rows) * rows));
    }
}
