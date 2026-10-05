using System.Globalization;

namespace TwMapDecompiler.Core.Terry;

/// <summary>Value formatting helpers for Terry XML files.</summary>
public static class TerryFormat
{
    /// <summary>Float rounded to 5 decimal places with trailing zeros trimmed (transform components).</summary>
    public static string F5(double value)
    {
        double rounded = Math.Round(value, 5);
        if (rounded == 0)
            rounded = 0; // normalize -0
        return rounded.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>Float rounded to 6 decimal places with trailing zeros trimmed (fitted geometry, e.g. river splines).</summary>
    public static string F6(double value)
    {
        double rounded = Math.Round(value, 6);
        if (rounded == 0)
            rounded = 0; // normalize -0
        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>Shortest round-trip representation (raw float pass-through).</summary>
    public static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string R(float value) => ((double)value).ToString("R", CultureInfo.InvariantCulture);

    public static string B(bool value) => value ? "true" : "false";

    public static string Xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

/// <summary>Generates unique Terry entity ids: 15 hex chars starting with '1'.</summary>
public sealed class TerryIdGenerator(int? seed = null)
{
    private readonly Random _random = seed.HasValue ? new Random(seed.Value) : new Random();
    private readonly HashSet<string> _issued = [];

    public string Next()
    {
        const string hex = "0123456789abcdef";
        Span<char> chars = stackalloc char[15];
        while (true)
        {
            chars[0] = '1';
            for (int i = 1; i < 15; i++)
                chars[i] = hex[_random.Next(16)];
            string id = new(chars);
            if (_issued.Add(id))
                return id;
        }
    }
}
