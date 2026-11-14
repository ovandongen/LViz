using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Flashing;

/// <summary>
/// <see cref="FlashRun"/> — the one-half flash flow: trigger, wait for the
/// bootloader drive, copy, wait for the restart, and (central) wait for the
/// board to report in. Runs the real <see cref="Uf2DriveWatcher"/> against a
/// scripted mount table (a queue that repeats its last answer, so a watcher
/// bug loops on the same table instead of dying on an empty queue), a fake
/// keyboard with a scripted trigger result and config-ID answers, a fake copy,
/// and instant delays. Progress is collected through a synchronous
/// <see cref="IProgress{T}"/> — a <see cref="Progress{T}"/> would post and
/// lose reports against the run's end.
/// </summary>
public sealed class FlashRunTests
{
    private const string ConfigId = "id-1";

    private static readonly Uf2Drive OldDrive = new("/Volumes/OLDSTICK", "OLDSTICK", "Board-ID other");
    private static readonly Uf2Drive Drive = new("/Volumes/NICENANO", "NICENANO", "Board-ID corne");

    private static readonly IReadOnlyList<Uf2Drive> NoDrives = [];
    private static readonly IReadOnlyList<Uf2Drive> WithOld = [OldDrive];
    private static readonly IReadOnlyList<Uf2Drive> WithDrive = [Drive];

    private static readonly FirmwareHalf Central = Half(FirmwareHalfRole.Central, 0, "corne6450_left", FirmwareSide.Left);
    private static readonly FirmwareHalf Peripheral = Half(FirmwareHalfRole.Peripheral, 1, "corne6450_right", FirmwareSide.Right);

    private static FirmwareHalf Half(FirmwareHalfRole role, int index, string board, FirmwareSide side) =>
        new(role, index, board, ["corne", "nice_nano"], side, $"{board}-zmk.uf2");

    // ---- the happy paths ----

