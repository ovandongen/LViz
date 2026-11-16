using System.Buffers.Binary;
using System.Text;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.ViewModels;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The flash window's Flash and Done steps over the real cosmos82 fixture:
/// the halves are queued peripheral-first, each half shows its instructions
/// (or the reason the trigger is out), Start and the manual way run it, the
/// phases turn into progress text, failures retry or skip, and the run ends
/// on Done with one line per half. GitHub-build halves land in the flash
/// history; file halves never do.
/// </summary>
public sealed class FlashViewModelRunTests : IDisposable
{
    private const string Owner = "maker";
    private const string Repo = "cosmos-config";
    private const string Branch = "main";
    private const string Sha = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const string ConfigKey = "github:maker/cosmos-config:main";

    private const string CentralFile = "cosmos82_right-nice_nano_v2-zmk.uf2";
    private const string PeripheralFile = "cosmos82_left-nice_nano_v2-zmk.uf2";

    private static readonly Uf2Drive DriveA = new("/vols/BOOT_A", "BOOT_A", "Board-ID: cosmos");
    private static readonly Uf2Drive DriveB = new("/vols/BOOT_B", "BOOT_B", "Board-ID: cosmos");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lviz-flashrun-{Guid.NewGuid():N}");
    private readonly FakeBuilds _builds = new();
    private readonly FakeDesktopInteraction _desktop = new();

    public FlashViewModelRunTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string T(string key) => Loc.Instance[key];

    private static string TF(string key, params object[] args) => Loc.Instance.Format(key, args);

    /// <summary>The central half's label: it sits on the right on cosmos82.</summary>
    private static string CentralLabel => TF("Flash_HalfWithCentral", T("Flash_HalfRight"));

    /// <summary>The manual hint as the flash window shows it (Linux adds the mount hint).</summary>
    private static string ExpectedManualHint(string side) =>
        OperatingSystem.IsLinux()
            ? TF("Flash_ManualHint", side) + " " + T("Flash_MountHint")
            : TF("Flash_ManualHint", side);

    // --- order and the full two-half run ---

    [Fact]
    public async Task FlashStep_PeripheralGoesFirst_ThenTheCentral()
    {
        var kb = ConnectedKeyboard();
        var drives = new ScriptedDrives(None(), [DriveA], None(), None(), [DriveB], None());
        var vm = await StartBothHalvesAsync(GitHubVm(kb, drives));

        // The queue starts at the peripheral while the central can still relay.
        Assert.Equal(T("Flash_HalfLeft"), vm.CurrentHalfLabel);
        Assert.Equal(TF("Flash_FlashHeader", T("Flash_HalfLeft")), vm.FlashHeaderText);
        Assert.Equal(FlashViewModel.FlashHalfStage.Prepare, vm.Stage);
        Assert.True(vm.CanStart);

        await vm.StartCommand.ExecuteAsync(null); // the peripheral

        Assert.Equal(CentralLabel, vm.CurrentHalfLabel);
        Assert.Equal(TF("Flash_FlashHeader", CentralLabel), vm.FlashHeaderText);
        Assert.True(vm.IsFlashStep); // one half still to go

        await vm.StartCommand.ExecuteAsync(null); // the central

        Assert.True(vm.IsDoneStep);
        Assert.Equal(
            new[] { TF("Flash_DoneFlashed", T("Flash_HalfLeft")), TF("Flash_DoneFlashed", CentralLabel) },
            vm.DoneLines);
        Assert.True(vm.Flashed);
        Assert.Equal(new byte[] { 0, BootloaderCommand.CentralTarget }, kb.TriggerTargets);
    }

    [Fact]
    public async Task BothHalvesFlashed_GitHubBuild_RecordsBothHalves()
    {
        var kb = ConnectedKeyboard();
        var drives = new ScriptedDrives(None(), [DriveA], None(), None(), [DriveB], None());
        var history = NewHistory("both");
        var vm = await StartBothHalvesAsync(GitHubVm(kb, drives, history));

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(Sha, history.Get(ConfigKey, FirmwareHalfRole.Peripheral)?.CommitSha);
        Assert.Equal(Sha, history.Get(ConfigKey, FirmwareHalfRole.Central)?.CommitSha);
    }

