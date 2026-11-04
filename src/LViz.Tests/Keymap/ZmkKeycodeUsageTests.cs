using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Keycode usage counting over a loaded keymap (the picker's "Used in this
/// keymap" ranking) plus the spelling choice for the key dialog, and the
/// logical-param grouping <see cref="ZmkModifierWrap.SplitLogical"/> that
/// feeds it.
/// </summary>
public class ZmkKeycodeUsageTests
{
    // 8 bindings per layer (the validator wants equal counts). Layer 1
    // carries &kp A so &lt 1 on layer 0 stays in range.
    private const string CountingKeymap = """
        / {
            keymap {
                compatible = "zmk,keymap";
                base {
                    bindings = <
                        &kp N1 &kp NUMBER_1 &kp N1 &kp LS(N1)
                        &mt F1 N2 &lt 1 DOT &trans &bt BT_SEL 0
                    >;
                };
                second {
                    bindings = <
                        &kp A &trans &trans &trans
                        &trans &trans &trans &trans
                    >;
                };
            };
        };
        """;

    private const string Number1OnlyKeymap = """
        / {
            keymap {
                compatible = "zmk,keymap";
                base {
                    bindings = < &kp NUMBER_1 &kp NUMBER_1 >;
                };
            };
        };
        """;

    private const string HoldTapKeymap = """
        / {
            behaviors {
                hm: hm {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&kp>, <&kp>;
                };
            };
            keymap {
                compatible = "zmk,keymap";
                base {
                    bindings = < &hm LSHFT A >;
                };
            };
        };
        """;

    // --- SplitLogical ---------------------------------------------------

    [Fact]
    public void SplitLogical_SingleKeycode_OneGroup()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "A" }),
            new[] { "A" });
    }

    [Fact]
    public void SplitLogical_ModifierChain_JoinsFollowingGroup()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "LS", "LC", "A" }),
            new[] { "LS", "LC", "A" });
    }

    [Fact]
    public void SplitLogical_BareModifierThenWrap_TwoGroups()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "LSHFT", "LS", "A" }),
            new[] { "LSHFT" },
            new[] { "LS", "A" });
    }

    [Fact]
    public void SplitLogical_TwoBareParams_TwoGroups()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "F1", "N1" }),
            new[] { "F1" },
            new[] { "N1" });
    }

    [Fact]
    public void SplitLogical_RawThenWrappedKeycode()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "2", "LS", "N7" }),
            new[] { "2" },
            new[] { "LS", "N7" });
    }

    [Fact]
    public void SplitLogical_TrailingModifierFunction_StaysItsOwnGroup()
    {
        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "A", "LS" }),
            new[] { "A" },
            new[] { "LS" });

        AssertGroups(
            ZmkModifierWrap.SplitLogical(new[] { "LS" }),
            new[] { "LS" });
    }

    [Fact]
    public void SplitLogical_Empty_IsEmptyList()
    {
        Assert.Empty(ZmkModifierWrap.SplitLogical(Array.Empty<string>()));
    }

    [Fact]
    public void SplitLogical_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ZmkModifierWrap.SplitLogical(null!));
    }

    // --- Counting ---------------------------------------------------------

    [Fact]
    public void Count_MainFixture_CountsEveryKeycodeSide()
    {
        var usage = ZmkKeycodeUsage.Count(Load(CountingKeymap));

        // &kp N1 ×2, &kp NUMBER_1 ×1 and the N1 inside &kp LS(N1).
        Assert.Equal(4, usage.CountOf(Code("NUMBER_1")));
        // &mt's Modifier param and Keycode param, &lt's Keycode param.
        Assert.Equal(1, usage.CountOf(Code("F1")));
        Assert.Equal(1, usage.CountOf(Code("NUMBER_2")));
        Assert.Equal(1, usage.CountOf(Code("PERIOD")));
        // The only binding of layer 1.
        Assert.Equal(1, usage.CountOf(Code("A")));

        // &trans has no params, &bt's command is no keycode param → the five
        // above are all the picker may rank, and the top one is the 4-use keycode.
        Assert.Equal(5, usage.Ranked.Count);
        Assert.Equal("NUMBER_1", usage.Ranked[0].Keycode.Name);
        Assert.DoesNotContain("BT_SEL", usage.Ranked.Select(u => u.Keycode.Name));
    }

    [Fact]
    public void Count_UserHoldTap_CountsBothSides()
    {
        var usage = ZmkKeycodeUsage.Count(Load(HoldTapKeymap));

        Assert.Equal(1, usage.CountOf(Code("LEFT_SHIFT")));
        Assert.Equal(1, usage.CountOf(Code("A")));
    }

    [Fact]
    public void Count_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ZmkKeycodeUsage.Count(null!));
    }

    // --- NameFor ----------------------------------------------------------

    [Fact]
    public void NameFor_UsedKeycode_WinsWithMostUsedSpelling()
    {
        var usage = ZmkKeycodeUsage.Count(Load(CountingKeymap));

        // 3 uses of N1 against 1 of NUMBER_1.
        Assert.Equal("N1", usage.NameFor(Code("NUMBER_1")));
    }

    [Fact]
    public void NameFor_FileUsesOnlyCanonical_WritesCanonical()
    {
        var usage = ZmkKeycodeUsage.Count(Load(Number1OnlyKeymap));

        Assert.Equal("NUMBER_1", usage.NameFor(Code("NUMBER_1")));
    }

    [Fact]
    public void NameFor_UnusedKeycode_ShortestNameOrAliasEarliestWins()
    {
        var usage = ZmkKeycodeUsage.Empty;

        Assert.Equal("DOT", usage.NameFor(Code("PERIOD")));
        // LCTL is deprecated and never offered.
        Assert.Equal("LCTRL", usage.NameFor(Code("LEFT_CONTROL")));
        // LGUI/LWIN/LCMD/LMETA all length 4 → the earliest alias.
        Assert.Equal("LGUI", usage.NameFor(Code("LEFT_GUI")));
        Assert.Equal("A", usage.NameFor(Code("A")));
    }

    // --- Counts and Empty -------------------------------------------------

    [Fact]
    public void CountOf_UnusedKeycode_IsZero()
    {
        var usage = ZmkKeycodeUsage.Count(Load(CountingKeymap));

        Assert.Equal(0, usage.CountOf(Code("LEFT_SHIFT")));
    }

    [Fact]
    public void Empty_Ranked_IsEmpty_AndCountsAreZero()
    {
        Assert.Empty(ZmkKeycodeUsage.Empty.Ranked);
        Assert.Equal(0, ZmkKeycodeUsage.Empty.CountOf(Code("A")));
    }

    // --- helpers ----------------------------------------------------------

    private static KeyboardConfig Load(string source) =>
        ZmkKeymapLoader.LoadFromText(source, "test");

    private static ZmkKeycode Code(string name) => ZmkKeycodeCatalog.Find(name)!;

    private static void AssertGroups(
        IReadOnlyList<IReadOnlyList<string>> groups, params string[][] expected)
    {
        Assert.Equal(expected.Length, groups.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                groups[i].SequenceEqual(expected[i]),
                $"Group {i} is [{string.Join(", ", groups[i])}], " +
                $"expected [{string.Join(", ", expected[i])}].");
        }
    }
}
