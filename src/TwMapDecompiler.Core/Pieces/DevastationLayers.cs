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

    /// <summary>
    /// The land craters over the logic-map heights the main project has (<see cref="Imaging.LogicMapHeights"/>),
    /// written into <paramref name="devastated"/>: devastated BC6H - <paramref name="baseHeights"/> in the texels
    /// whose BC6H blocks the pieces change, 0 elsewhere. CA re-encoded every block of a piece, so most of a
    /// piece differs by BC6H noise alone; the delta is kept where its magnitude, blurred (sigma 2 texels), is
    /// over 0.02 to 0.05 (smoothstep). Measured on wh3_main_combi_map_devastate_1, with vanilla's own BC6H
    /// noise in the same texels as the test: 1% of that noise survives (its large-error tail), the craters
    /// (|delta| over 0.1) move 0.0003 RMS, and 85% of the changed texels keep a delta. Blurring the values too
    /// would flatten chasm walls (0.04 RMS at sigma 1). Returns the number of nonzero texels.
    /// </summary>
    public static long MaskedDelta(float[] vanillaBc6h, float[] devastated, float[] baseHeights, int width, int height)
    {
        long n = (long)width * height;
        var magnitude = new float[n];
        for (long i = 0; i < n; i++)
        {
            float d = devastated[i] == vanillaBc6h[i] ? 0 : devastated[i] - baseHeights[i];
            devastated[i] = d;
            magnitude[i] = Math.Abs(d);
        }
        magnitude = Blur(magnitude, width, height, 2f);
        long changed = 0;
        for (long i = 0; i < n; i++)
        {
            float t = Math.Clamp((magnitude[i] - 0.02f) / 0.03f, 0, 1);
            devastated[i] *= t * t * (3 - 2 * t);
            if (devastated[i] != 0)
                changed++;
        }
        return changed;
    }

    /// <summary>Separable Gaussian, edges clamped.</summary>
    private static float[] Blur(float[] source, int width, int height, float sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[2 * radius + 1];
        for (int i = -radius; i <= radius; i++)
            kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
        float sum = kernel.Sum();
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        var across = new float[source.LongLength];
        var result = new float[source.LongLength];
        Parallel.For(0, height, y =>
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int j = -radius; j <= radius; j++)
                    acc += kernel[j + radius] * source[row + Math.Clamp(x + j, 0, width - 1)];
                across[row + x] = acc;
            }
        });
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int j = -radius; j <= radius; j++)
                    acc += kernel[j + radius] * across[(long)Math.Clamp(y + j, 0, height - 1) * width + x];
                result[(long)y * width + x] = acc;
            }
        });
        return result;
    }

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