    // --- the prepare state ---

    [Fact]
    public async Task Prepare_CentralOnUsbWithSlots_TriggerOffered()
    {
        var vm = await StartFileAsync(LocalVm(ConnectedKeyboard(), new ScriptedDrives(None())));

        Assert.Equal(FlashViewModel.FlashHalfStage.Prepare, vm.Stage);
        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.True(vm.ManualCommand.CanExecute(null));
        Assert.False(vm.CancelFlashCommand.CanExecute(null));
        Assert.Equal(TF("Flash_PrepareCentral", T("Flash_SideRight")), vm.PrepareText);
        Assert.Null(vm.ReasonText);
        Assert.Equal(CentralLabel, vm.CurrentHalfLabel);
    }

    [Fact]
    public async Task Prepare_PeripheralFirst_NamesBothSides()
    {
        var vm = await StartBothHalvesAsync(GitHubVm(ConnectedKeyboard(), new ScriptedDrives(None())));

        Assert.True(vm.CanStart);
        Assert.Equal(TF("Flash_PreparePeripheral", T("Flash_SideLeft"), T("Flash_SideRight")), vm.PrepareText);
        Assert.Null(vm.ReasonText);
    }

    [Fact]
    public async Task Prepare_Bluetooth_ManualTextAndReason()
    {
        var kb = ConnectedKeyboard(bus: KeyboardBus.Bluetooth);
        var vm = await StartFileAsync(LocalVm(kb, new ScriptedDrives(None())));

        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.ManualCommand.CanExecute(null));
        Assert.Equal(T("Flash_ReasonBluetooth"), vm.ReasonText);
        Assert.Equal(TF("Flash_PrepareManual", T("Flash_SideRight")), vm.PrepareText);
    }

    [Fact]
    public async Task Prepare_NotConnected_ReasonSaysSo()
    {
        var vm = await StartFileAsync(LocalVm(new FakeKeyboard(), new ScriptedDrives(None())));

        Assert.False(vm.CanStart);
        Assert.Equal(T("Flash_ReasonNotConnected"), vm.ReasonText);
    }

    [Fact]
    public async Task Prepare_SlotsNull_ReasonNoSupport()
    {
        var kb = ConnectedKeyboard(slots: null);
        var vm = await StartFileAsync(LocalVm(kb, new ScriptedDrives(None())));

        Assert.False(vm.CanStart);
        Assert.Equal(T("Flash_ReasonNoSupport"), vm.ReasonText);
    }

    [Fact]
    public async Task Prepare_PeripheralWithZeroSlots_ReasonNoSupport()
    {
        var kb = ConnectedKeyboard(slots: 0);
        var vm = await StartBothHalvesAsync(GitHubVm(kb, new ScriptedDrives(None())));

        Assert.Equal(T("Flash_HalfLeft"), vm.CurrentHalfLabel); // the peripheral is first
        Assert.False(vm.CanStart);
        Assert.Equal(T("Flash_ReasonNoSupport"), vm.ReasonText);
    }

    // --- running a half ---

    [Fact]
    public async Task Start_CentralFile_DriveComesAndGoes_DoneFlashed_NoHistory()
    {
        var kb = ConnectedKeyboard();
        var drives = new ScriptedDrives(None(), [DriveA], None());
        var history = NewHistory("file");
        var vm = await StartFileAsync(LocalVm(kb, drives, history));

        var phases = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashViewModel.ProgressText) && vm.ProgressText.Length > 0)
            {
                phases.Add(vm.ProgressText);
            }
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDoneStep);
        Assert.Equal(new[] { TF("Flash_DoneFlashed", CentralLabel) }, vm.DoneLines);
        Assert.True(vm.Flashed);
        Assert.Equal(new byte[] { BootloaderCommand.CentralTarget }, kb.TriggerTargets);
        Assert.Null(history.Get(ZmkConfigFixtures.Root("cosmos82"), FirmwareHalfRole.Central));

        // The phases arrive in order, with the half named where the text asks for it.
        Assert.Equal(
            new[]
            {
                TF("Flash_Triggering", T("Flash_SideRight")),
                TF("Flash_WaitingForDrive", T("Flash_SideRight")),
                T("Flash_Copying"),
                TF("Flash_WaitingForRestart", T("Flash_SideRight")),
                T("Flash_CheckingBoard"),
            },
            phases);
    }

    [Fact]
    public async Task Start_CentralGitHubBuild_HistoryHastheBuildSha()
    {
        var kb = ConnectedKeyboard();
        var drives = new ScriptedDrives(None(), [DriveA], None());
        var history = NewHistory("central");
        var vm = await StartCentralOnlyAsync(GitHubVm(kb, drives, history));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDoneStep);
        Assert.Equal(Sha, history.Get(ConfigKey, FirmwareHalfRole.Central)?.CommitSha);
        Assert.Null(history.Get(ConfigKey, FirmwareHalfRole.Peripheral));
    }

    [Fact]
    public async Task ManualByHand_NeverTriggers_AndTheHintIsUpFromTheStart()
    {
        var kb = ConnectedKeyboard();
        var vm = await StartFileAsync(LocalVm(kb, new ScriptedDrives(None(), [DriveA], None())));

        var hints = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashViewModel.ManualHintText) && vm.ManualHintText is { } hint)
            {
                hints.Add(hint);
            }
        };

        await vm.ManualCommand.ExecuteAsync(null);

        Assert.Empty(kb.TriggerTargets); // the bootloader command was never sent
        Assert.Equal(new[] { ExpectedManualHint(T("Flash_SideRight")) }, hints);
        Assert.True(vm.IsDoneStep);
    }

    [Fact]
    public async Task CopyFailed_ShowsTheError_RetryPrepares_SkipEndsOnDone()
    {
        var kb = ConnectedKeyboard();
        var vm = await StartFileAsync(LocalVm(
            kb, new ScriptedDrives(None(), [DriveA]), null, (_, _, _) => Task.FromResult(new Uf2CopyResult(false, "disk full"))));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FlashViewModel.FlashHalfStage.Failed, vm.Stage);
        Assert.Equal(TF("Flash_FailedCopy", "disk full"), vm.FailureText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.RetryHalfCommand.CanExecute(null));
        Assert.True(vm.SkipHalfCommand.CanExecute(null));

        vm.RetryHalfCommand.Execute(null);

        Assert.Equal(FlashViewModel.FlashHalfStage.Prepare, vm.Stage);
        Assert.Equal(string.Empty, vm.FailureText);
        Assert.Equal(CentralLabel, vm.CurrentHalfLabel); // the same half

        vm.SkipHalfCommand.Execute(null);

        Assert.True(vm.IsDoneStep);
        Assert.Equal(new[] { TF("Flash_DoneSkipped", CentralLabel) }, vm.DoneLines);
        Assert.False(vm.Flashed);
    }

    [Fact]
    public async Task Skip_FailedHalf_MovesOnToTheNextHalf()
    {
        var kb = ConnectedKeyboard();
        var vm = await StartBothHalvesAsync(GitHubVm(
            kb, new ScriptedDrives(None(), [DriveA]), null, (_, _, _) => Task.FromResult(new Uf2CopyResult(false, "disk full"))));

        await vm.StartCommand.ExecuteAsync(null); // the peripheral fails
        vm.SkipHalfCommand.Execute(null);

        Assert.True(vm.IsFlashStep);
        Assert.Equal(CentralLabel, vm.CurrentHalfLabel);
        Assert.Equal(FlashViewModel.FlashHalfStage.Prepare, vm.Stage);
        Assert.Equal(new[] { TF("Flash_DoneSkipped", T("Flash_HalfLeft")) }, vm.DoneLines);
    }

    [Fact]
    public async Task NotAccepted_BootsButKeepsTheDrive_ShowsItsText()
    {
        var kb = ConnectedKeyboard();
        // The drive comes and never goes: the bootloader ignored the file.
        var vm = await StartFileAsync(LocalVm(
            kb, new ScriptedDrives(None(), [DriveA], [DriveA])));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FlashViewModel.FlashHalfStage.Failed, vm.Stage);
        Assert.Equal(T("Flash_FailedNotAccepted"), vm.FailureText);
    }

    [Fact]
    public async Task Cancel_WhileWaitingForTheDrive_BackToPrepare_NoLine()
    {
        var kb = ConnectedKeyboard();
        var vm = await StartFileAsync(LocalVm(kb, new ScriptedDrives(None()))); // a drive never comes

        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ManualHintText is not null); // in the open-ended wait

        vm.CancelFlashCommand.Execute(null);
        await running;

        Assert.Equal(FlashViewModel.FlashHalfStage.Prepare, vm.Stage);
        Assert.True(vm.IsFlashStep);
        Assert.Empty(vm.DoneLines);
        Assert.Null(vm.ManualHintText);
        Assert.Equal(string.Empty, vm.ProgressText);
        Assert.Equal(new byte[] { BootloaderCommand.CentralTarget }, kb.TriggerTargets);
    }

    [Fact]
    public async Task CancelWork_WindowClosedMidWait_NothingPollsOrCopiesAfterwards()
    {
        // The window closes while LViz waits for the drive; a bootloader
        // drive that turns up afterwards must not get the file.
        var kb = ConnectedKeyboard();
        var driveUp = false;
        var listed = 0;
        var copies = 0;
        var vm = await StartFileAsync(LocalVm(
            kb,
            new CallbackDrives(_ => driveUp ? [DriveA] : None(), _ => listed++),
            copy: (_, _, _) =>
            {
                copies++;
                return Task.FromResult(new Uf2CopyResult(true, null));
            }));

        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ManualHintText is not null); // in the open-ended wait

        vm.CancelWork();
        await running;
        driveUp = true;
        var listedAtClose = listed;
        await Task.Delay(50);

        Assert.Equal(0, copies);
        Assert.Equal(listedAtClose, listed);
        Assert.Empty(vm.DoneLines);
    }

    [Fact]
    public async Task WrongHalf_DriveLabelNamesTheOtherSide_FailsWithoutCopying()
    {
        // The cosmos82 central is the right half; a drive labelled for the left must not get its file.
        var kb = ConnectedKeyboard();
        var copies = 0;
        var leftDrive = new Uf2Drive("/vols/GLV80LHBOOT", "GLV80LHBOOT", "Board-ID: glove80");
        var vm = await StartFileAsync(LocalVm(
            kb,
            new ScriptedDrives(None(), [leftDrive]),
            copy: (_, _, _) =>
            {
                copies++;
                return Task.FromResult(new Uf2CopyResult(true, null));
            }));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(FlashViewModel.FlashHalfStage.Failed, vm.Stage);
        Assert.Equal(TF("Flash_FailedWrongHalf", "GLV80LHBOOT", T("Flash_SideRight")), vm.FailureText);
        Assert.Equal(0, copies);
        Assert.True(vm.RetryHalfCommand.CanExecute(null));
    }

    [Fact]
    public async Task BoardStaysAway_FlashedButNotReported_IsADoneNote()
    {
        var kb = ConnectedKeyboard();
        // The drive arrives (and the board dies with it) but never reports again.
        var drives = new CallbackDrives(
            n => n == 1 ? [DriveA] : None(),
            n =>
            {
                if (n == 1)
                {
                    kb.IsConnected = false;
                }
            });
        var vm = await StartFileAsync(LocalVm(kb, drives));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDoneStep);
        Assert.Equal(new[] { TF("Flash_DoneNotReported", CentralLabel) }, vm.DoneLines);
        Assert.True(vm.Flashed); // the copy finished — the late report is a note, not an error
    }

    [Fact]
    public async Task HistoryRecordFails_IsLoggedAndDoesntStopTheRun()
    {
        var kb = ConnectedKeyboard();
        var vm = await StartCentralOnlyAsync(GitHubVm(
            kb, new ScriptedDrives(None(), [DriveA], None()), new FirmwareFlashHistory(_tempDir))); // a directory as the file

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDoneStep);
        Assert.Equal(new[] { TF("Flash_DoneFlashed", CentralLabel) }, vm.DoneLines);
        Assert.True(vm.Flashed);
        Assert.Equal(string.Empty, vm.FailureText); // the failure never reaches the window
    }

    // --- fixtures and fakes ---

    private static FakeKeyboard ConnectedKeyboard(
        KeyboardBus bus = KeyboardBus.Usb, int? slots = 1) =>
        new() { IsConnected = true, Bus = bus, BootloaderPeripheralSlots = slots };

    private static IReadOnlyList<Uf2Drive> None() => [];

    private FirmwareFlashHistory NewHistory(string name) =>
        new(Path.Combine(_tempDir, $"{name}-{Guid.NewGuid():N}.json"));

    private static Task FastDelay(TimeSpan _, CancellationToken ct) => Task.Delay(1, ct);

    private static Task<Uf2CopyResult> CopyOk(
        ReadOnlyMemory<byte> _, string __, CancellationToken ___) =>
        Task.FromResult(new Uf2CopyResult(true, null));

    /// <summary>A local context in file mode, past the picked file, on the Flash step with the central planned.</summary>
    private async Task<FlashViewModel> StartFileAsync(FlashViewModel vm)
    {
        await vm.StartAsync();
        _desktop.Uf2Path = WithMarker();
        await vm.PickFileCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsFlashStep);
        return vm;
    }

    /// <summary>A GitHub context whose ready build is downloaded and whose ticked plan is both halves.</summary>
    private async Task<FlashViewModel> StartBothHalvesAsync(FlashViewModel vm)
    {
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        await vm.StartAsync();
        await vm.NextCommand.ExecuteAsync(null); // source -> halves
        Assert.True(vm.IsHalvesStep);
        vm.HalfRows[1].IsChecked = true; // the peripheral too
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsFlashStep);
        return vm;
    }

    /// <summary>Same, but the advice leaves the peripheral unticked: the plan is the central alone.</summary>
    private async Task<FlashViewModel> StartCentralOnlyAsync(FlashViewModel vm)
    {
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        _builds.Advice = PeripheralAdvice.NotNeeded;
        await vm.StartAsync();
        await vm.NextCommand.ExecuteAsync(null); // source -> halves
        Assert.True(vm.IsHalvesStep);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsFlashStep);
        return vm;
    }

    private FlashViewModel LocalVm(
        FakeKeyboard kb,
        IUf2DriveSource drives,
        FirmwareFlashHistory? history = null,
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<Uf2CopyResult>>? copy = null)
    {
        var run = new FlashRun(kb, new Uf2DriveWatcher(drives, FastDelay), copy ?? CopyOk, FastDelay);
        var root = ZmkConfigFixtures.Root("cosmos82");
        return new FlashViewModel(
            new FlashContext(root, root, null, null),
            _builds,
            history ?? NewHistory("local"),
            kb,
            run,
            _desktop);
    }

    private FlashViewModel GitHubVm(
        FakeKeyboard kb,
        IUf2DriveSource drives,
        FirmwareFlashHistory? history = null,
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<Uf2CopyResult>>? copy = null)
    {
        var run = new FlashRun(kb, new Uf2DriveWatcher(drives, FastDelay), copy ?? CopyOk, FastDelay);
        _builds.Build = new FirmwareBuild(FirmwareBuildState.Ready, Run(), 7, false);
        return new FlashViewModel(
            new FlashContext(ConfigKey, ZmkConfigFixtures.Root("cosmos82"), new FlashGitHubTarget(Owner, Repo, Branch), null),
            _builds,
            history ?? NewHistory("github"),
            kb,
            run,
            _desktop);
    }

    /// <summary>One run as the API reports it (as in FlashViewModelTests).</summary>
    private static GitHubWorkflowRun Run() =>
        new(42, "Build firmware", Branch, Sha, "completed", "success",
            "https://github.com/maker/cosmos-config/actions/runs/42",
            new DateTimeOffset(2026, 11, 11, 10, 30, 0, TimeSpan.Zero),
            "Tune cosmos82 keymap");

    /// <summary>One valid 512-byte UF2 block carrying the ASCII payload (as in FlashViewModelTests).</summary>
    private static byte[] Uf2Block(string payload)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        var block = new byte[Uf2File.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), Uf2File.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), Uf2File.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), 1); // block count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)bytes.Length);
        bytes.CopyTo(block.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), Uf2File.MagicEnd);
        return block;
    }

    /// <summary>A temp <c>.uf2</c>; its path is what DownloadAsync would hand back.</summary>
    private string Uf2(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Uf2Block("ZMK"));
        return path;
    }

    private string WithMarker()
    {
        var path = Path.Combine(_tempDir, $"support-{Guid.NewGuid():N}.uf2");
        File.WriteAllBytes(path, Uf2Block("zmk behaviors here core.system.bootloader and more"));
        return path;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>A mount table per <see cref="List"/> call; the last step repeats forever.</summary>
    private sealed class ScriptedDrives(params IReadOnlyList<Uf2Drive>[] script) : IUf2DriveSource
    {
        private int _calls;

        public IReadOnlyList<Uf2Drive> List() => script[Math.Min(_calls++, script.Length - 1)];
    }

    /// <summary>A listing the test computes per call, with a per-call side effect (unplugging the board).</summary>
    private sealed class CallbackDrives(
        Func<int, IReadOnlyList<Uf2Drive>> listAt,
        Action<int>? onList = null) : IUf2DriveSource
    {
        private int _calls;

        public IReadOnlyList<Uf2Drive> List()
        {
            var n = _calls++;
            onList?.Invoke(n);
            return listAt(n);
        }
    }

    /// <summary>Answers on request, with the trigger calls logged; connected only when the test says so.</summary>
    private sealed class FakeKeyboard : IKeyboardFlashAccess
    {
        public bool IsConnected { get; set; }

        public KeyboardBus Bus { get; set; } = KeyboardBus.None;

        public int? BootloaderPeripheralSlots { get; set; }

        public List<byte> TriggerTargets { get; } = [];

        public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default)
        {
            TriggerTargets.Add(targetHalf);
            return Task.FromResult(new ControlResult(ControlOutcome.Confirmed));
        }

        public Task<string?> QueryConfigIdAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>The scripted build service (as in FlashViewModelTests); the GitHub path needs the lookup and download.</summary>
    private sealed class FakeBuilds : IFirmwareBuildService
    {
        public FirmwareBuild Build = new(FirmwareBuildState.NoRun, null, null, false);

        public FirmwareDownloadResult Download = new FirmwareDownloadResult.Done([]);

        public PeripheralAdvice Advice = PeripheralAdvice.Unknown;

        public string? Login => "build-user";

        public bool IsSignedIn => true;

        public bool IsLoginPersisted => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task EnsureRestoredAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("the login is restored already");

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FirmwareBuild> FindBuildAsync(
            string owner, string repo, string? headSha, string? branch, CancellationToken ct = default) =>
            Task.FromResult(Build);

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner, string repo, string sha, long artifactId, CancellationToken ct = default) =>
            Task.FromResult(Download);

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner, string repo, string? lastFlashedSha, string buildSha, CancellationToken ct = default) =>
            Task.FromResult(Advice);
    }
}
