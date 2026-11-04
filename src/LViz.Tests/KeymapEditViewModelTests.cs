using System.IO;
using System.Text;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Persistence;
using LViz.Tests.GitHub;
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

    private static KeymapEditViewModel NewVm() =>
        new(
            new KeymapEditSource("x.keymap", "x.keymap", KeymapText, "sha", null),
            ZmkKeymapLoader.LoadFromText(KeymapText, "editor"),
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
