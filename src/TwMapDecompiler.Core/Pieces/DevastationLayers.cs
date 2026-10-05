using TwMapDecompiler.Core.Terry;

namespace TwMapDecompiler.Core.Pieces;

/// <summary>
/// The devastated project's terrain maps as layers over the main project's: the main project's
/// layers of a map are hard-linked in unchanged, and one "devastation" layer on top holds what the
/// devastate pieces change. Each builder returns that layer, or null when the map type's blend rule
/// cannot express the change (the step then writes the whole devastated map instead, see
/// <see cref="Stack"/>). The rules are Terry's per-type compositing (qttoolutility
/// TerrainMapLayer::composite):
///
/// - heights add (<see cref="Delta"/>),
/// - blend maps and the event area mask replace wherever the layer is not 255 (<see cref="Keyed"/>),
/// - colour overlays are alpha-over (<see cref="AlphaOver"/>),
/// - corruption and snow masks combine as dst + src * (1 - dst), which can only raise a value
///   (<see cref="Union"/>).
///
/// Trees have no builder: 255, "no tree", is a tree layer's transparent value, so a layer cannot
/// take away a tree the main project has, and the devastated tree map is always written whole.
///
/// Each builder compares the vanilla compiled map with the devastated one, so a layer holds the
/// devastation itself and not the decompile: edits to the main project show through it.
/// </summary>
public static class DevastationLayers
{
    /// <summary>
    /// A devastated map's layer list: the main project's layers (hard-linked) with the devastation
    /// layer on top. For a map written whole, the main project's layers become hidden "reference"
    /// layers: Terry and BOB skip a layer with visible="0", so they only keep the undevastated map
    /// at hand.
    /// </summary>
    public static List<TerryProjectWriter.TerrainLayerRef> Stack(IReadOnlyList<TerryProjectWriter.TerrainLayerRef> main,
        DevastationMapMode mode, string? layerId) => mode switch
    {
        DevastationMapMode.Base => main.ToList(),
        DevastationMapMode.Overlay => [.. main, new TerryProjectWriter.TerrainLayerRef(layerId!, "devastation")],
        DevastationMapMode.Own =>
        [
            .. main.Select(l => l with { Name = main.Count == 1 ? "reference" : $"reference {l.Name}", Visible = "0" }),
            new TerryProjectWriter.TerrainLayerRef(layerId!, "devastation"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>devastated - vanilla, written into <paramref name="devastated"/>; returns the number of texels that differ.</summary>
    public static long Delta(float[] vanilla, float[] devastated)
    {
        long changed = 0;
        for (long i = 0; i < devastated.Length; i++)
        {
            float d = devastated[i] - vanilla[i];
            devastated[i] = d;
            if (d != 0)
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// A replace-where-not-<paramref name="transparent"/> layer: the devastated value where it
    /// differs, transparent elsewhere. Null when a changed pixel's new value is the transparent one.
    /// </summary>
    public static byte[]? Keyed(byte[] vanilla, byte[] devastated, byte transparent, out long changed)
    {
        changed = 0;
        var layer = new byte[devastated.Length];
        for (long i = 0; i < layer.Length; i++)
        {
            if (devastated[i] == vanilla[i])
            {
                layer[i] = transparent;
                continue;
            }
            if (devastated[i] == transparent)
                return null;
            layer[i] = devastated[i];
            changed++;
        }
        return layer;
    }

    /// <summary>
    /// A union layer (dst + src * (1 - dst)). It can only be exact over 0, so it is null unless every
    /// changed pixel was 0 in the vanilla map. (On combi the devastation lowers both masks nearly
    /// everywhere it touches them, so these are written whole.)
    /// </summary>
    public static byte[]? Union(byte[] vanilla, byte[] devastated, out long changed)
    {
        changed = 0;
        var layer = new byte[devastated.Length];
        for (long i = 0; i < layer.Length; i++)
        {
            if (devastated[i] == vanilla[i])
                continue;
            if (vanilla[i] != 0)
                return null;
            layer[i] = devastated[i];
            changed++;
        }
        return layer;
    }

    /// <summary>
    /// An alpha-over RGBA layer: the devastated pixel, opaque, where it differs; fully transparent
    /// elsewhere. Null when a changed pixel is not opaque (the compiled overlays are BC1, all opaque).
    /// </summary>
    public static byte[]? AlphaOver(byte[] vanillaRgba, byte[] devastatedRgba, out long changed)
    {
        changed = 0;
        var layer = new byte[devastatedRgba.Length];
        for (long i = 0; i < layer.Length; i += 4)
        {
            if (devastatedRgba.AsSpan((int)i, 4).SequenceEqual(vanillaRgba.AsSpan((int)i, 4)))
                continue;
            if (devastatedRgba[i + 3] != 255)
                return null;
            Array.Copy(devastatedRgba, i, layer, i, 4);
            changed++;
        }
        return layer;
    }
}

/// <summary>How a devastated terrain map is made up.</summary>
public enum DevastationMapMode
{
    /// <summary>The main project's layers, hard-linked, and nothing else: the pieces leave the map unchanged.</summary>
    Base,

    /// <summary>The main project's layers, hard-linked, with the devastation layer on top.</summary>
    Overlay,

    /// <summary>
    /// The whole devastated map as the devastation layer, over the main project's layers, which are
    /// hard-linked as hidden "reference" layers: kept at hand, left out of the compositing.
    /// </summary>
    Own,
}
