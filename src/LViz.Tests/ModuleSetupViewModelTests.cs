using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using LViz.Core.ZmkConfig;
using LViz.Tests.GitHub;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The module setup wizard over the real zmk-config fixtures: what a local
/// config reports, when Next is allowed, and how the central and feature
/// defaults are pre-filled; both GitHub paths (fetch into the cache folder,
/// translated fetch failure) and the constructor's context guards; and the
/// review/save/done steps — the plan's diffs, the local and GitHub saves,
/// their conflict and failure paths, and the reload-after-conflict route.
/// </summary>
public class ModuleSetupViewModelTests
{
    private static readonly Guid FixedGuid = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");

    /// <summary>Nothing saves in this card, so the backup root is never created.</summary>
    private static KeymapBackupStore FreshBackups() =>
        new(Path.Combine(Path.GetTempPath(), $"lviz-modsetup-backups-{Guid.NewGuid():N}"));

    private static ModuleSetupViewModel LocalVm(string root) =>
        new(new ModuleSetupContext(root, null, _ => Task.FromResult<string?>(null)), gitHub: null, FreshBackups(), () => FixedGuid);

    [Fact]
    public async Task Cosmos82_CheckReportsTheFacts_ThenWalksChooseToReview()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();

            Assert.Equal(ModuleSetupStep.Check, vm.Step);
            Assert.True(vm.IsCheckStep);
            Assert.True(vm.CanSetUp);
            Assert.Null(vm.ErrorMessage);
            Assert.Null(vm.BlockerMessage);
            Assert.Null(vm.StatusMessage);
            Assert.False(vm.IsBusy);

            Assert.Equal(4, vm.FactLines.Count);
            Assert.Equal(Loc.Instance.Format("ModuleSetup_FactZmk", "zmkfirmware", "v0.3"), vm.FactLines[0]);
            Assert.Equal(Loc.Instance["ModuleSetup_FactSplit"], vm.FactLines[1]);
            // The right half is the central via its Kconfig; one shield, so no
            // space-join — the " · " between board and shields is still there.
            Assert.Equal(
                Loc.Instance.Format("ModuleSetup_FactCentral",
                    "nice_nano_v2 \u00b7 cosmos82_right",
                    Loc.Instance["ModuleSetup_ReasonRepoKconfig"]),
                vm.FactLines[2]);
            Assert.Equal(Loc.Instance["ModuleSetup_FactStatusNotSetUp"], vm.FactLines[3]);

            Assert.Equal(1, vm.SelectedCentral!.Index);
            Assert.True(vm.IsSplit);
            Assert.Null(vm.CentralHint);
            Assert.False(vm.ShowRgb);
            Assert.False(vm.Rgb);
            Assert.True(vm.Signal);
            Assert.False(vm.Emit);
            Assert.False(vm.ShowAdvanced);
            Assert.Equal(2, vm.CentralOptions.Count);
            Assert.True(vm.NextCommand.CanExecute(null));
            Assert.False(vm.BackCommand.CanExecute(null));

            vm.NextCommand.Execute(null);
            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
            Assert.True(vm.IsChooseStep);
            Assert.True(vm.NextCommand.CanExecute(null));

            vm.NextCommand.Execute(null);
            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.True(vm.BackCommand.CanExecute(null));

            vm.BackCommand.Execute(null);
            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cosmos82_Choose_SystemOffByDefault()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();

            // A config that is not set up has nothing locked and everything
            // to choose; flashing still starts off.
            Assert.False(vm.System);
            Assert.False(vm.RgbLocked);
            Assert.False(vm.SignalLocked);
            Assert.False(vm.EmitLocked);
            Assert.False(vm.SystemLocked);
            Assert.True(vm.CanChangeCentral);

