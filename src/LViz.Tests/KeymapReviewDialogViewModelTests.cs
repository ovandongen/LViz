using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The review-before-saving dialog's view model: one translated line per change
/// the save writes — structure edits, then renames, then keys — plus the
/// editable commit message for a GitHub save. Plans are built by hand; the view model
/// renders what <see cref="KeymapSavePlan"/> says and derives nothing itself.
/// <para>
/// Expected text is always built through <see cref="Loc"/> so the assertions
/// hold whichever resource culture the test run resolves.
/// </para>
/// </summary>
public class KeymapReviewDialogViewModelTests
{
    private static KeymapSavePlan Plan(
        LayerStructureEdit[]? structure = null,
        KeymapBindingChange[]? bindings = null,
        KeymapLayerRenameChange[]? renames = null)
        => new(
            "",
            structure ?? Array.Empty<LayerStructureEdit>(),
            bindings ?? Array.Empty<KeymapBindingChange>(),
            renames ?? Array.Empty<KeymapLayerRenameChange>());

    private static KeymapReviewDialogViewModel Review(
        KeymapSavePlan plan,
        string targetName = "slabwing.keymap",
        string? commitMessage = null)
        => new(new KeymapReviewRequest(plan, targetName, commitMessage));

    private static KeyBinding Key(string behavior, params string[] parameters) => new(behavior, parameters);

    [Fact]
    public void Ctor_NullRequest_Throws()
        => Assert.Throws<ArgumentNullException>(() => new KeymapReviewDialogViewModel(null!));

    [Fact]
    public void Lines_OneBindingChange_IsTheKeyLine()
    {
        var vm = Review(Plan(bindings: new[]
        {
            new KeymapBindingChange(0, "Base", 11, Key("&kp", "Q"), KeyBinding.Transparent),
        }));

        Assert.Single(vm.Lines);
        Assert.Equal(
            new[] { Loc.Instance.Format("Edit_ReviewKeyFormat", "Base", 11, "&kp Q", "&trans") },
            vm.Lines);
    }

    [Fact]
    public void Lines_StructureRenameAndKey_GoesStructureThenRenameThenKey()
    {
        var vm = Review(Plan(
            structure: new LayerStructureEdit[] { new LayerStructureEdit.Add(2, "Aux", new[] { KeyBinding.None }) },
            renames: new[] { new KeymapLayerRenameChange(1, "Lower", "Lower2") },
            bindings: new[]
            {
                new KeymapBindingChange(0, "Base", 3, KeyBinding.None, Key("&kp", "Z")),
            }));

        Assert.Equal(
            new[]
            {
                Loc.Instance.Format("Edit_ReviewAddFormat", "Aux", 2),
                Loc.Instance.Format("Edit_ReviewRenameFormat", 1, "Lower", "Lower2"),
                Loc.Instance.Format("Edit_ReviewKeyFormat", "Base", 3, "&none", "&kp Z"),
            },
            vm.Lines);
    }

    [Fact]
    public void Lines_Delete_UsesItsFormatWithItsIndex()
    {
        var vm = Review(Plan(structure: new LayerStructureEdit[] { new LayerStructureEdit.Delete(4) }));

        Assert.Equal(
            new[] { Loc.Instance.Format("Edit_ReviewDeleteFormat", 4) },
            vm.Lines);
    }

    [Fact]
    public void Lines_Move_UsesItsFormatWithBothPositions()
    {
        var vm = Review(Plan(structure: new LayerStructureEdit[] { new LayerStructureEdit.Move(1, 3) }));

        Assert.Equal(
            new[] { Loc.Instance.Format("Edit_ReviewMoveFormat", 1, 3) },
            vm.Lines);
    }

    [Fact]
    public void Lines_StructureEdits_KeepTheirOrder()
    {
        var vm = Review(Plan(structure: new LayerStructureEdit[]
        {
            new LayerStructureEdit.Move(0, 2),
            new LayerStructureEdit.Delete(1),
            new LayerStructureEdit.Add(3, "Tweak", new[] { KeyBinding.None }),
        }));

        Assert.Equal(
            new[]
            {
                Loc.Instance.Format("Edit_ReviewMoveFormat", 0, 2),
                Loc.Instance.Format("Edit_ReviewDeleteFormat", 1),
                Loc.Instance.Format("Edit_ReviewAddFormat", "Tweak", 3),
            },
            vm.Lines);
    }

