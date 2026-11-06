using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The key dialog's keycode picker: category list ("Used in this keymap"
/// ranked first), search, the wrap toggles for both hands, the flat param
/// value it produces, seeding from an existing binding, and recorded keys.
/// </summary>
public class KeycodePickerViewModelTests
{
    // N1 twice (name choice + UseCount 2), DOT (spelling echo), and A under
    // LS(A) so all three used keycodes differ in count and spelling.
    private const string Keymap = """
        / {
            keymap {
                compatible = "zmk,keymap";
                base {
                    bindings = < &kp N1 &kp N1 &kp DOT &kp LS(A) >;
                };
            };
        };
        """;

    private static readonly ZmkKeycodeUsage Usage =
        ZmkKeycodeUsage.Count(ZmkKeymapLoader.LoadFromText(Keymap, "test"));

    // --- Categories -------------------------------------------------------

    [Fact]
    public void Categories_Default_UsedThenAllThenEnumOrder()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.Equal(16, vm.Categories.Count);
        Assert.True(vm.Categories[0].IsUsed);
        Assert.True(vm.Categories[0].Category is null);
        Assert.False(vm.Categories[1].IsUsed);
        Assert.True(vm.Categories[1].Category is null);
        Assert.Equal(
            Enum.GetValues<ZmkKeycodeCategory>(),
            vm.Categories.Skip(2).Select(c => c.Category!.Value));
        Assert.Same(vm.Categories[0], vm.SelectedCategory);
    }

    [Fact]
    public void Categories_ModifiersFirst_ModifierEntryMovesToTheFront()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: true);

        Assert.Equal(16, vm.Categories.Count);
        Assert.Equal(ZmkKeycodeCategory.Modifiers, vm.Categories[0].Category);
        Assert.Single(vm.Categories, c => c.Category == ZmkKeycodeCategory.Modifiers);
        Assert.True(vm.Categories[1].IsUsed);
        Assert.Same(vm.Categories[0], vm.SelectedCategory);
        Assert.False(vm.ShowWrapToggles);
    }

    [Fact]
    public void Categories_EmptyUsage_NoUsedEntry_AllSelected()
    {
        var vm = new KeycodePickerViewModel(ZmkKeycodeUsage.Empty, modifiersFirst: false);

        Assert.Equal(15, vm.Categories.Count);
        Assert.DoesNotContain(vm.Categories, c => c.IsUsed);
        Assert.True(vm.SelectedCategory.Category is null);
        Assert.False(vm.SelectedCategory.IsUsed);
    }

    [Fact]
    public void CategoryNames_EveryEntry_Resolves()
    {
        foreach (var modifiersFirst in new[] { false, true })
        {
            var vm = new KeycodePickerViewModel(Usage, modifiersFirst);
            foreach (var choice in vm.Categories)
                Assert.DoesNotContain("[", choice.Name);
        }
    }

    // --- Items ------------------------------------------------------------

    [Fact]
    public void Items_Default_UsedOrderWithUsageNamesAndCounts()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        // NUMBER_1 twice, then the two 1-use keycodes in catalog order.
        Assert.Equal(
            new[] { "NUMBER_1", "A", "PERIOD" },
            vm.Items.Select(i => i.Keycode.Name));
        Assert.Equal(2, vm.Items[0].UseCount);
        Assert.Equal(new[] { "N1", "A", "DOT" }, vm.Items.Select(i => i.Name));
    }

    [Fact]
    public void Search_FindsWithSpaces_ClearingRestoresTheUsedList()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        vm.SearchText = "page up";

        Assert.Equal("PAGE_UP", vm.Items[0].Keycode.Name);
        Assert.Equal(
            Loc.Instance.Format("KeyDialog_MatchCountFormat", vm.Items.Count),
            vm.MatchCountText);

        vm.SearchText = "";

        Assert.Equal(
            new[] { "NUMBER_1", "A", "PERIOD" },
            vm.Items.Select(i => i.Keycode.Name));
    }

    [Fact]
    public void Items_CategoryListsCatalog_ShiftGlyphFromShiftedKeycode()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        vm.SelectedCategory = Category(ZmkKeycodeCategory.Digits);

        Assert.Equal(
            ZmkKeycodeCatalog.InCategory(ZmkKeycodeCategory.Digits).Select(k => k.Name),
            vm.Items.Select(i => i.Keycode.Name));

        var n1 = vm.Items.Single(i => i.Keycode.Name == "NUMBER_1");
        Assert.Equal("1", n1.Glyph);
        Assert.Equal("!", n1.ShiftGlyph);
    }

    // --- Wrap toggles -------------------------------------------------------

    [Fact]
    public void IsShift_WrapsValue_AndRaisesChangedPerStep()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.SelectedKeycode = Code("A");
        vm.IsShift = true;

        Assert.Equal(new[] { "LS", "A" }, vm.Value);
        Assert.Equal("LS(A)", vm.ValueText);
        Assert.True(vm.HasValue);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void IsRightShift_Unchecked_ClearsTheWrap_AndRaisesChangedOnce()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.Seed(new[] { "RS", "A" });
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.IsRightShift = false;

        Assert.Equal(new[] { "A" }, vm.Value);
        Assert.Equal(1, changes);
    }

    // --- ToggleWrap ---------------------------------------------------------

    [Fact]
    public void ToggleWrap_RightGui_FlipsOnThenOff_RaisingChangedPerStep()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.SelectedKeycode = Code("A");
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.ToggleWrap(ZmkModifiers.RightGui);
        Assert.Equal(new[] { "RG", "A" }, vm.Value);

        vm.ToggleWrap(ZmkModifiers.RightGui);
        Assert.Equal(new[] { "A" }, vm.Value);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void ToggleWrap_NotExactlyOneFlag_Throws()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => vm.ToggleWrap(ZmkModifiers.LeftShift | ZmkModifiers.LeftCtrl));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => vm.ToggleWrap(ZmkModifiers.None));
    }

    // --- Seeding ------------------------------------------------------------

    [Fact]
    public void Seed_KeepsTheOpenedSpelling_ForThatKeycodeOnly()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.True(vm.Seed(new[] { "NUMBER_1" }));
        Assert.Equal(new[] { "NUMBER_1" }, vm.Value);

        // Another keycode writes the usage name ("B" is unused → catalog
        // shortest); coming back to the seeded keycode restores its spelling.
        vm.SelectedKeycode = Code("B");
        Assert.Equal(new[] { "B" }, vm.Value);

        vm.SelectedKeycode = Code("NUMBER_1");
        Assert.Equal(new[] { "NUMBER_1" }, vm.Value);
    }

    [Fact]
    public void Seed_SplitsLeftToggles_KeepsRightSideWraps()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        Assert.True(vm.Seed(new[] { "RS", "LC", "A" }));

        Assert.True(vm.IsCtrl);
        Assert.False(vm.IsShift);
        Assert.False(vm.IsAlt);
        Assert.False(vm.IsGui);
        // Same keycode, same flags — in Wrap's fixed order.
        Assert.Equal(new[] { "LC", "RS", "A" }, vm.Value);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Seed_RightHandGroup_ChecksRightToggle_AndValueKeepsIt()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.True(vm.Seed(new[] { "RS", "A" }));

        Assert.True(vm.IsRightShift);
        Assert.False(vm.IsShift);
        Assert.Equal(new[] { "RS", "A" }, vm.Value);
    }

    [Fact]
    public void Seed_BothHands_TogglesWrapFlagsAndWrapOrder()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.True(vm.Seed(new[] { "LC", "RA", "T" }));

        Assert.True(vm.IsCtrl);
        Assert.True(vm.IsRightAlt);
        Assert.Equal(ZmkModifiers.LeftCtrl | ZmkModifiers.RightAlt, vm.WrapFlags);
        // Wrap's fixed order LC LS LA LG RC RS RA RG puts LC before RA.
        Assert.Equal(new[] { "LC", "RA", "T" }, vm.Value);
    }

    [Fact]
    public void Seed_RoundTripsAPlainWrappedGroup()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        Assert.True(vm.Seed(new[] { "LS", "N1" }));

        Assert.Equal(new[] { "LS", "N1" }, vm.Value);
    }

    [Fact]
    public void Seed_UnknownKeycodeOrNoKeycode_FailsWithoutChanges()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        Assert.False(vm.Seed(new[] { "FOO" }));
        Assert.False(vm.Seed(new[] { "LS" }));

        Assert.Null(vm.SelectedKeycode);
        Assert.Null(vm.Value);
        Assert.False(vm.HasValue);
        Assert.Equal("", vm.ValueText);
        Assert.Equal(0, changes);
    }

    // --- Selection ------------------------------------------------------------

    [Fact]
    public void SelectedCategory_ChangeKeepsTheKeycode_WithoutAListedItem()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.SelectedItem = vm.Items.Single(i => i.Keycode.Name == "A");
        Assert.Equal(Code("A"), vm.SelectedKeycode);

        vm.SelectedCategory = Category(ZmkKeycodeCategory.Digits);

        Assert.Equal(Code("A"), vm.SelectedKeycode);
        Assert.Null(vm.SelectedItem);

        // A null set (e.g. the view clearing a refreshed list) never drops it.
        vm.SelectedItem = null;
        Assert.Equal(Code("A"), vm.SelectedKeycode);
    }

    [Fact]
    public void SelectedCategory_NullIsIgnored()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        var before = vm.SelectedCategory;
        var items = vm.Items;

        vm.SelectedCategory = null!;

        Assert.Same(before, vm.SelectedCategory);
        Assert.Same(items, vm.Items);
    }

    // --- Recording ------------------------------------------------------------

    [Fact]
    public void ToggleRecording_FlipsIsRecording()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);

        vm.ToggleRecordingCommand.Execute(null);
        Assert.True(vm.IsRecording);
        vm.ToggleRecordingCommand.Execute(null);
        Assert.False(vm.IsRecording);
    }

    [Fact]
    public void ApplyRecorded_SelectsKeyAndTogglesAndCategory()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.SearchText = "junk";
        vm.ToggleRecordingCommand.Execute(null);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        Assert.True(vm.ApplyRecorded(new RecordedKey("NUMBER_1", ZmkModifiers.LeftShift)));

        Assert.Equal(Code("NUMBER_1"), vm.SelectedKeycode);
        Assert.True(vm.IsShift);
        Assert.Equal("", vm.SearchText);
        Assert.Equal(ZmkKeycodeCategory.Digits, vm.SelectedCategory.Category);
        Assert.False(vm.IsRecording);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ApplyRecorded_ReplacesToggles_AndUnknownNameOnlyStopsRecording()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.IsCtrl = true;
        vm.ToggleRecordingCommand.Execute(null);

        Assert.False(vm.ApplyRecorded(new RecordedKey("NOT_A_KEY", ZmkModifiers.None)));
        Assert.False(vm.IsRecording);
        Assert.True(vm.IsCtrl); // the failure changes nothing but IsRecording

        Assert.True(vm.ApplyRecorded(new RecordedKey("PERIOD", ZmkModifiers.None)));
        Assert.False(vm.IsCtrl); // modifiers from the recording replace the toggles
        Assert.Equal(Code("PERIOD"), vm.SelectedKeycode);
        Assert.Equal(new[] { "DOT" }, vm.Value);
    }

    [Fact]
    public void ApplyRecorded_AfterRightHandSeed_ClearsRightToggles()
    {
        var vm = new KeycodePickerViewModel(Usage, modifiersFirst: false);
        vm.Seed(new[] { "RS", "RA", "A" });
        vm.ToggleRecordingCommand.Execute(null);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        Assert.True(vm.ApplyRecorded(new RecordedKey("NUMBER_1", ZmkModifiers.LeftShift)));

        Assert.False(vm.IsRightShift);
        Assert.False(vm.IsRightAlt);
        Assert.True(vm.IsShift);
        Assert.Equal(new[] { "LS", "N1" }, vm.Value);
        Assert.Equal(1, changes);
    }

    // --- Construction -----------------------------------------------------------

    [Fact]
    public void ShowWrapToggles_FalseOnlyForModifiersFirst()
    {
        Assert.True(new KeycodePickerViewModel(Usage, modifiersFirst: false).ShowWrapToggles);
        Assert.False(new KeycodePickerViewModel(Usage, modifiersFirst: true).ShowWrapToggles);
    }

    [Fact]
    public void Constructor_NullUsage_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new KeycodePickerViewModel(null!, modifiersFirst: false));
    }

    // --- helpers ------------------------------------------------------------

    private static ZmkKeycode Code(string name) => ZmkKeycodeCatalog.Find(name)!;

    private static KeycodeCategoryChoice Category(ZmkKeycodeCategory category)
    {
        // Fresh VM just to reach the shared, deterministic category entries;
        // callers assign it to their own VM's SelectedCategory.
        return new KeycodePickerViewModel(Usage, modifiersFirst: false)
            .Categories.First(c => c.Category == category);
    }
}