            vm.NextCommand.Execute(null); // -> Choose
            vm.System = true;
            vm.NextCommand.Execute(null); // -> Review

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            var conf = vm.Files.Single(f => f.Path == "config/cosmos82.conf");
            Assert.Contains(conf.Lines, l => l.IsAdded && l.Text == "+ CONFIG_HID_VIZ_SYSTEM=y");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Glove80_SetUp_LocksPresentFeatures()
    {
        var root = ZmkConfigFixtures.CopyToTemp("glove80");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();

            // A set-up config is add-only: Check is passable, Choose gates.
            Assert.True(vm.CanSetUp);
            Assert.True(vm.NextCommand.CanExecute(null));
            Assert.Equal(Loc.Instance["ModuleSetup_AlreadySetUp"], vm.StatusMessage);
            Assert.Equal(Loc.Instance["ModuleSetup_FactStatusSetUp"], vm.FactLines[3]);
            // MoErgo boards are treated as having underglow, so the RGB
            // default rides along.
            Assert.True(vm.ShowRgb);
            Assert.True(vm.Rgb);
            Assert.Equal(
                Loc.Instance.Format("ModuleSetup_FactCentral",
                    "glove80_lh \u00b7 raw_hid_adapter",
                    Loc.Instance["ModuleSetup_ReasonLeftNaming"]),
                vm.FactLines[2]);
            Assert.Equal(Loc.Instance["ModuleSetup_CentralHintGuessed"], vm.CentralHint);

            vm.NextCommand.Execute(null); // -> Choose

            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
            // RGB, SIGNAL and EMIT are all y in the fixture: ticked and locked.
            Assert.True(vm.RgbLocked);
            Assert.True(vm.SignalLocked);
            Assert.True(vm.EmitLocked);
            Assert.True(vm.Rgb);
            Assert.True(vm.Signal);
            Assert.True(vm.Emit);
            // Flashing is not on yet: it is the only thing that can be added.
            Assert.False(vm.SystemLocked);
            Assert.False(vm.System);
            // The central is the raw_hid_adapter entry, and fixed to it.
            Assert.False(vm.CanChangeCentral);
            Assert.Equal(0, vm.SelectedCentral!.Index);
            // Nothing new is ticked: Next stays off.
            Assert.False(vm.NextCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Glove80_SetUp_TickSystem_ReviewsOneConfLine()
    {
        var root = ZmkConfigFixtures.CopyToTemp("glove80");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose

            Assert.False(vm.NextCommand.CanExecute(null));
            vm.System = true;
            Assert.True(vm.NextCommand.CanExecute(null));

            vm.NextCommand.Execute(null); // -> Review

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            // The west manifest and build matrix already have everything: only the conf changes.
            Assert.Equal(["config/glove80.conf"], vm.Files.Select(f => f.Path));
            Assert.Contains(
                vm.Files.Single().Lines,
                l => l.IsAdded && !l.IsRemoved && !l.IsGap && l.Text == "+ CONFIG_HID_VIZ_SYSTEM=y");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Glove80_SetUp_Save_UsesAddFeaturesMessage()
    {
        var root = ZmkConfigFixtures.CopyToTemp("glove80");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.System = true;
            vm.NextCommand.Execute(null); // -> Review

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(ModuleSetupStep.Done, vm.Step);
            Assert.True(vm.Saved);
            Assert.EndsWith(
                "CONFIG_HID_VIZ_SYSTEM=y\n",
                File.ReadAllText(Path.Combine(root, "config", "glove80.conf")));
            // The GitHub save of such a plan commits with this message.
            Assert.Equal("LViz: enable more live features", ModuleSetupViewModel.CommitMessageAddFeatures);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Zzeneg_OtherFork_ContinuesWithTheForkNotice()
    {
        var root = ZmkConfigFixtures.CopyToTemp("zzeneg");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();

            Assert.True(vm.CanSetUp);
            Assert.Equal(Loc.Instance["ModuleSetup_NeedsForkNotice"], vm.StatusMessage);
            Assert.Equal(Loc.Instance["ModuleSetup_FactStatusNeedsFork"], vm.FactLines[3]);
            Assert.True(vm.NextCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Dongle_IsBlockedWithItsText()
    {
        var root = ZmkConfigFixtures.CopyToTemp("dongle");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();

            Assert.Equal(Loc.Instance["ModuleSetup_Blocker_Dongle"], vm.BlockerMessage);
            Assert.False(vm.CanSetUp);
            Assert.False(vm.NextCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TwoBlockers_JoinOverNewlines()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            File.Delete(Path.Combine(root, "config", "west.yml"));
            File.Delete(Path.Combine(root, "build.yaml"));

            var vm = LocalVm(root);
            await vm.StartAsync();

            Assert.Equal(
                Loc.Instance["ModuleSetup_Blocker_NoWestManifest"]
                + "\n"
                + Loc.Instance["ModuleSetup_Blocker_NoBuildMatrix"],
                vm.BlockerMessage);
            Assert.False(vm.CanSetUp);
            Assert.False(vm.NextCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnknownCentral_RequiresAPickBeforeReview()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            // No shield Kconfig, and no _left/_lh name left: the central is nobody's guess.
            Directory.Delete(Path.Combine(root, "boards"), recursive: true);
            var matrix = File.ReadAllText(Path.Combine(root, "build.yaml"));
            File.WriteAllText(
                Path.Combine(root, "build.yaml"),
                matrix.Replace("cosmos82_left", "cosmos82_a").Replace("cosmos82_right", "cosmos82_b"));

            var vm = LocalVm(root);
            await vm.StartAsync();

            Assert.True(vm.CanSetUp);
            Assert.Equal(Loc.Instance["ModuleSetup_FactCentralUnknown"], vm.FactLines[2]);
            Assert.Equal(Loc.Instance["ModuleSetup_CentralHintPick"], vm.CentralHint);
            Assert.Null(vm.SelectedCentral);

            vm.NextCommand.Execute(null); // the check step is still passable
            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
            Assert.False(vm.NextCommand.CanExecute(null));

            vm.SelectedCentral = vm.CentralOptions.Single(o => o.Index == 1);
            Assert.True(vm.NextCommand.CanExecute(null));
            vm.NextCommand.Execute(null);
            Assert.Equal(ModuleSetupStep.Review, vm.Step);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GitHub_FetchesTheBranch_AndReadsTheCachedFolder()
    {
        var folder = ZmkConfigFixtures.CopyToTemp("slabwing");
        try
        {
            string? fetched = null;
            var gitHub = new FakeGitHubKeymapService
            {
                OnFetchRepo = (owner, repo, branch) =>
                {
                    fetched = $"{owner}/{repo}/{branch}";
                    return Task.FromResult(new GitHubCacheResult(folder, "abc", Downloaded: true));
                },
            };
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"), _ => Task.FromResult<string?>(null)),
                gitHub,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();

            Assert.Equal("od/my-config/main", fetched);
            Assert.Contains(nameof(FakeGitHubKeymapService.FetchRepoAsync), gitHub.Calls);
            Assert.Null(vm.ErrorMessage);
            Assert.True(vm.CanSetUp);
            // The slabwing facts: unibody, its only entry is the central;
            // its three shields join with spaces after the board.
            Assert.Equal(Loc.Instance["ModuleSetup_FactUnibody"], vm.FactLines[1]);
            Assert.Equal(
                Loc.Instance.Format("ModuleSetup_FactCentral",
                    "nice_nano_v2 \u00b7 slabwing nice_view_adapter nice_view",
                    Loc.Instance["ModuleSetup_ReasonUnibody"]),
                vm.FactLines[2]);
            Assert.Equal(0, vm.SelectedCentral!.Index);
            Assert.Equal("nice_nano_v2 \u00b7 slabwing nice_view_adapter nice_view", vm.CentralOptions[0].Text);
            Assert.False(vm.IsSplit);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task GitHub_FetchFails_ShowsTheTranslatedErrorAndStops()
    {
        var gitHub = new FakeGitHubKeymapService
        {
            OnFetchRepo = (_, _, _) => throw new GitHubApiException(GitHubErrorKind.Network, "no route"),
        };
        var vm = new ModuleSetupViewModel(
            new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"), _ => Task.FromResult<string?>(null)),
            gitHub,
            FreshBackups(),
            () => FixedGuid);
        await vm.StartAsync();

        Assert.Equal(
            GitHubOpenViewModel.MessageFor(new GitHubApiException(GitHubErrorKind.Network, "no route")),
            vm.ErrorMessage);
        Assert.False(vm.CanSetUp);
        Assert.Empty(vm.FactLines);
        Assert.Equal(ModuleSetupStep.Check, vm.Step);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task GitHubFetch_DiskError_ShowsReadError()
    {
        // Unpacking the branch tarball can fail on the disk, not the API;
        // that lands in the same read-error text, not an unobserved task.
        var gitHub = new FakeGitHubKeymapService
        {
            OnFetchRepo = (_, _, _) => throw new IOException("disk full"),
        };
        var vm = new ModuleSetupViewModel(
            new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"),
                _ => Task.FromResult<string?>(null)),
            gitHub,
            FreshBackups(),
            () => FixedGuid);
        await vm.StartAsync();

        Assert.Equal(Loc.Instance.Format("ModuleSetup_ErrorRead", "disk full"), vm.ErrorMessage);
        Assert.False(vm.CanSetUp);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Ctor_RejectsMissingOrDoubleContextAndGitHubWithoutService()
    {
        Assert.Throws<ArgumentException>(() => new ModuleSetupViewModel(
            new ModuleSetupContext(null, null, _ => Task.FromResult<string?>(null)), gitHub: null, FreshBackups(), () => FixedGuid));

        var target = new ModuleSetupGitHubTarget("od", "my-config", "main");
        Assert.Throws<ArgumentException>(() => new ModuleSetupViewModel(
            new ModuleSetupContext("/tmp/whatever", target, _ => Task.FromResult<string?>(null)),
            new FakeGitHubKeymapService(), FreshBackups(), () => FixedGuid));

        Assert.Throws<ArgumentException>(() => new ModuleSetupViewModel(
            new ModuleSetupContext(null, target, _ => Task.FromResult<string?>(null)), gitHub: null, FreshBackups(), () => FixedGuid));
    }

    [Fact]
    public void Close_RaisesCloseRequested_WithSavedStillFalse()
    {
        var vm = LocalVm(ZmkConfigFixtures.Root("cosmos82"));
        bool? closed = null;
        vm.CloseRequested += saved => closed = saved;

        vm.CloseCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.False(closed ?? true);
    }

    /// <summary>
    /// The reason, status and blocker texts are built as interpolated keys, so
    /// <c>LocalizationKeyTests</c> cannot see them — resolve every member of
    /// each family and fail on the <c>[Key]</c> placeholder a missing key gives.
    /// </summary>
    [Fact]
    public void EveryInterpolatedReasonStatusAndBlockerKey_Resolves()
    {
        var loc = Loc.Instance;
        foreach (var reason in new[]
                 {
                     ZmkCentralReason.RepoKconfig,
                     ZmkCentralReason.LeftNaming,
                     ZmkCentralReason.Unibody,
                 })
            AssertResolves(loc, $"ModuleSetup_Reason{reason}");
        foreach (var status in Enum.GetValues<ZmkModuleStatus>())
            AssertResolves(loc, $"ModuleSetup_FactStatus{status}");
        foreach (var blocker in Enum.GetValues<ZmkConfigBlocker>())
            AssertResolves(loc, $"ModuleSetup_Blocker_{blocker}");
    }

    private static void AssertResolves(Loc loc, string key)
    {
        var text = loc[key];
        Assert.DoesNotContain($"[{key}]", text);
        Assert.NotEqual(string.Empty, text);
    }

    // ---------- review, save and done ----------

    [Fact]
    public async Task Cosmos82_Review_ShowsThePlanDiffs()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.NextCommand.Execute(null); // -> Review

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.True(vm.IsReviewStep);
            Assert.Equal(
                ["config/west.yml", "build.yaml", "config/cosmos82.conf"],
                vm.Files.Select(f => f.Path));
            Assert.All(vm.Files, f => Assert.False(f.IsNew));

            var conf = vm.Files.Single(f => f.Path == "config/cosmos82.conf");
            Assert.Contains(conf.Lines, l => l.IsAdded && !l.IsRemoved && !l.IsGap && l.Text == "+ CONFIG_RAW_HID=y");
            Assert.Contains(conf.Lines, l => !l.IsAdded && !l.IsRemoved && !l.IsGap && l.Text.StartsWith("  "));
            Assert.All(conf.Lines, l => Assert.True(l.IsGap == (l.Text == "\u2026")));

            // zmkfirmware v0.3 is a tested pairing, so no revision warning.
            Assert.Null(vm.RevisionWarning);
            Assert.Null(vm.ForkNotice);
            Assert.True(vm.SaveCommand.CanExecute(null));

            vm.BackCommand.Execute(null);
            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
            Assert.Empty(vm.Files);
            Assert.False(vm.SaveCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnknownRevision_Review_Warns()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            // The defaults revision the zmk project inherits; the pmw3610
            // driver keeps its own zmk-0.3 revision.
            var west = Path.Combine(root, "config", "west.yml");
            File.WriteAllText(west,
                File.ReadAllText(west).Replace("    revision: v0.3", "    revision: v0.2"));

            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.NextCommand.Execute(null); // -> Review

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.Equal(
                Loc.Instance.Format("ModuleSetup_RevisionUnknown", "zmkfirmware", "v0.2"),
                vm.RevisionWarning);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cosmos82_Save_WritesThePlanToTheFolder_AndLandsOnDone()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(ModuleSetupStep.Done, vm.Step);
            Assert.True(vm.IsDoneStep);
            Assert.True(vm.Saved);
            Assert.False(vm.IsBusy);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.Contains(
                "cosmos82_right raw_hid_adapter",
                File.ReadAllText(Path.Combine(root, "build.yaml")));
            Assert.Contains(
                "CONFIG_RAW_HID=y",
                File.ReadAllText(Path.Combine(root, "config", "cosmos82.conf")));
            Assert.Null(vm.ActionsUrl);
            Assert.False(vm.IsGitHub);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cosmos82_SaveAfterDiskEdit_ConflictsWithReload_AndReloadRechecks()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);

            var west = Path.Combine(root, "config", "west.yml");
            File.AppendAllText(west, "# touched by hand\n");

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.False(vm.Saved);
            Assert.Equal(Loc.Instance["ModuleSetup_ConflictLocal"], vm.ErrorMessage);
            Assert.True(vm.ShowReload);
            // No overwrite: the hand-edited file is exactly as it was.
            Assert.EndsWith("# touched by hand\n", File.ReadAllText(west));

            await vm.ReloadCommand.ExecuteAsync(null);

            Assert.False(vm.ShowReload);
            Assert.Equal(ModuleSetupStep.Check, vm.Step);
            Assert.True(vm.CanSetUp);
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Glove80_ForceReview_ReportsThePlanRefusal_AndStaysOnChoose()
    {
        // Next is disabled here (already set up); Execute ignores CanExecute,
        // so this drives the plan build the way a stale button press could.
        var root = ZmkConfigFixtures.CopyToTemp("glove80");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.NextCommand.Execute(null); // Review: the plan refuses

            Assert.Equal(ModuleSetupStep.Choose, vm.Step);
            Assert.NotNull(vm.ErrorMessage);
            Assert.StartsWith(
                Loc.Instance.Format("ModuleSetup_ErrorPlan", "").TrimEnd(),
                vm.ErrorMessage);
            Assert.False(vm.IsReviewStep);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GitHub_Save_CommitsAllFilesInOneCall_WithTheFetchedBaseSha()
    {
        var folder = ZmkConfigFixtures.CopyToTemp("slabwing");
        try
        {
            string? savedOwner = null, savedRepo = null, savedBranch = null, savedSha = null, savedMessage = null;
            IReadOnlyList<GitHubFileWrite>? savedFiles = null;
            var gitHub = new FakeGitHubKeymapService
            {
                OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "abc", Downloaded: true)),
                OnSaveFiles = (owner, repo, branch, baseSha, files, message) =>
                {
                    (savedOwner, savedRepo, savedBranch, savedSha, savedFiles, savedMessage) =
                        (owner, repo, branch, baseSha, files, message);
                    return Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Saved("def"));
                },
            };
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"), _ => Task.FromResult<string?>(null)),
                gitHub,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);

            string? opened = null;
            vm.OpenUrl = url => opened = url;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("od", savedOwner);
            Assert.Equal("my-config", savedRepo);
            Assert.Equal("main", savedBranch);
            Assert.Equal("abc", savedSha);
            Assert.Equal(
                ["config/west.yml", "build.yaml", "config/slabwing.conf"],
                savedFiles!.Select(f => f.Path));
            Assert.Equal(ModuleSetupViewModel.CommitMessage, savedMessage);
            Assert.Equal(ModuleSetupStep.Done, vm.Step);
            Assert.True(vm.Saved);
            Assert.True(vm.IsGitHub);
            Assert.Equal("https://github.com/od/my-config/actions", vm.ActionsUrl);

            vm.OpenActionsCommand.Execute(null);
            Assert.Equal("https://github.com/od/my-config/actions", opened);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task GitHub_SaveConflict_OffersReload_AndKeepsReview()
    {
        var folder = ZmkConfigFixtures.CopyToTemp("slabwing");
        try
        {
            var gitHub = new FakeGitHubKeymapService
            {
                OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "abc", Downloaded: true)),
                OnSaveFiles = (_, _, _, _, _, _) =>
                    Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Conflict()),
            };
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"), _ => Task.FromResult<string?>(null)),
                gitHub,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.False(vm.Saved);
            Assert.Equal(Loc.Instance["ModuleSetup_ConflictGitHub"], vm.ErrorMessage);
            Assert.True(vm.ShowReload);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task GitHub_SaveFails_ShowsTheTranslatedError_WithoutReload()
    {
        var folder = ZmkConfigFixtures.CopyToTemp("slabwing");
        try
        {
            var gitHub = new FakeGitHubKeymapService
            {
                OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "abc", Downloaded: true)),
                OnSaveFiles = (_, _, _, _, _, _) =>
                    Task.FromResult<GitHubSaveResult>(new GitHubSaveResult.Failed(GitHubErrorKind.Network, "x")),
            };
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(null, new ModuleSetupGitHubTarget("od", "my-config", "main"), _ => Task.FromResult<string?>(null)),
                gitHub,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.False(vm.Saved);
            Assert.Equal(
                GitHubOpenViewModel.MessageFor(new GitHubApiException(GitHubErrorKind.Network, "x")),
                vm.ErrorMessage);
            Assert.False(vm.ShowReload);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RefreshBoardStatus_TrueOnlyForTheExpectedConfigId()
    {
        // A config without an id: the wizard writes its GUID, so that GUID
        // alone is "the board reporting in".
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            string? reported = null;
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(root, null, _ => Task.FromResult(reported)),
                gitHub: null,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.NextCommand.Execute(null); // -> Review
            await vm.SaveCommand.ExecuteAsync(null); // -> Done, FinishSave

            Assert.Equal(ModuleSetupStep.Done, vm.Step);
            Assert.False(vm.IsAddFeaturesSave); // a first setup writes a new id the board can report

            reported = null;
            await vm.RefreshBoardStatusAsync();
            Assert.False(vm.IsBoardConnected);

            reported = "something-else";
            await vm.RefreshBoardStatusAsync();
            Assert.False(vm.IsBoardConnected);

            reported = FixedGuid.ToString("D");
            await vm.RefreshBoardStatusAsync();
            Assert.True(vm.IsBoardConnected);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AddFeaturesSave_SkipsTheBoardCheck_AndAsksToFlash()
    {
        // The glove80 fixture already carries CONFIG_HID_VIZ_CONFIG_ID, so the
        // plan writes no new id and the old firmware reports the same one:
        // the Done step can't tell old from new, so it doesn't ask.
        var root = ZmkConfigFixtures.CopyToTemp("glove80");
        try
        {
            var queries = 0;
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(root, null, _ =>
                {
                    queries++;
                    return Task.FromResult<string?>("123e4567-e89b-12d3-a456-426614174000");
                }),
                gitHub: null,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null); // -> Choose
            vm.System = true;
            vm.NextCommand.Execute(null); // -> Review
            await vm.SaveCommand.ExecuteAsync(null); // -> Done

            await vm.RefreshBoardStatusAsync();
            Assert.True(vm.IsDoneStep);
            Assert.True(vm.IsAddFeaturesSave);
            Assert.False(vm.IsBoardConnected);
            Assert.Equal(0, queries);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshBoardStatus_QueryThrows_IsFalse()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = new ModuleSetupViewModel(
                new ModuleSetupContext(root, null,
                    _ => throw new InvalidOperationException("no transport")),
                gitHub: null,
                FreshBackups(),
                () => FixedGuid);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);
            await vm.SaveCommand.ExecuteAsync(null); // FinishSave swallows it too

            Assert.Equal(ModuleSetupStep.Done, vm.Step);
            await vm.RefreshBoardStatusAsync(); // the throw stays inside the method
            Assert.False(vm.IsBoardConnected);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Zzeneg_Review_NoticesTheForkSwitch_AndShowsTheRemoteChange()
    {
        var root = ZmkConfigFixtures.CopyToTemp("zzeneg");
        try
        {
            var vm = LocalVm(root);
            await vm.StartAsync();
            vm.NextCommand.Execute(null);
            vm.SelectedCentral ??= vm.CentralOptions.Single(o => o.Index == 0);
            vm.NextCommand.Execute(null);

            Assert.Equal(ModuleSetupStep.Review, vm.Step);
            Assert.Equal(Loc.Instance["ModuleSetup_ForkNotice"], vm.ForkNotice);

            var west = vm.Files.Single(f => f.Path == "config/west.yml");
            Assert.Contains(west.Lines, l => l.IsRemoved && !l.IsAdded && l.Text.EndsWith("remote: zzeneg"));
            Assert.Contains(west.Lines, l => l.IsAdded && !l.IsRemoved && l.Text.EndsWith("remote: ovandongen"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