    [Fact]
    public void Lines_ComboChanges_GoAfterRenames_BeforeKeys()
    {
        var zoom = new ZmkCombo("combo_zoom", "", [10, 11], [-1], Key("&kp", "Z"))
        {
            NodeName = "combo_zoom",
        };
        var f12Old = new ZmkCombo("f12", "", [0, 1], [-1], Key("&kp", "F12"))
        {
            NodeName = "f12",
        };
        var f12New = new ZmkCombo("f12", "", [0, 1], [-1], Key("&kp", "F13"))
        {
            NodeName = "f12",
        };
        var mouseMid = new ZmkCombo("mouse-mid", "", [46, 47], [-1], Key("&mkp", "MCLK"))
        {
            NodeName = "mouse-mid",
        };

        var plan = Plan(
            structure: new LayerStructureEdit[] { new LayerStructureEdit.Delete(4) },
            renames: new[] { new KeymapLayerRenameChange(1, "Lower", "Lower2") },
            bindings: new[]
            {
                new KeymapBindingChange(0, "Base", 3, KeyBinding.None, Key("&kp", "Q")),
            }) with
        {
            ComboChanges = new[]
            {
                new KeymapComboChange(new ComboEdit.Add(zoom), null, zoom),
                new KeymapComboChange(new ComboEdit.Change("f12", f12New), f12Old, f12New),
                new KeymapComboChange(new ComboEdit.Delete("mouse-mid"), mouseMid, null),
            },
        };

        var vm = Review(plan);

        Assert.Equal(
            new[]
            {
                Loc.Instance.Format("Edit_ReviewDeleteFormat", 4),
                Loc.Instance.Format("Edit_ReviewRenameFormat", 1, "Lower", "Lower2"),
                Loc.Instance.Format("Edit_ReviewComboAddFormat", "combo_zoom", "10+11", "&kp Z"),
                Loc.Instance.Format("Edit_ReviewComboEditFormat", "f12", "0+1", "&kp F13"),
                Loc.Instance.Format("Edit_ReviewComboDeleteFormat", "mouse-mid", "46+47"),
                Loc.Instance.Format("Edit_ReviewKeyFormat", "Base", 3, "&none", "&kp Q"),
            },
            vm.Lines);

        // Resx safety net for the three combo keys: a missing key renders as
        // "[Key]" and both sides of the comparison would resolve it alike.
        Assert.DoesNotContain(vm.Lines, line => line.StartsWith("[", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_DeleteComboWithoutOld_Throws()
    {
        var plan = Plan() with
        {
            ComboChanges = new[]
            {
                new KeymapComboChange(new ComboEdit.Delete("gone"), null, null),
            },
        };

        Assert.Throws<ArgumentException>(() => Review(plan));
    }

    [Fact]
    public void Lines_EmptyPlan_IsEmpty()
        => Assert.Empty(Review(Plan()).Lines);

    [Fact]
    public void CommitMessage_NullMeansNoCommitBox()
    {
        var vm = Review(Plan());

        Assert.Null(vm.CommitMessage);
        Assert.False(vm.HasCommitMessage);
    }

    [Fact]
    public void CommitMessage_IsPassedThroughUnchanged()
    {
        var vm = Review(Plan(), "octocat/zmk-config (main)", "LViz: edit 1 key");

        Assert.True(vm.HasCommitMessage);
        Assert.Equal("LViz: edit 1 key", vm.CommitMessage);
    }

    [Fact]
    public void CommitMessage_Edited_TheAnswerCarriesItTrimmed()
    {
        var vm = Review(Plan(), "octocat/zmk-config (main)", "LViz: edit 1 key");

        vm.CommitMessage = "  Swap the B key\n";

        Assert.True(vm.CanSave);
        Assert.Equal("Swap the B key", vm.Answer()!.CommitMessage);
    }

    [Fact]
    public void CommitMessage_Blank_CannotSave_AndRaisesCanSave()
    {
        var vm = Review(Plan(), "octocat/zmk-config (main)", "LViz: edit 1 key");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.CommitMessage = "   ";

        Assert.False(vm.CanSave);
        Assert.Null(vm.Answer());
        Assert.Contains(nameof(KeymapReviewDialogViewModel.CanSave), raised);
    }

    [Fact]
    public void LocalSave_CanSave_WithNoCommitMessage()
    {
        var vm = Review(Plan());

        Assert.True(vm.CanSave);
        Assert.Null(vm.Answer()!.CommitMessage);
    }

    [Fact]
    public void HeaderText_NamesTheTarget()
        => Assert.Contains("slabwing.keymap", Review(Plan()).HeaderText);

    [Fact]
    public void HeaderText_UsesTheHeaderFormat()
        => Assert.Equal(
            Loc.Instance.Format("Edit_ReviewHeaderFormat", "octocat/zmk-config (main)"),
            Review(Plan(), "octocat/zmk-config (main)").HeaderText);

    [Fact]
    public void Lines_EveryFormat_HasAResolvedKey()
    {
        var vm = Review(Plan(
            structure: new LayerStructureEdit[]
            {
                new LayerStructureEdit.Add(1, "Aux", new[] { KeyBinding.None }),
                new LayerStructureEdit.Delete(2),
                new LayerStructureEdit.Move(0, 1),
            },
            renames: new[] { new KeymapLayerRenameChange(3, "Lower", "Base") },
            bindings: new[]
            {
                new KeymapBindingChange(0, "Base", 11, Key("&kp", "Q"), KeyBinding.Transparent),
            }));

        // A missing resx key renders as "[Key]" instead of failing, so an
        // unresolved key is only visible as a line that starts with '['. The
        // expected strings can't be compared against here: both sides would
        // resolve the same missing key identically.
        Assert.NotEmpty(vm.Lines);
        Assert.DoesNotContain(vm.Lines, line => line.StartsWith("[", StringComparison.Ordinal));
        Assert.False(vm.HeaderText.StartsWith("[", StringComparison.Ordinal));
    }
}