    [Fact]
    public async Task CentralHappyPath_TriggerCopyRestartCheck_Flashed()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);
        h.Keyboard.ConfigIds.Enqueue(null);
        h.Keyboard.ConfigIds.Enqueue(ConfigId);

        var result = await h.FlashAsync(Central, useTrigger: true, ConfigId);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(BootloaderCommand.CentralTarget, Assert.Single(h.Keyboard.TriggerTargets));
        Assert.Equal(Drive.RootPath, Assert.Single(h.Copy.Roots));

        // First poll says nothing, the second answers with the expected id.
        Assert.Equal(2, h.Keyboard.ConfigIdCalls);
        Assert.Equal([FlashRun.ReportPoll], h.Delays);

        Assert.Equal(
            new[]
            {
                FlashPhase.Triggering,
                FlashPhase.WaitingForDrive,
                FlashPhase.Copying,
                FlashPhase.WaitingForRestart,
                FlashPhase.CheckingBoard,
            },
            h.Phases);
        var waiting = Assert.Single(h.Progress, p => p.Phase == FlashPhase.WaitingForDrive);
        Assert.False(waiting.ShowManualHint);
        Assert.All(h.Progress, p => Assert.Null(p.TriggerProblem));
    }

    [Fact]
    public async Task Peripheral_TriggerTargetsSlot0_DriveGoneIsFlashedWithoutAConfigIdQuery()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);
        h.Keyboard.ConfigIds.Enqueue(ConfigId); // would match, but must never be asked

        var result = await h.FlashAsync(Peripheral, useTrigger: true, ConfigId);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal((byte)0, Assert.Single(h.Keyboard.TriggerTargets));
        Assert.Equal(0, h.Keyboard.ConfigIdCalls);
        Assert.DoesNotContain(FlashPhase.CheckingBoard, h.Phases);
    }

    // ---- the manual-hint paths ----

    [Fact]
    public async Task TriggerNotConfirmed_FirstWaitingForDriveCarriesHintAndProblem()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);
        h.Keyboard.TriggerResult = new ControlResult(ControlOutcome.Failed, "nope");

        var result = await h.FlashAsync(Peripheral, useTrigger: true, null);

        // The drive then appears and the flow continues to the end.
        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Equal(FlashPhase.Triggering, h.Progress[0].Phase);

        // The one WaitingForDrive already asks for the manual bootloader —
        // the 15 s trigger wait was skipped.
        var waiting = Assert.Single(h.Progress, p => p.Phase == FlashPhase.WaitingForDrive);
        Assert.True(waiting.ShowManualHint);
        Assert.Equal("nope", waiting.TriggerProblem);
        Assert.Equal(0, h.DriveDelayCount);
    }

    [Fact]
    public async Task TriggerOkButNoDriveWithinTriggerWait_HintAppearsThenDriveArrives()
    {
        // The snapshot and every poll of the 15 s wait see nothing; only then
        // does the user mount the drive, which the copy takes and the half
        // reboots away.
        var finiteWaitPolls = (int)(FlashRun.TriggerWait / Uf2DriveWatcher.PollInterval) + 1;
        var snapshots = new List<IReadOnlyList<Uf2Drive>>(2 + finiteWaitPolls) { NoDrives };
        snapshots.AddRange(Enumerable.Repeat(NoDrives, finiteWaitPolls));
        snapshots.Add(WithDrive);
        snapshots.Add(NoDrives);
        var h = new Harness([.. snapshots]);

        var result = await h.FlashAsync(Peripheral, useTrigger: true, null);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Equal(
            new[]
            {
                FlashPhase.Triggering,
                FlashPhase.WaitingForDrive,
                FlashPhase.WaitingForDrive,
                FlashPhase.Copying,
                FlashPhase.WaitingForRestart,
            },
            h.Phases);
        Assert.False(h.Progress[1].ShowManualHint);
        Assert.True(h.Progress[2].ShowManualHint);
        Assert.Null(h.Progress[2].TriggerProblem);
        // The timed-out 15 s wait burned exactly its budget in poll delays.
        Assert.Equal(finiteWaitPolls - 1, h.DriveDelayCount);
    }

    [Fact]
    public async Task NoTrigger_BootloaderCommandIsNeverSentAndHintStartsTrue()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);

        var result = await h.FlashAsync(Peripheral, useTrigger: false, null);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Empty(h.Keyboard.TriggerTargets);
        Assert.DoesNotContain(FlashPhase.Triggering, h.Phases);
        var waiting = Assert.Single(h.Progress, p => p.Phase == FlashPhase.WaitingForDrive);
        Assert.True(waiting.ShowManualHint);
        Assert.Null(waiting.TriggerProblem);
    }

    [Fact]
    public async Task DriveMountedBeforeTheSnapshot_IsIgnoredUntilANewOneAppears()
    {
        // The old stick is already mounted at the snapshot and stays; only
        // the fresh one counts, and the copy goes to that one.
        var h = new Harness(WithOld, WithOld, WithDrive, NoDrives);

        var result = await h.FlashAsync(Peripheral, useTrigger: true, null);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Equal(Drive.RootPath, Assert.Single(h.Copy.Roots));
        var waiting = Assert.Single(h.Progress, p => p.Phase == FlashPhase.WaitingForDrive);
        Assert.False(waiting.ShowManualHint);
    }

    // ---- the soft outcomes ----

    [Fact]
    public async Task CopyFails_ReturnsCopyFailedWithTheError()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);
        h.Copy.Result = new Uf2CopyResult(false, "disk full");

        var result = await h.FlashAsync(Central, useTrigger: true, ConfigId);

        Assert.Equal(FlashOutcome.CopyFailed, result.Outcome);
        Assert.Equal("disk full", result.Error);
        Assert.Equal(
            new[] { FlashPhase.Triggering, FlashPhase.WaitingForDrive, FlashPhase.Copying },
            h.Phases);
        Assert.Equal(0, h.Keyboard.ConfigIdCalls);
    }

    [Fact]
    public async Task DriveNeverGoes_ReturnsNotAccepted()
    {
        // The last table repeats forever: the bootloader ignored the file.
        var h = new Harness(NoDrives, WithDrive);

        var result = await h.FlashAsync(Central, useTrigger: true, ConfigId);

        Assert.Equal(FlashOutcome.NotAccepted, result.Outcome);
        Assert.Null(result.Error);
        Assert.DoesNotContain(FlashPhase.CheckingBoard, h.Phases);
        Assert.Equal(0, h.Keyboard.ConfigIdCalls);
    }

    [Fact]
    public async Task CentralNeverReports_FlashedNotReportedAfterEveryPoll()
    {
        var h = new Harness(NoDrives, WithDrive, NoDrives);
        // Empty answer queue: null repeats forever, the id never arrives.

        var result = await h.FlashAsync(Central, useTrigger: true, ConfigId);

        Assert.Equal(FlashOutcome.FlashedNotReported, result.Outcome);
        Assert.Null(result.Error);
        var polls = (int)(FlashRun.ReportWait / FlashRun.ReportPoll);
        Assert.Equal(30, polls);
        Assert.Equal(polls, h.Keyboard.ConfigIdCalls);
        Assert.Equal(polls, h.Delays.Count);
        Assert.All(h.Delays, d => Assert.Equal(FlashRun.ReportPoll, d));
    }

    [Fact]
    public async Task CancellationDuringTheInfiniteDriveWait_Throws()
    {
        var h = new Harness(NoDrives);
        using var cts = new CancellationTokenSource();
        // Snapshot, first poll, then the third listing cancels the caller.
        h.Source.CancelWith = cts;
        h.Source.CancelOnCall = 3;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.FlashAsync(Peripheral, useTrigger: false, null, cts.Token));
    }

    // ---- drive side check ----

    [Fact]
    public async Task DriveLabelNamesTheOtherSide_WrongHalf_NothingCopied()
    {
        // Central is the left half; a MoErgo right-half bootloader turns up.
        var rightDrive = new Uf2Drive("/Volumes/GO60RHBOOT", "GO60RHBOOT", "Board-ID go60");
        var h = new Harness(NoDrives, [rightDrive]);

        var result = await h.FlashAsync(Central, useTrigger: false, ConfigId);

        Assert.Equal(FlashOutcome.WrongHalf, result.Outcome);
        Assert.Equal("GO60RHBOOT", result.Error);
        Assert.Empty(h.Copy.Roots);
        Assert.DoesNotContain(FlashPhase.Copying, h.Phases);
    }

    [Fact]
    public async Task DriveLabelNamesTheSameSide_Copies()
    {
        var leftDrive = new Uf2Drive("/Volumes/GLV80LHBOOT", "GLV80LHBOOT", "Board-ID glove80");
        var h = new Harness(NoDrives, [leftDrive], NoDrives);
        h.Keyboard.ConfigIds.Enqueue(ConfigId);

        var result = await h.FlashAsync(Central, useTrigger: false, ConfigId);

        Assert.Equal(FlashOutcome.Flashed, result.Outcome);
        Assert.Equal(leftDrive.RootPath, Assert.Single(h.Copy.Roots));
    }

    [Theory]
    [InlineData("GLV80LHBOOT", FirmwareSide.Left)]
    [InlineData("go60lhboot", FirmwareSide.Left)]
    [InlineData("GO60RHBOOT", FirmwareSide.Right)]
    [InlineData("NICENANO", FirmwareSide.Unknown)]
    [InlineData("XIAO-SENSE", FirmwareSide.Unknown)]
    public void Uf2Drive_SideComesFromTheMoErgoLabel(string label, FirmwareSide side) =>
        Assert.Equal(side, new Uf2Drive("/Volumes/" + label, label, "").Side);

    // ---- CanTrigger ----

    [Fact]
    public void CanTrigger_UsbWithOneSlot_BothRoles()
    {
        var keyboard = new FakeKeyboard { Bus = KeyboardBus.Usb, BootloaderPeripheralSlots = 1 };
        Assert.True(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Central));
        Assert.True(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Peripheral));
    }

    [Fact]
    public void CanTrigger_NoPeripheralSlots_CentralOnly()
    {
        var keyboard = new FakeKeyboard { BootloaderPeripheralSlots = 0 };
        Assert.True(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Central));
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Peripheral));
    }

    [Fact]
    public void CanTrigger_Bluetooth_NeitherRole()
    {
        var keyboard = new FakeKeyboard { Bus = KeyboardBus.Bluetooth };
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Central));
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Peripheral));
    }

    [Fact]
    public void CanTrigger_FirmwareWithoutTheCapability_NeitherRole()
    {
        var keyboard = new FakeKeyboard { BootloaderPeripheralSlots = null };
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Central));
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Peripheral));
    }

    [Fact]
    public void CanTrigger_NotConnected_NeitherRole()
    {
        var keyboard = new FakeKeyboard { IsConnected = false };
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Central));
        Assert.False(FlashRun.CanTrigger(keyboard, FirmwareHalfRole.Peripheral));
    }

    // ---- harness ----

    /// <summary>
    /// Wires a <see cref="FlashRun"/> over a scripted mount table with instant
    /// delays (the watcher's own poll delay and the run's report delay are
    /// recorded separately), a fake keyboard and a fake copy.
    /// </summary>
    private sealed class Harness
    {
        private readonly List<TimeSpan> _driveDelays = new();

        public Harness(params IReadOnlyList<Uf2Drive>[] snapshots)
        {
            Source = new ScriptedDriveSource(snapshots);
            var drives = new Uf2DriveWatcher(Source, RecordDriveDelay);
            Run = new FlashRun(Keyboard, drives, Copy.Run, RecordDelay);
        }

        public FakeKeyboard Keyboard { get; } = new();

        public FakeCopy Copy { get; } = new();

        public ScriptedDriveSource Source { get; }

        public List<FlashProgress> Progress { get; } = new();

        public List<TimeSpan> Delays { get; } = new();

        public FlashRun Run { get; }

        public int DriveDelayCount
        {
            get
            {
                lock (_driveDelays)
                {
                    return _driveDelayCount;
                }
            }
        }

        public FlashPhase[] Phases => [.. Progress.Select(p => p.Phase)];

        public Task<FlashHalfResult> FlashAsync(
            FirmwareHalf half,
            bool useTrigger,
            string? expectedConfigId,
            CancellationToken ct = default) =>
            Run.FlashHalfAsync(half, new byte[] { 1, 2, 3 }, useTrigger, expectedConfigId, new ListProgress(Progress), ct);

        private int _driveDelayCount;

        private Task RecordDelay(TimeSpan interval, CancellationToken ct)
        {
            Delays.Add(interval);
            return Task.CompletedTask;
        }

        private Task RecordDriveDelay(TimeSpan interval, CancellationToken ct)
        {
            lock (_driveDelays)
            {
                _driveDelayCount++;
            }

            // Instant, but the drive watcher still checks its token at every
            // loop top, which is what the cancellation test relies on.
            return Task.CompletedTask;
        }
    }

    /// <summary>Adds straight to the list — synchronous, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class ListProgress(List<FlashProgress> sink) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => sink.Add(value);
    }

    /// <summary>
    /// The mount table the watcher sees, from a scripted queue that keeps
    /// repeating its last entry; can cancel a caller's token at a numbered
    /// listing to simulate hot-plug timing.
    /// </summary>
    private sealed class ScriptedDriveSource(IReadOnlyList<Uf2Drive>[] snapshots) : IUf2DriveSource
    {
        private readonly Queue<IReadOnlyList<Uf2Drive>> _queue = new(snapshots);
        private readonly IReadOnlyList<Uf2Drive> _last = snapshots.Length > 0 ? snapshots[^1] : [];
        private int _calls;

        public CancellationTokenSource? CancelWith { get; set; }

        /// <summary>The 1-based listing call at which <see cref="CancelWith"/> fires; 0 = never.</summary>
        public int CancelOnCall { get; set; }

        public IReadOnlyList<Uf2Drive> List()
        {
            _calls++;
            if (CancelWith is not null && CancelOnCall > 0 && _calls == CancelOnCall)
            {
                CancelWith.Cancel();
            }

            return _queue.Count > 0 ? _queue.Dequeue() : _last;
        }
    }

    /// <summary>
    /// The keyboard the run may ask: a scripted trigger result (targets are
    /// recorded), a config-ID queue repeating its last answer (null when it
    /// was empty to begin with), and settable connection facts.
    /// </summary>
    private sealed class FakeKeyboard : IKeyboardFlashAccess
    {
        private string? _lastConfigId;
        private int _configIdCalls;

        public bool IsConnected { get; set; } = true;

        public KeyboardBus Bus { get; set; } = KeyboardBus.Usb;

        public int? BootloaderPeripheralSlots { get; set; } = 1;

        public ControlResult TriggerResult { get; set; } = new(ControlOutcome.Confirmed);

        public List<byte> TriggerTargets { get; } = new();

        public Queue<string?> ConfigIds { get; } = new();

        public int ConfigIdCalls => _configIdCalls;

        public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default)
        {
            TriggerTargets.Add(targetHalf);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(TriggerResult);
        }

        public Task<string?> QueryConfigIdAsync(CancellationToken ct = default)
        {
            _configIdCalls++;
            ct.ThrowIfCancellationRequested();

            // Repeating the last answer turns over-polling into a call-count
            // assert instead of an empty-queue crash.
            _lastConfigId = ConfigIds.Count > 0 ? ConfigIds.Dequeue() : _lastConfigId;
            return Task.FromResult(_lastConfigId);
        }
    }

    /// <summary>A copy that succeeds (or returns a settable error) and records the drive roots it was handed.</summary>
    private sealed class FakeCopy
    {
        public Uf2CopyResult Result { get; set; } = new(true, null);

        public List<string> Roots { get; } = new();

        public Task<Uf2CopyResult> Run(ReadOnlyMemory<byte> uf2, string driveRoot, CancellationToken ct)
        {
            Roots.Add(driveRoot);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
}
