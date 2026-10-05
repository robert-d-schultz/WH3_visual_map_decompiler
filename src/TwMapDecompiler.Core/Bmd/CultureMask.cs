using System.Globalization;
using System.Text.Json.Nodes;
using TwMapDecompiler.Core.Rpfm;

namespace TwMapDecompiler.Core.Bmd;

/// <summary>One prefab_types_tables row: a prefab type, the culture it stands for (if any) and its value.</summary>
public sealed record PrefabType(string Key, string? Culture, int Value);

/// <summary>
/// Reads the 64-bit culture bitmask attached to compiled BMD entities, using the bit -> culture
/// table from the <c>prefab_types_tables</c> DB table. A row's bit is <c>value - 1</c>, taken
/// mod 64 as a 64-bit shift is, so NONE (value 0, culture "*") is bit 63. Rows with no culture
/// (BASE, BUILDING_*, ABANDONED, ...) are prefab states, not cultures, and are ignored. A DLC
/// culture adds a row, so the table is read, not recorded.
///
/// A mask of all ones (or zero), or one with no culture bit set, means "no culture restriction"
/// and is rendered as an empty culture_mask attribute, matching the reference scripts.
/// </summary>
public sealed class CultureMask
{
    public const string TableName = "prefab_types_tables";

    /// <summary>
    /// The vanilla "order" factions. A prop masked for all of them is generic good-guy dressing
    /// (a torch every order faction gets), not one faction's own (Bretonnia's Lady of the Lake statue).
    /// </summary>
    private static readonly string[] Order =
        ["wh_main_emp_empire", "wh_main_dwf_dwarfs", "wh2_main_hef_high_elves", "wh3_main_cth_cathay"];

    /// <summary>
    /// Modded cultures that have no bit of their own and should see what vanilla cultures see:
    /// with <see cref="WithCustomCultures"/>, a mask naming every one of the vanilla cultures
    /// (all of them, so a set is a consensus) also names the modded one.
    /// </summary>
    public static readonly IReadOnlyList<(string Custom, string[] Vanilla)> CustomCultures =
    [
        ("mixer_nag_nagash", ["wh3_dlc29_nag_undead_legions"]),
        ("ovn_fimir", ["wh_dlc08_nor_norsca"]),
        ("mixer_vmp_jade_vampires", ["wh_main_vmp_vampire_counts"]),
        ("mixer_hng_hung", ["wh_dlc08_nor_norsca"]),
        ("mixer_cou_vampires", ["wh_main_vmp_vampire_counts"]),
        ("mixer_dom_dominion", ["wh_main_vmp_vampire_counts"]),
        ("mixer_khu_khuresh", ["wh_main_chs_chaos"]),
        ("mixer_teb_southern_realms", Order),
        ("mixer_gnob_gnoblar_horde", ["wh3_main_ogr_ogre_kingdoms"]),
        ("ovn_albion", Order),
        ("ovn_amazon", Order),
        ("ovn_araby", Order),
        ("ovn_mar_marienburg", Order),
        ("ovn_halflings", Order),
        ("mixer_nip_nippon", Order),
        ("mixer_ind_ind", Order),
        ("mixer_hbg_hobgoblins", ["wh_main_grn_greenskins"]),
        ("mixer_sam_samhan", Order),
        ("mixer_yam_yamato", Order),
        ("ovn_troll", ["wh_dlc08_nor_norsca"]),
    ];

    private readonly string?[] _cultures;
    private readonly IReadOnlyList<(string Custom, string[] Vanilla)> _custom;

    private CultureMask(string?[] cultures, IReadOnlyList<(string Custom, string[] Vanilla)> custom)
    {
        _cultures = cultures;
        _custom = custom;
    }

    /// <summary>
    /// The same table, but formatted masks also list the <see cref="CustomCultures"/> whose
    /// vanilla cultures they all name, after the vanilla ones.
    /// </summary>
    public CultureMask WithCustomCultures() => new(_cultures, CustomCultures);

    /// <summary>Culture of each bit (null for bits that are not a culture).</summary>
    public IReadOnlyList<string?> Cultures => _cultures;

    /// <summary>Reads the table using an already open pack session; null when no pack has it.</summary>
    public static async Task<CultureMask?> ReadAsync(PackFileSource packs, string scratchFolder, CancellationToken ct = default)
    {
        var rows = new List<PrefabType>();
        foreach (var row in await packs.ReadTableAsync(TableName, scratchFolder, ct))
        {
            if (Str(row, "prefab_type") is not { } key)
                continue;
            rows.Add(new PrefabType(key, Str(row, "culture"), Int(row, "value", key)));
        }
        return rows.Count > 0 ? FromRows(rows) : null;
    }

    /// <summary>
    /// Builds the bit table. Two cultures can share a bit (KISLEV and KISLEV_PROLOGUE); the mask
    /// cannot tell them apart, so the one whose prefab type sorts first by key wins.
    /// </summary>
    public static CultureMask FromRows(IEnumerable<PrefabType> rows)
    {
        var cultures = new string?[64];
        foreach (var row in rows.Where(r => !string.IsNullOrEmpty(r.Culture)).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            int bit = (row.Value - 1) & 63;
            cultures[bit] ??= row.Culture;
        }
        if (cultures.All(c => c == null))
            throw new InvalidDataException($"{TableName} has no row with a culture.");
        return new CultureMask(cultures, []);
    }

    public string Format(ulong mask)
    {
        if (mask == ulong.MaxValue || mask == 0)
            return string.Empty;

        var parts = new List<string>();
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0 && _cultures[bit] is { } culture)
                parts.Add(culture);
        }
        if (parts.Count > 0)
        {
            // Customs follow in list order; parts is a List, so check membership before adding.
            var vanilla = parts.ToHashSet(StringComparer.Ordinal);
            foreach (var (custom, of) in _custom)
                if (of.All(vanilla.Contains) && !parts.Contains(custom))
                    parts.Add(custom);
        }
        return string.Join(",", parts);
    }

    /// <summary>True when the mask imposes no culture restriction.</summary>
    public bool IsUnrestricted(ulong mask) => Format(mask).Length == 0;

    /// <summary>Sign-extends the old 4-byte mask (version &lt;= 6 files) to 64 bits, so -1 stays "all".</summary>
    public static ulong FromInt32(int mask) => unchecked((ulong)(long)mask);

    /// <summary>
    /// The game stores a mask on both a parent BMD entry and its child entities.
    /// A restricted parent mask wins over an unrestricted child mask.
    /// </summary>
    public ulong ResolveInherited(ulong parentMask, ulong ownMask)
        => !IsUnrestricted(parentMask) && IsUnrestricted(ownMask) ? parentMask : ownMask;

    /// <summary>
    /// Variant used for VFX, lights, sounds and composite scenes: a restricted
    /// parent mask always replaces the child's own mask.
    /// </summary>
    public ulong ResolveInheritedOverride(ulong parentMask, ulong ownMask)
        => !IsUnrestricted(parentMask) ? parentMask : ownMask;

    private static string? Str(Dictionary<string, JsonNode?> row, string column) =>
        row.TryGetValue(column, out var v) && v is JsonValue value && value.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private static int Int(Dictionary<string, JsonNode?> row, string column, string key) =>
        row.TryGetValue(column, out var v) && v is JsonValue value
        && int.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new InvalidDataException($"Prefab type '{key}' has no valid {column} (got '{v?.ToJsonString()}').");
}
