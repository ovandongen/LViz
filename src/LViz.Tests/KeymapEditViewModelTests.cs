using System.IO;
using System.Text;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Tests.GitHub;
using LViz.Tests.Keymap;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The App-side owner of one edit session: fresh state, clearing keys to
/// <c>&amp;trans</c> / <c>&amp;none</c>, the banner text, undo/redo command
/// plumbing, selection, what <see cref="KeymapEditViewModel.Start"/> reads off
/// disk, and saving — local file with backup, GitHub commit, and a changed
/// target reported as a conflict result.
/// </summary>
public class KeymapEditViewModelTests
{
    /// <summary>2 layers ("Base", "Lower"), 3 keys each.</summary>
    private const string KeymapText = """
        / {
            keymap {
                compatible = "zmk,keymap";

                base {
                    display-name = "Base";
                    bindings = <&kp A &kp B &kp C>;
                };

                lower {
                    display-name = "Lower";
                    bindings = <&kp X &kp Y &kp Z>;
                };
            };
        };
        """;

    /// <summary>3 layers ("Base", "Lower", "Raise"), 3 keys each; Base's key 2
    /// and a combo both target Raise, so deleting it is blocked.</summary>
    private const string ComboKeymapText = """
        / {
            combos {
                compatible = "zmk,combos";
                combo_raise {
                    key-positions = <0 1>;
                    bindings = <&mo 2>;
                };
            };
            keymap {
                compatible = "zmk,keymap";
                base  { display-name = "Base";  bindings = <&kp A &kp B &mo 2>; };
                lower { display-name = "Lower"; bindings = <&kp X &kp Y &kp Z>; };
                raise { display-name = "Raise"; bindings = <&trans &trans &trans>; };
            };
        };
        """;

    /// <summary>The same keymap without the combos node — deleting Raise is
    /// then allowed and clears Base's key 2.</summary>
    private const string NoComboKeymapText = """
        / {
            keymap {
                compatible = "zmk,keymap";
                base  { display-name = "Base";  bindings = <&kp A &kp B &mo 2>; };
                lower { display-name = "Lower"; bindings = <&kp X &kp Y &kp Z>; };
                raise { display-name = "Raise"; bindings = <&trans &trans &trans>; };
            };
        };
        """;

    private static KeymapEditViewModel NewVm() => NewVm(KeymapText);

    private static KeymapEditViewModel NewVm(string text) =>
        new(
            new KeymapEditSource("x.keymap", "x.keymap", text, "sha", null),
            ZmkKeymapLoader.LoadFromText(text, "editor"),
            gitHub: null,
            new KeymapBackupStore(TempBackupsRoot()));

    private static string TempKeymapPath() =>
        Path.Combine(Path.GetTempPath(), $"lviz-edit-{Guid.NewGuid():N}.keymap");

    /// <summary>
    /// Fresh backups root; the store creates the folder only on the first
    /// backup, so a save-less test leaves nothing behind.
    /// </summary>
    private static string TempBackupsRoot() =>
        Path.Combine(Path.GetTempPath(), $"lviz-edit-backups-{Guid.NewGuid():N}");

    /// <summary>Removes a save test's temp keymap file or backups folder, if present.</summary>
    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>KeyBinding's record equality ignores the contents of Params —
    /// assert behavior and params separately.</summary>
    private static void AssertBinding(KeyBinding actual, string behavior, params string[] parameters)
    {
        Assert.Equal(behavior, actual.Behavior);
        Assert.Equal(parameters, actual.Params);
    }

    // ---- a fresh view-model ---------------------------------------------------------

    [Fact]
    public void New_HasNoChanges_AndNothingSelected()
    {
        var vm = NewVm();

        Assert.Equal(0, vm.ChangeCount);
        Assert.False(vm.CanUndo);
        Assert.Equal(Loc.Instance["Edit_BannerNone"], vm.BannerText);
        Assert.Null(vm.SelectedKeyIndex);
    }

