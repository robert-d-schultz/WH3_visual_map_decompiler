using System.Numerics;

namespace TwMapDecompiler.Core.Pipeline;

/// <summary>
/// The size of a campaign hex grid, as tile_list.bin's header holds it (1440x970 on combi). A hex
/// is 2x2 tile map pixels, and every terrain map is half a hex taller than its rows (the columns
/// are staggered): tile map 2880x1941, height map 11520x7764. Terry takes at most 2048 hexes each
/// way, and an even number of columns.
/// </summary>
public readonly record struct HexGrid(int Width, int Height)
{
    public const int MaxSize = 2048;

    /// <summary>Why Terry cannot hold a grid of this size, or null.</summary>
    public string? Problem =>
        Width < 2 || Width > MaxSize ? $"the hex map must be 2 to {MaxSize} hexes wide, not {Width}"
        : Width % 2 != 0 ? $"the hex map must be an even number of hexes wide, not {Width}"
        : Height < 1 || Height > MaxSize ? $"the hex map must be 1 to {MaxSize} hexes high, not {Height}"
        : null;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// Hexes added to each side of the map, counted on the (scaled) campaign hex grid. Top is north
/// (TIFF row 0).
/// </summary>
public sealed record MapPadding(int Left, int Right, int Top, int Bottom)
{
    public static readonly MapPadding None = new(0, 0, 0, 0);

    public bool IsNone => Left == 0 && Right == 0 && Top == 0 && Bottom == 0;

    public override string ToString() => $"left {Left}, right {Right}, top {Top}, bottom {Bottom}";
}

/// <summary>
/// A uniform scale of the map, snapped to a hex grid Terry can hold. The scaled grid's width is
/// the even number of hexes nearest the source width times the requested factor, and that width
/// fixes the factor actually used (<see cref="Effective"/>). The height follows from it: the
/// map's extent is half a hex more than its rows, so the rows are
/// round(effective x (rows + 1/2) - 1/2). That generally leaves the scaled map a fraction of a hex
/// off the grid's height (<see cref="NorthResidual"/>, at most half a hex); since the world's
/// origin is the south-west corner, the difference goes at the north edge.
/// </summary>
public sealed record MapScale(HexGrid Source, double Requested, HexGrid Scaled, double Effective, bool Capped)
{
    public static MapScale None(HexGrid source) => new(source, 1, source, 1, false);

    /// <summary>The scale of <paramref name="source"/> nearest <paramref name="requested"/>, capped to a grid of at most 2048 hexes each way.</summary>
    public static MapScale Plan(HexGrid source, double requested)
    {
        if (!(requested > 0) || double.IsInfinity(requested))
            throw new ArgumentException($"The map scale must be a number above 0, not {requested}");
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException($"Invalid hex grid size {source}");
        if (requested == 1)
            return None(source);

        double max = Math.Min((double)HexGrid.MaxSize / source.Width, (HexGrid.MaxSize + 0.5) / (source.Height + 0.5));
        bool capped = requested > max;
        double factor = Math.Min(requested, max);

        int width = Math.Clamp(2 * (int)Math.Round(source.Width * factor / 2, MidpointRounding.AwayFromZero), 2, HexGrid.MaxSize);
        double effective = (double)width / source.Width;
        int height = Math.Clamp((int)Math.Round(effective * (source.Height + 0.5) - 0.5, MidpointRounding.AwayFromZero), 1, HexGrid.MaxSize);
        return new MapScale(source, requested, new HexGrid(width, height), effective, capped);
    }

    public bool IsNone => Effective == 1 && Scaled == Source;

    /// <summary>
    /// Hexes between the scaled map's north edge and the grid's: positive when the grid is taller
    /// (the map's edge is repeated there), negative when the map is cropped.
    /// </summary>
    public double NorthResidual => Scaled.Height + 0.5 - Effective * (Source.Height + 0.5);
}

public enum Sampling
{
    /// <summary>Each pixel takes the source pixel it lands on: for indexed maps and hard-edged layers.</summary>
    Nearest,

    /// <summary>Bilinear when enlarging, a tent filter as wide as a target pixel when shrinking: for heights, colours, masks and normals.</summary>
    Linear,
}

/// <summary>
/// Resizes the maps of one run: a uniform <see cref="MapScale"/> anchored at the south-west corner,
/// then a <see cref="MapPadding"/> in hexes of the scaled grid. Each map's pixels per hex is its
/// size over the source hex grid's, rounded (CA's maps are whole multiples give or take a pixel or
/// two), so every map grows by its own pixels per hex: 8 for the height, blend and overlay maps, 4
/// for the shroud heights, 2 for the tile, tree and mask maps. Pixels that fall outside the source
/// map (the padding, and the scale's sliver at the north edge) repeat its edge, unless the caller
/// gives a fill.
///
/// The world scales with the map about its origin, the south-west corner: positions, heights and
/// sizes all by the same factor, so hexes and texels keep their world size. Padding the left or the
/// bottom then moves everything placed in the world by <see cref="OffsetX"/> / <see cref="OffsetZ"/>.
/// </summary>
public sealed class MapResizer
{
    public static readonly MapResizer Identity = new(MapScale.None(new HexGrid(1, 1)), MapPadding.None);

    public MapScale Scale { get; }
    public MapPadding Padding { get; }

    public MapResizer(MapScale scale, MapPadding padding)
    {
        if (padding.Left < 0 || padding.Right < 0 || padding.Top < 0 || padding.Bottom < 0)
            throw new ArgumentException($"Map padding cannot be negative ({padding})");
        if (scale.Source.Width <= 0 || scale.Source.Height <= 0)
            throw new ArgumentException($"Invalid hex grid size {scale.Source}");
        Scale = scale;
        Padding = padding;
    }

    public HexGrid Source => Scale.Source;

    /// <summary>The hex grid of the resized map.</summary>
    public HexGrid Final => new(Scale.Scaled.Width + Padding.Left + Padding.Right, Scale.Scaled.Height + Padding.Top + Padding.Bottom);

    /// <summary>What positions, heights and sizes in the world are multiplied by.</summary>
    public double Factor => Scale.Effective;

    public bool IsScaled => !Scale.IsNone;
    public bool IsNone => !IsScaled && Padding.IsNone;

    /// <summary>Where a map of this size goes: its scaled size and the pixels padded onto each side.</summary>
    public readonly record struct Layout(int ScaledWidth, int ScaledHeight, int Left, int Right, int Top, int Bottom)
    {
        public int Width => ScaledWidth + Left + Right;
        public int Height => ScaledHeight + Top + Bottom;
    }

    public Layout LayoutOf(int width, int height)
    {
        // Down the map, the staggered columns add half a hex to the rows (1941 = 2 x 970.5).
        double px = PerHex(width, Source.Width), py = PerHex(height, Source.Height + 0.5);
        return new Layout(
            width + Pixels(Scale.Scaled.Width - Source.Width, px),
            height + Pixels(Scale.Scaled.Height - Source.Height, py),
            Pixels(Padding.Left, px), Pixels(Padding.Right, px), Pixels(Padding.Top, py), Pixels(Padding.Bottom, py));

        static double PerHex(int size, double hexes)
        {
            double ratio = size / hexes;
            return ratio >= 1 ? Math.Round(ratio) : ratio;
        }

        static int Pixels(int hexes, double perHex) => (int)Math.Round(hexes * perHex);
    }

    /// <summary>The resized size of a map of this size.</summary>
    public (int Width, int Height) Size(int width, int height)
    {
        var layout = LayoutOf(width, height);
        return (layout.Width, layout.Height);
    }

    /// <summary>World width of the resized map, for the source map's <paramref name="worldWidth"/>.</summary>
    public double WorldWidth(double worldWidth) => worldWidth * Final.Width / Source.Width;

    /// <summary>How far east padding moves everything placed in the world (after scaling it).</summary>
    public double OffsetX(double worldWidth) => worldWidth / Source.Width * Padding.Left;

    /// <summary>How far north padding moves everything placed in the world (hexes are as wide in world units as they are tall).</summary>
    public double OffsetZ(double worldWidth) => worldWidth / Source.Width * Padding.Bottom;

    /// <summary>
    /// Resizes a row-major map of <paramref name="channels"/> values per pixel. Pixels outside the
    /// source map repeat its nearest edge pixel, or are <paramref name="fill"/> (one value per
    /// channel) when given. Returns the map itself when there is nothing to do.
    /// </summary>
    public (T[] Data, int Width, int Height) Resize<T>(T[] data, int width, int height, Sampling sampling,
        int channels = 1, T[]? fill = null)
        where T : unmanaged, INumberBase<T>
    {
        var layout = LayoutOf(width, height);
        if (layout.Width == width && layout.Height == height && !IsScaled)
            return (data, width, height);
        if (fill != null && fill.Length != channels)
            throw new ArgumentException($"The fill has {fill.Length} values, the map {channels} channels per pixel");

        bool linear = sampling == Sampling.Linear && IsScaled;
        var columns = Axis(width, layout.ScaledWidth, layout.Left, layout.Width, fromEnd: false, linear, edge: fill == null);
        var rows = Axis(height, layout.ScaledHeight, layout.Top, layout.Height, fromEnd: true, linear, edge: fill == null);

        int newWidth = layout.Width, newHeight = layout.Height;
        long rowLength = (long)width * channels, newRowLength = (long)newWidth * channels;
        var result = new T[newRowLength * newHeight];

        Parallel.For(0, newHeight, () => linear ? new double[rowLength] : null, (y, _, line) =>
        {
            long target = y * newRowLength;
            var rowTaps = rows[y];
            if (rowTaps == null)
            {
                for (int x = 0; x < newWidth; x++)
                    fill!.CopyTo(result, target + (long)x * channels);
                return line;
            }

            if (!linear)
            {
                long source = rowTaps[0].Index * rowLength;
                for (int x = 0; x < newWidth; x++)
                {
                    var taps = columns[x];
                    if (taps == null)
                        fill!.CopyTo(result, target + (long)x * channels);
                    else
                        Array.Copy(data, source + (long)taps[0].Index * channels, result, target + (long)x * channels, channels);
                }
                return line;
            }

            // Down the rows into one line of the source's width, then across it.
            Array.Clear(line!);
            foreach (var (index, weight) in rowTaps)
            {
                long source = index * rowLength;
                for (long i = 0; i < rowLength; i++)
                    line![i] += weight * double.CreateTruncating(data[source + i]);
            }
            for (int x = 0; x < newWidth; x++)
            {
                long o = target + (long)x * channels;
                var taps = columns[x];
                if (taps == null)
                {
                    fill!.CopyTo(result, o);
                    continue;
                }
                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    foreach (var (index, weight) in taps)
                        sum += weight * line![(long)index * channels + c];
                    result[o + c] = FromDouble<T>(sum);
                }
            }
            return line;
        }, _ => { });
        return (result, newWidth, newHeight);
    }

    /// <summary>Integers round to nearest (and saturate); floating point passes through.</summary>
    private static T FromDouble<T>(double value) where T : INumberBase<T> =>
        typeof(T) == typeof(float) || typeof(T) == typeof(double)
            ? T.CreateTruncating(value)
            : T.CreateSaturating(Math.Round(value, MidpointRounding.AwayFromZero));

    /// <summary>
    /// For each pixel along one axis of the resized map, the source pixels it is made from and
    /// their weights, or null where it is to be filled. Pixel centres sit at whole coordinates; the
    /// scaled map is anchored at the source's start, or at its end for <paramref name="fromEnd"/>
    /// (the rows, which run from the north while the world grows from the south).
    /// </summary>
    private (int Index, double Weight)[]?[] Axis(int sourceLength, int scaledLength, int padBefore, int length,
        bool fromEnd, bool linear, bool edge)
    {
        double k = Factor;
        double radius = Math.Max(1, 1 / k);
        var axis = new (int, double)[]?[length];
        for (int t = 0; t < length; t++)
        {
            int s = t - padBefore;
            double u = fromEnd ? sourceLength - 0.5 - (scaledLength - s - 0.5) / k : (s + 0.5) / k - 0.5;
            if (u < -0.5 || u > sourceLength - 0.5)
            {
                axis[t] = edge ? [(Math.Clamp((int)Math.Round(u), 0, sourceLength - 1), 1)] : null;
                continue;
            }
            if (!linear)
            {
                axis[t] = [(Math.Clamp((int)Math.Floor(u + 0.5), 0, sourceLength - 1), 1)];
                continue;
            }

            var taps = new List<(int Index, double Weight)>();
            double total = 0;
            for (int i = (int)Math.Ceiling(u - radius); i <= (int)Math.Floor(u + radius); i++)
            {
                double weight = 1 - Math.Abs(i - u) / radius;
                if (weight <= 0)
                    continue;
                int index = Math.Clamp(i, 0, sourceLength - 1);
                int existing = taps.FindIndex(tap => tap.Index == index);
                if (existing >= 0)
                    taps[existing] = (index, taps[existing].Weight + weight);
                else
                    taps.Add((index, weight));
                total += weight;
            }
            axis[t] = taps.Select(tap => (tap.Index, tap.Weight / total)).ToArray();
        }
        return axis;
    }
}
