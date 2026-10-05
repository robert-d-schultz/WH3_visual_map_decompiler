using TwMapDecompiler.Core.Bmd;
using Xunit;

namespace TwMapDecompiler.Tests;

/// <summary>
/// The culture mask's bits come from prefab_types_tables: bit = value - 1 for rows with a culture.
/// The rows below are real ones from the shipped table.
/// </summary>
public class CultureMaskTests
{
    private static readonly CultureMask Cultures = CultureMask.FromRows([
        new("NONE", "*", 0),
        new("BASE", null, 1),
        new("BUILDING_BASE", null, 2),
        new("BEASTMEN", "wh_dlc03_bst_beastmen", 7),
        new("BRETONNIA", "wh_main_brt_bretonnia", 8),
        new("ABANDONED", null, 15),
        new("KISLEV_PROLOGUE", "wh3_main_pro_ksl_kislev", 24),
        new("KISLEV", "wh3_main_ksl_kislev", 24),
        new("CHAOS_DWARFS", "wh3_dlc23_chd_chaos_dwarfs", 35),
        new("UNDEAD_LEGIONS", "wh3_dlc29_nag_undead_legions", 36),
        new("BUILDING_DEVASTATED", null, 37),
    ]);

    [Fact]
    public void Bit_is_value_minus_one()
    {
        Assert.Equal("wh_dlc03_bst_beastmen", Cultures.Format(1UL << 6));
        Assert.Equal("wh3_dlc29_nag_undead_legions", Cultures.Format(1UL << 35));
        Assert.Equal("wh_dlc03_bst_beastmen,wh_main_brt_bretonnia", Cultures.Format((1UL << 6) | (1UL << 7)));
    }

    [Fact]
    public void None_row_wraps_to_bit_63() => Assert.Equal("*", Cultures.Format(1UL << 63));

    [Fact]
    public void Shared_bit_keeps_the_first_key() => Assert.Equal("wh3_main_ksl_kislev", Cultures.Format(1UL << 23));

    [Fact]
    public void Non_culture_bits_are_ignored()
    {
        ulong states = (1UL << 0) | (1UL << 1) | (1UL << 14) | (1UL << 36);
        Assert.Equal("", Cultures.Format(states));
        Assert.True(Cultures.IsUnrestricted(states));
        Assert.Equal("wh_main_brt_bretonnia", Cultures.Format(states | (1UL << 7)));
    }

    [Fact]
    public void All_or_nothing_is_unrestricted()
    {
        Assert.True(Cultures.IsUnrestricted(ulong.MaxValue));
        Assert.True(Cultures.IsUnrestricted(0));
        Assert.True(Cultures.IsUnrestricted(CultureMask.FromInt32(-1)));
    }

    [Fact]
    public void Custom_cultures_follow_their_vanilla_culture()
    {
        var custom = Cultures.WithCustomCultures();
        Assert.Equal("wh3_dlc29_nag_undead_legions,mixer_nag_nagash", custom.Format(1UL << 35));
        Assert.Equal("wh_main_brt_bretonnia", custom.Format(1UL << 7));
        Assert.Equal("wh_dlc03_bst_beastmen", Cultures.Format(1UL << 6));
    }

    [Fact]
    public void Custom_cultures_are_listed_once_in_list_order()
    {
        var table = CultureMask.FromRows([
            new("CHAOS", "wh_main_chs_chaos", 9),
            new("VAMPIRE", "wh_main_vmp_vampire_counts", 13),
            new("NORSCA", "wh_dlc08_nor_norsca", 34),
        ]).WithCustomCultures();
        Assert.Equal(
            "wh_main_chs_chaos,wh_main_vmp_vampire_counts,wh_dlc08_nor_norsca," +
            "ovn_fimir,mixer_vmp_jade_vampires,mixer_hng_hung,mixer_cou_vampires,mixer_dom_dominion,mixer_khu_khuresh,ovn_troll",
            table.Format((1UL << 8) | (1UL << 12) | (1UL << 33)));
    }

    private static readonly CultureMask Order = CultureMask.FromRows([
        new("BRETONNIA", "wh_main_brt_bretonnia", 8),
        new("DWARFS", "wh_main_dwf_dwarfs", 10),
        new("EMPIRE", "wh_main_emp_empire", 11),
        new("HIGHELVES", "wh2_main_hef_high_elves", 19),
        new("CATHAY", "wh3_main_cth_cathay", 33),
    ]).WithCustomCultures();

    private const ulong Brt = 1UL << 7, Dwf = 1UL << 9, Emp = 1UL << 10, Hef = 1UL << 18, Cth = 1UL << 32;

    [Fact]
    public void Set_needs_every_culture_in_it()
    {
        string all = Order.Format(Dwf | Emp | Hef | Cth | Brt);
        Assert.StartsWith("wh_main_brt_bretonnia,wh_main_dwf_dwarfs,wh_main_emp_empire,wh2_main_hef_high_elves,wh3_main_cth_cathay," +
                          "mixer_teb_southern_realms,ovn_albion,", all);
        Assert.EndsWith(",mixer_yam_yamato", all);
    }

    [Fact]
    public void Partial_set_adds_nothing()
    {
        Assert.Equal("wh_main_brt_bretonnia", Order.Format(Brt));
        Assert.Equal("wh_main_dwf_dwarfs", Order.Format(Dwf));
        Assert.DoesNotContain("ovn_", Order.Format(Dwf | Emp | Hef));
    }

    [Fact]
    public void Custom_cultures_do_not_restrict_an_unrestricted_mask()
    {
        var custom = Cultures.WithCustomCultures();
        Assert.Equal("", custom.Format(ulong.MaxValue));
        Assert.Equal("", custom.Format(1UL << 0));
    }

    [Fact]
    public void Table_without_cultures_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => CultureMask.FromRows([new("BASE", null, 1)]));
}