    [Fact]
    public void New_Defines_CarryTheBaselineFilesDefines()
    {
        const string text = """
            #define NAV 1

            / {
                keymap {
                    compatible = "zmk,keymap";
                    base { display-name = "Base"; bindings = <&kp A>; };
                };
            };
            """;

        var vm = new KeymapEditViewModel(
            new KeymapEditSource("d.keymap", "d.keymap", text, "sha", null),
            ZmkKeymapLoader.LoadFromText(text, "editor"),
            gitHub: null,
            new KeymapBackupStore(TempBackupsRoot()));

        Assert.Contains(new KeyValuePair<string, string>("NAV", "1"), vm.Defines);
    }

    // ---- clearing keys --------------------------------------------------------------

    [Fact]
    public void ClearKey_ToTrans_WritesTrans_AndRaisesChangedOnce()
    {
        var vm = NewVm();
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        Assert.True(vm.ClearKey(0, 1, toNone: false));

        Assert.Equal("&trans", vm.Keymap.Config.Layers[0].Bindings[1].Behavior);
        Assert.True(vm.IsKeyChanged(0, 1));
        Assert.Equal(1, vm.ChangeCount);
        Assert.Equal(Loc.Instance["Edit_BannerOne"], vm.BannerText);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ClearKey_ToNone_WritesNone_AndRepeatingItIsSilent()
    {
        var vm = NewVm();
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        Assert.True(vm.ClearKey(0, 1, toNone: true));
        Assert.Equal("&none", vm.Keymap.Config.Layers[0].Bindings[1].Behavior);

        // The key already holds &none: no step, no event.
        Assert.False(vm.ClearKey(0, 1, toNone: true));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ClearKey_TwoKeys_BannerFormatsTheCount()
    {
        var vm = NewVm();

        Assert.True(vm.ClearKey(0, 0, toNone: false));
        Assert.True(vm.ClearKey(1, 2, toNone: false));

        Assert.Equal(Loc.Instance.Format("Edit_BannerFormat", 2), vm.BannerText);
    }

    // ---- undo / redo ----------------------------------------------------------------

    [Fact]
    public void UndoRedo_CommandsTrackTheSession()
    {
        var vm = NewVm();
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.ClearKey(0, 0, toNone: false);
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.UndoCommand.Execute(null);
        Assert.Equal(0, vm.ChangeCount);
        Assert.True(vm.RedoCommand.CanExecute(null));

        vm.RedoCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
    }

    // ---- out-of-range input -----------------------------------------------------------

    [Fact]
    public void ClearKey_OutOfRange_ReturnsFalse_AndChangesNothing()
    {
        var vm = NewVm();

        Assert.False(vm.ClearKey(-1, 0, toNone: false));
        Assert.False(vm.ClearKey(2, 0, toNone: false));
        Assert.False(vm.ClearKey(0, 3, toNone: false));

        Assert.Equal(0, vm.ChangeCount);
    }

    // ---- setting keys (the key dialog's Confirm) ------------------------------------

    [Fact]
    public void SetKey_WritesBinding_AsOneUndoStep()
    {
        var vm = NewVm();

        Assert.True(vm.SetKey(0, 0, new KeyBinding("&kp", ["B"])));

        var binding = vm.Keymap.Config.Layers[0].Bindings[0];
        Assert.Equal("&kp", binding.Behavior);
        Assert.Equal("B", binding.Params[0]);
        Assert.Equal(1, vm.ChangeCount);

        vm.UndoCommand.Execute(null);

        Assert.Equal(0, vm.ChangeCount);
        Assert.Equal("A", vm.Keymap.Config.Layers[0].Bindings[0].Params[0]);
    }

    [Fact]
    public void SetKey_SameBinding_ReturnsFalse_AndAddsNoUndoStep()
    {
        var vm = NewVm();

        // Layer 0 key 1 already holds &kp B.
        Assert.False(vm.SetKey(0, 1, new KeyBinding("&kp", ["B"])));

        Assert.False(vm.CanUndo);
        Assert.Equal(0, vm.ChangeCount);
    }

    [Fact]
    public void SetKey_OutOfRange_ReturnsFalse_AndChangesNothing()
    {
        var vm = NewVm();

        Assert.False(vm.SetKey(-1, 0, KeyBinding.None));
        Assert.False(vm.SetKey(2, 0, KeyBinding.None));
        Assert.False(vm.SetKey(0, 3, KeyBinding.None));

        Assert.Equal(0, vm.ChangeCount);
    }

    [Fact]
    public void SetKey_NullBinding_Throws()
    {
        var vm = NewVm();

        Assert.Throws<ArgumentNullException>(() => vm.SetKey(0, 0, null!));
    }

    // ---- selection --------------------------------------------------------------------

    [Fact]
    public void SelectKey_AndClearSelection_TrackSelectedKeyIndex()
    {
        var vm = NewVm();

        vm.SelectKey(4);
        Assert.Equal(4, vm.SelectedKeyIndex);

        vm.SelectKey(-1);
        Assert.Null(vm.SelectedKeyIndex);

        vm.SelectKey(2);
        vm.ClearSelection();
        Assert.Null(vm.SelectedKeyIndex);
    }

    // ---- adding and duplicating layers ------------------------------------------------

    [Fact]
    public void AddLayer_AppendsTransLayer_AndUndoesAsOneStep()
    {
        var vm = NewVm();

        Assert.Equal(2, vm.AddLayer());

        var added = vm.Session.Current.Layers[2];
        Assert.Equal("Layer 2", added.Name);
        Assert.Equal(3, added.Bindings.Count);
        Assert.All(added.Bindings, b => Assert.Equal("&trans", b.Behavior));
        Assert.Equal(1, vm.ChangeCount);

        vm.UndoCommand.Execute(null);

        Assert.Equal(2, vm.Session.Current.Layers.Count);
    }

    [Fact]
    public void AddLayer_NameTaken_AppendsSuffix()
    {
        var vm = NewVm();
        Assert.Equal(2, vm.AddLayer()); // "Layer 2"
        Assert.True(vm.RenameLayer(0, "Layer 3")); // a layer is now named "Layer 3"

        Assert.Equal(3, vm.AddLayer());

        Assert.Equal("Layer 3 2", vm.Session.Current.Layers[3].Name);
    }

    [Fact]
    public void DuplicateLayer_InsertsAfterOriginal_WithCopiedBindings()
    {
        var vm = NewVm();

        Assert.Equal(2, vm.DuplicateLayer(1));

        var copy = vm.Session.Current.Layers[2];
        Assert.Equal("Lower copy", copy.Name);
        AssertBinding(copy.Bindings[0], "&kp", "X");
        AssertBinding(copy.Bindings[1], "&kp", "Y");
        AssertBinding(copy.Bindings[2], "&kp", "Z");
    }

    [Fact]
    public void DuplicateLayer_Twice_MakesUniqueCopyNames()
    {
        var vm = NewVm();
        Assert.Equal(2, vm.DuplicateLayer(1)); // "Lower copy"

        Assert.Equal(2, vm.DuplicateLayer(1));

        Assert.Equal("Lower copy 2", vm.Session.Current.Layers[2].Name);
    }

    [Fact]
    public void DuplicateLayer_OutOfRange_ReturnsNull_AndChangesNothing()
    {
        var vm = NewVm();

        Assert.Null(vm.DuplicateLayer(5));
        Assert.Null(vm.DuplicateLayer(-1));

        Assert.Equal(2, vm.Session.Current.Layers.Count);
        Assert.Equal(0, vm.ChangeCount);
    }

    // ---- moving and renaming layers ----------------------------------------------------

    [Fact]
    public void MoveLayer_Reorders_AndRefusesNoOpAndOutOfRange()
    {
        var vm = NewVm();

        Assert.True(vm.MoveLayer(0, 1));
        Assert.Equal(new[] { "Lower", "Base" }, vm.Session.Current.Layers.Select(l => l.Name).ToArray());

        Assert.False(vm.MoveLayer(0, 0));
        Assert.False(vm.MoveLayer(0, 9));
        Assert.Equal(1, vm.ChangeCount); // the refused calls added nothing
    }

    [Fact]
    public void MoveLayer_RenumberLayerReferences()
    {
        var vm = NewVm(ComboKeymapText);

        Assert.True(vm.MoveLayer(2, 0));

        Assert.Equal(new[] { "Raise", "Base", "Lower" }, vm.Session.Current.Layers.Select(l => l.Name).ToArray());
        // Base's "&mo 2" followed Raise to its new index 0.
        AssertBinding(vm.Session.Current.Layers[1].Bindings[2], "&mo", "0");
    }

    [Fact]
    public void RenameLayer_Trims_AndRefusesInvalidNames()
    {
        var vm = NewVm();

        Assert.True(vm.RenameLayer(1, "  Nav  "));
        Assert.Equal("Nav", vm.Session.Current.Layers[1].Name);

        Assert.False(vm.RenameLayer(1, "   ")); // blank
        Assert.False(vm.RenameLayer(1, "a\tb")); // control character
        Assert.False(vm.RenameLayer(1, "Nav")); // unchanged
        Assert.False(vm.RenameLayer(7, "x")); // out of range

        Assert.Equal("Nav", vm.Session.Current.Layers[1].Name);
        Assert.Equal(1, vm.ChangeCount);
    }

    // ---- deleting layers ---------------------------------------------------------------

    [Fact]
    public void CheckDelete_BlockedByCombo_DeleteRefused()
    {
        var vm = NewVm(ComboKeymapText);

        var check = vm.CheckDeleteLayer(2);
        Assert.NotNull(check);
        Assert.False(check.CanDelete);
        Assert.Equal(LayerDeleteBlockerKind.Combo, Assert.Single(check.Blockers).Kind);

        Assert.False(vm.DeleteLayer(2));

        Assert.Equal(3, vm.Session.Current.Layers.Count);
    }

    [Fact]
    public void DeleteLayer_Allowed_RenumberRemainingLayers_AndTheirReferences()
    {
        var vm = NewVm(ComboKeymapText);

        var check = vm.CheckDeleteLayer(1);
        Assert.NotNull(check);
        Assert.True(check.CanDelete);
        Assert.Equal(0, check.ClearedBindings); // combo and key point at Raise (2), not Lower

        Assert.True(vm.DeleteLayer(1));

        Assert.Equal(new[] { "Base", "Raise" }, vm.Session.Current.Layers.Select(l => l.Name).ToArray());
        // Raise slid from index 2 to 1, and Base's "&mo 2" with it.
        AssertBinding(vm.Session.Current.Layers[0].Bindings[2], "&mo", "1");
    }

    [Fact]
    public void DeleteLayer_CountsAndClearsReferencingKeys_AndUndoesAsOneStep()
    {
        var vm = NewVm(NoComboKeymapText);

        var check = vm.CheckDeleteLayer(2);
        Assert.NotNull(check);
        Assert.True(check.CanDelete);
        Assert.Equal(1, check.ClearedBindings); // Base's key 2 targets Raise

        Assert.True(vm.DeleteLayer(2));

        AssertBinding(vm.Session.Current.Layers[0].Bindings[2], "&none");

        vm.UndoCommand.Execute(null);

        Assert.Equal(3, vm.Session.Current.Layers.Count);
        AssertBinding(vm.Session.Current.Layers[0].Bindings[2], "&mo", "2");
    }

    [Fact]
    public void SavePlan_AfterAMixedHistory_ListsOnlyTheNetEdits_AndWritesTheEditedKeymap()
    {
        var vm = NewVm(NoComboKeymapText);       // Base, Lower, Raise
        Assert.Equal(2, vm.DuplicateLayer(1));     // Base, Lower, Lower copy, Raise
        Assert.True(vm.MoveLayer(0, 3));         // Lower, Lower copy, Raise, Base
        Assert.True(vm.MoveLayer(3, 0));         // back: Base, Lower, Lower copy, Raise
        Assert.True(vm.DeleteLayer(3));          // Base, Lower, Lower copy
        Assert.True(vm.RenameLayer(2, "Nav"));
        Assert.True(vm.SetKey(2, 0, new KeyBinding("&kp", ["Q"])));

        var plan = vm.BuildSavePlan();

        Assert.Equal(2, plan.StructureEdits.Count);
        Assert.Equal(new LayerStructureEdit.Delete(2), plan.StructureEdits[0]);
        var add = Assert.IsType<LayerStructureEdit.Add>(plan.StructureEdits[1]);
        Assert.Equal((2, "Nav"), (add.AtIndex, add.Name));
        Assert.Empty(plan.Renames);

        var written = ZmkKeymapLoader.LoadFromText(plan.NewSource, "test");
        var current = vm.Session.Current;
        Assert.Equal(current.Layers.Select(l => l.Name), written.Layers.Select(l => l.Name));
        for (int i = 0; i < current.Layers.Count; i++)
            Assert.Equal(
                current.Layers[i].Bindings.Select(b => b.Display),
                written.Layers[i].Bindings.Select(b => b.Display));
    }

    [Fact]
    public void CheckDelete_HonoursPendingStructureEdits()
    {
        var vm = NewVm(ComboKeymapText);
        Assert.True(vm.MoveLayer(2, 0)); // the layers are now Raise, Base, Lower

        // The combo still targets the moved layer, which now sits at index 0.
        var blocked = vm.CheckDeleteLayer(0);
        Assert.NotNull(blocked);
        Assert.False(blocked.CanDelete);
        Assert.Equal(LayerDeleteBlockerKind.Combo, Assert.Single(blocked.Blockers).Kind);

        // Lower slid into index 2 and nothing references it.
        var allowed = vm.CheckDeleteLayer(2);
        Assert.NotNull(allowed);
        Assert.True(allowed.CanDelete);
    }

    [Fact]
    public void CheckDelete_OutOfRange_ReturnsNull()
    {
        var vm = NewVm();

        Assert.Null(vm.CheckDeleteLayer(-1));
        Assert.Null(vm.CheckDeleteLayer(2));
    }

    // ---- combo edits ----------------------------------------------------------------

    /// <summary>Slabwing: 7 layers, 4 combos, and its <c>syslayer</c> combo
    /// binds <c>&amp;mo 2</c>.</summary>
    private static KeymapEditViewModel NewSlabwingVm() => NewVm(KeymapFixtures.Read("slabwing.keymap"));

    private static ZmkCombo Combo(string name, string binding, params string[] parameters) =>
        new(name, "", [10, 11], [-1], new KeyBinding(binding, parameters)) { NodeName = name };

    [Fact]
    public void AddCombo_TargetingALayer_BlocksDeletingThatLayer_AndDeleteRefuses()
    {
        var vm = NewSlabwingVm();

        vm.AddCombo(Combo("extra", "&mo", "5"));

        var check = vm.CheckDeleteLayer(5);
        Assert.NotNull(check);
        Assert.False(check.CanDelete);
        var blocker = Assert.Single(check.Blockers, b => b.Kind == LayerDeleteBlockerKind.Combo);
        Assert.Equal("extra", blocker.Name);
        Assert.Equal(0, blocker.Line);

        Assert.False(vm.DeleteLayer(5)); // refused, not thrown
        Assert.Equal(7, vm.Session.Current.Layers.Count);
    }

    [Fact]
    public void CheckDelete_TextAndModelSeeTheSameCombo_BlocksExactlyOnce()
    {
        var vm = NewSlabwingVm();

        var check = vm.CheckDeleteLayer(2);
        Assert.NotNull(check);
        Assert.False(check.CanDelete);
        var blocker = Assert.Single(check.Blockers, b => b.Kind == LayerDeleteBlockerKind.Combo);
        Assert.Equal("syslayer", blocker.Name);
    }

    [Fact]
    public void AddCombo_RaisesChanged_AndPendingFlagClearsOnUndo()
    {
        var vm = NewVm();
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        Assert.Equal(0, vm.AddCombo(Combo("c", "&kp", "X")));

        Assert.Equal(1, raised);
        Assert.Equal(1, vm.ChangeCount);
        Assert.True(vm.HasPendingComboEdits);

        vm.Session.Undo();

        Assert.Equal(0, vm.ChangeCount);
        Assert.False(vm.HasPendingComboEdits);
    }

    [Fact]
    public void SetCombo_AndDeleteCombo_WrapTheSession_AndIsComboChangedTracksIt()
    {
        var vm = NewVm();
        var index = vm.AddCombo(Combo("c", "&kp", "X"));
        Assert.True(vm.IsComboChanged(index));

        // The same combo: false, no second step.
        Assert.False(vm.SetCombo(index, Combo("c", "&kp", "X")));
        Assert.Equal(1, vm.ChangeCount);

        Assert.True(vm.SetCombo(index, Combo("c", "&kp", "Y")));
        // The count is the net diff against the baseline, not the undo steps:
        // still the one added combo.
        Assert.Equal(1, vm.ChangeCount);

        vm.DeleteCombo(index);
        Assert.Empty(vm.Session.Current.Combos);
        // The add and the delete of that added combo net out.
        Assert.Equal(0, vm.ChangeCount);
        Assert.False(vm.HasPendingComboEdits);
    }

    // ---- Start on a real file -----------------------------------------------------------

    [Fact]
    public void Start_LocalFile_KeepsExactTextShaAndFileName()
    {
        var path = TempKeymapPath();
        try
        {
            File.WriteAllText(path, KeymapText);

            var vm = KeymapEditViewModel.Start(path, path, gitHub: null, new KeymapBackupStore(TempBackupsRoot()));

            Assert.Equal(File.ReadAllText(path), vm.Source.BaselineText);
            Assert.Equal(GitBlobSha.Compute(File.ReadAllBytes(path)), vm.Source.BaseBlobSha);
            Assert.False(vm.Source.IsGitHub);
            Assert.Equal(Path.GetFileName(path), vm.Source.TargetName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Start_GitHubEntry_ParsesRef_NamesTheRepo()
    {
        var path = TempKeymapPath();
        try
        {
            File.WriteAllText(path, KeymapText);

            var vm = KeymapEditViewModel.Start(path, "github:o/r:main:config/x.keymap", gitHub: null, new KeymapBackupStore(TempBackupsRoot()));

            Assert.True(vm.Source.IsGitHub);
            Assert.Equal("o/r (main)", vm.Source.TargetName);
            Assert.Equal("config/x.keymap", vm.Source.GitHub!.Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Start_LeadingBom_IsStrippedFromBaselineText()
    {
        var path = TempKeymapPath();
        try
        {
            var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
            bytes.AddRange(Encoding.UTF8.GetBytes(KeymapText));
            File.WriteAllBytes(path, bytes.ToArray());

            var vm = KeymapEditViewModel.Start(path, path, gitHub: null, new KeymapBackupStore(TempBackupsRoot()));

            Assert.False(vm.Source.BaselineText.StartsWith('\uFEFF'));
            Assert.Equal(KeymapText, vm.Source.BaselineText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Start_UnparsableFile_ThrowsKeymapParseException()
    {
        var path = TempKeymapPath();
        try
        {
            File.WriteAllText(path, "this is not a keymap");

            Assert.Throws<ZmkKeymapParseException>(
                () => KeymapEditViewModel.Start(path, path, gitHub: null, new KeymapBackupStore(TempBackupsRoot())));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- saving the local file ----------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Local_WritesPlanText_AndBacksUpPreviousContent()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(path, path, gitHub: null, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            var outcome = await vm.SaveAsync(plan, overwrite: false);

            Assert.Equal(new KeymapEditSaveOutcome.Saved(path), outcome);
            Assert.Equal(plan.NewSource, File.ReadAllText(path));
            Assert.Single(backups.List(path));

            // Saving never touches the session.
            Assert.Equal(1, vm.ChangeCount);
            Assert.True(vm.HasChanges);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_Local_FileChangedOnDisk_Conflicts_UntilOverwrite()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(path, path, gitHub: null, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            File.WriteAllText(path, KeymapText + "\n// touched by someone else\n");
            var onDisk = File.ReadAllText(path);

            Assert.IsType<KeymapEditSaveOutcome.Conflict>(await vm.SaveAsync(plan, overwrite: false));
            Assert.Equal(onDisk, File.ReadAllText(path));

            var outcome = await vm.SaveAsync(plan, overwrite: true);
            Assert.Equal(new KeymapEditSaveOutcome.Saved(path), outcome);
            Assert.Equal(plan.NewSource, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    // ---- saving to GitHub -----------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_GitHub_CommitsPlan_AndReloadsCacheCopy()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        GitHubKeymapRef? savedRef = null;
        byte[]? savedContent = null;
        string? savedSha = null;
        string? savedMessage = null;
        bool? savedOverwrite = null;
        var fake = new FakeGitHubKeymapService
        {
            OnLocalPathFor = _ => "/cache/x.keymap",
            OnSave = (keymap, content, baseBlobSha, message, overwrite) =>
            {
                savedRef = keymap;
                savedContent = content;
                savedSha = baseBlobSha;
                savedMessage = message;
                savedOverwrite = overwrite;
                return Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Saved("abc"));
            },
        };
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", fake, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            var outcome = await vm.SaveAsync(plan, overwrite: false);

            Assert.Equal(new KeymapEditSaveOutcome.Saved("/cache/x.keymap"), outcome);
            Assert.Equal(vm.Source.GitHub, savedRef);
            Assert.Equal(new UTF8Encoding(false).GetBytes(plan.NewSource), savedContent);
            Assert.False(savedContent!.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(vm.Source.BaseBlobSha, savedSha);
            Assert.Equal(KeymapCommitMessage.Build(plan), savedMessage);
            Assert.False(savedOverwrite!.Value);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_GitHub_AnEditedCommitMessage_IsTheOneCommitted()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        string? savedMessage = null;
        var fake = new FakeGitHubKeymapService
        {
            OnLocalPathFor = _ => "/cache/x.keymap",
            OnSave = (_, _, _, message, _) =>
            {
                savedMessage = message;
                return Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Saved("abc"));
            },
        };
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", fake, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));

            await vm.SaveAsync(vm.BuildSavePlan(), overwrite: false, commitMessage: "Swap the B key");

            Assert.Equal("Swap the B key", savedMessage);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_GitHub_CacheCopyMissing_ReloadsLocalPath()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        var fake = new FakeGitHubKeymapService
        {
            // OnLocalPathFor defaults to null: the commit left no cache copy.
            OnSave = (_, _, _, _, _) =>
                Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Saved("abc")),
        };
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", fake, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            var outcome = await vm.SaveAsync(plan, overwrite: false);

            Assert.Equal(new KeymapEditSaveOutcome.Saved(path), outcome);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_GitHub_ConflictPassesThrough_AndOverwriteFlagReachesService()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        bool? lastOverwrite = null;
        var fake = new FakeGitHubKeymapService
        {
            OnSave = (_, _, _, _, overwrite) =>
            {
                lastOverwrite = overwrite;
                return Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Conflict());
            },
        };
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", fake, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            Assert.IsType<KeymapEditSaveOutcome.Conflict>(await vm.SaveAsync(plan, overwrite: false));
            Assert.False(lastOverwrite!.Value);

            Assert.IsType<KeymapEditSaveOutcome.Conflict>(await vm.SaveAsync(plan, overwrite: true));
            Assert.True(lastOverwrite!.Value);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_GitHub_Failed_MapsKindToTranslatedText()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        var fake = new FakeGitHubKeymapService
        {
            OnSave = (_, _, _, _, _) => Task.FromResult<GitHubSaveResult>(
                new GitHubSaveResult.Failed(GitHubErrorKind.Network, "x")),
        };
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", fake, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            var outcome = await vm.SaveAsync(plan, overwrite: false);

            var failed = Assert.IsType<KeymapEditSaveOutcome.Failed>(outcome);
            Assert.Equal(Loc.Instance["GitHub_ErrorNetwork"], failed.Message);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }

    [Fact]
    public async Task SaveAsync_GitHub_NoService_FailsWithUnavailableText()
    {
        var path = TempKeymapPath();
        var backups = new KeymapBackupStore(TempBackupsRoot());
        try
        {
            File.WriteAllText(path, KeymapText);
            var vm = KeymapEditViewModel.Start(
                path, "github:o/r:main:config/x.keymap", gitHub: null, backups);
            Assert.True(vm.ClearKey(0, 1, toNone: false));
            var plan = vm.BuildSavePlan();

            var outcome = await vm.SaveAsync(plan, overwrite: false);

            var failed = Assert.IsType<KeymapEditSaveOutcome.Failed>(outcome);
            Assert.Equal(Loc.Instance["Edit_SaveGitHubUnavailable"], failed.Message);
        }
        finally
        {
            DeleteIfPresent(path);
            DeleteIfPresent(backups.Root);
        }
    }
}
