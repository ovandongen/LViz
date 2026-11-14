using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.Flashing;
using LViz.Core.Diagnostics;
using LViz.Core.Firmware;

namespace LViz.App.ViewModels;

/// <summary>
/// The Flash and Done steps: the planned halves queued peripheral-first (the
/// central still runs firmware that can relay the bootloader command to it),
/// per half the instructions with the trigger or the manual way, the run's
/// progress text and failures with retry/skip, and at the end the Done
/// lines. GitHub-build flashes land in <see cref="FirmwareFlashHistory"/>;
/// file flashes never do, and a failed record only reaches the log.
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>Where the current half of the Flash step stands.</summary>
    public enum FlashHalfStage
    {
        /// <summary>The instructions are up; Start triggers, the manual way does not.</summary>
        Prepare,

        /// <summary>The half is flashing; Cancel aborts back to Prepare.</summary>
        Running,

        /// <summary>The half failed; Retry prepares it again, Skip marks it skipped and moves on.</summary>
        Failed,
    }

    // The run over the plan: _queue is _plan re-ordered peripheral-first,
    // _queueIndex the half on screen, _doneLines one line per half that
    // ended (flashed, not reported or skipped) in run order.
    private readonly List<(FirmwareHalf Half, byte[] Bytes)> _queue = [];
    private int _queueIndex;
    private readonly List<string> _doneLines = [];
    private bool _anyFlashed;

    // The current half's cancellation; replaced per run like _signInCts.
    private CancellationTokenSource? _runCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(ManualCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFlashCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryHalfCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipHalfCommand))]
    private FlashHalfStage _stage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canStart;

    [ObservableProperty]
    private string _prepareText = string.Empty;

    [ObservableProperty]
    private string? _reasonText;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string? _manualHintText;

    [ObservableProperty]
    private string _failureText = string.Empty;

    [ObservableProperty]
    private string _currentHalfLabel = string.Empty;

    [ObservableProperty]
    private string _flashHeaderText = string.Empty;

    /// <summary>One line per half that ended, in run order; the Done step's body.</summary>
    public IReadOnlyList<string> DoneLines => _doneLines;

    /// <summary>The half being prepared or run; null once the queue is through.</summary>
    private (FirmwareHalf Half, byte[] Bytes)? CurrentHalf =>
        _queueIndex < _queue.Count ? _queue[_queueIndex] : null;

    /// <summary>
    /// Queue the plan (peripheral first) and show its first half; called
    /// when <c>Step</c> becomes <see cref="FlashStep.Flash"/>.
    /// </summary>
    private void StartFlashRun()
    {
        _queue.Clear();
        _queue.AddRange(_plan.OrderByDescending(p => p.Half.Role == FirmwareHalfRole.Peripheral));
        _queueIndex = 0;
        _doneLines.Clear();
        _anyFlashed = false;
        if (_queue.Count > 0)
        {
            ShowCurrentHalf();
        }
    }

    /// <summary>Publish the current half's label and header, then its instructions.</summary>
    private void ShowCurrentHalf()
    {
        if (CurrentHalf is not { } item)
        {
            return;
        }

        CurrentHalfLabel = HalfLabel(item.Half, _isUnibody);
        FlashHeaderText = Loc.Instance.Format("Flash_FlashHeader", CurrentHalfLabel);
        EnterPrepare();
    }

    /// <summary>
    /// Show the half's instructions: re-read whether the bootloader trigger
    /// is possible right now, what to do, and — when the trigger is out —
    /// why. The run's transient texts start clean again.
    /// </summary>
    private void EnterPrepare()
    {
        FailureText = string.Empty;
        ProgressText = string.Empty;
        ManualHintText = null;
        Stage = FlashHalfStage.Prepare;
        if (CurrentHalf is not { } item)
        {
            return;
        }

        CanStart = FlashRun.CanTrigger(_keyboard, item.Half.Role);
        var side = SideLower(item.Half);
        if (CanStart)
        {
            ReasonText = null;
            PrepareText = item.Half.Role == FirmwareHalfRole.Central
                ? Loc.Instance.Format("Flash_PrepareCentral", side)
                : Loc.Instance.Format("Flash_PreparePeripheral", side, CentralSideLower());
        }
        else
        {
            PrepareText = Loc.Instance.Format("Flash_PrepareManual", side);
            ReasonText = TriggerReason(item.Half);
        }

        // Re-arm the buttons by hand too: the first half already finds the
        // stage at its Prepare default, so no Stage change fires the event.
        StartCommand.NotifyCanExecuteChanged();
        ManualCommand.NotifyCanExecuteChanged();
        CancelFlashCommand.NotifyCanExecuteChanged();
        RetryHalfCommand.NotifyCanExecuteChanged();
        SkipHalfCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The half's plain name in sentence form ("the left half", "the keyboard").</summary>
    private string SideLower(FirmwareHalf half)
    {
        if (_isUnibody)
        {
            return Loc.Instance["Flash_SideKeyboard"];
        }

        return half.Side switch
        {
            FirmwareSide.Left => Loc.Instance["Flash_SideLeft"],
            FirmwareSide.Right => Loc.Instance["Flash_SideRight"],
            _ => half.Role == FirmwareHalfRole.Central
                ? Loc.Instance["Flash_SideCentral"]
                : Loc.Instance["Flash_SideOther"],
        };
    }

    /// <summary>
    /// The config's central in sentence form for the peripheral's "keep it
    /// plugged in" instruction; falls back to "the central half" — a file
    /// flash of only the peripheral plans no central, but the board has one.
    /// </summary>
    private string CentralSideLower() =>
        _halves.FirstOrDefault(h => h.Role == FirmwareHalfRole.Central) is { } central
            ? SideLower(central)
            : Loc.Instance["Flash_SideCentral"];

    /// <summary>Why the bootloader trigger is out for this half; texts per the spec's order.</summary>
    private string? TriggerReason(FirmwareHalf half)
    {
        if (!_keyboard.IsConnected)
        {
            return Loc.Instance["Flash_ReasonNotConnected"];
        }

        if (_keyboard.Bus == KeyboardBus.Bluetooth)
        {
            return Loc.Instance["Flash_ReasonBluetooth"];
        }

        if (_keyboard.BootloaderPeripheralSlots is null)
        {
            return Loc.Instance["Flash_ReasonNoSupport"];
        }

        if (half.Role == FirmwareHalfRole.Peripheral && _keyboard.BootloaderPeripheralSlots == 0)
        {
            return Loc.Instance["Flash_ReasonNoSupport"];
        }

        return null; // CanStart was true — the reason is only shown when it isn't
    }

    private bool CanStartFlash() => Stage == FlashHalfStage.Prepare && CanStart;

    private bool CanFlashManually() => Stage == FlashHalfStage.Prepare;

    private bool CanCancelFlash() => Stage == FlashHalfStage.Running;

    private bool CanRetryHalf() => Stage == FlashHalfStage.Failed;

    private bool CanSkipHalf() => Stage == FlashHalfStage.Failed;

    /// <summary>Flash the half by command: it goes into its bootloader by itself.</summary>
    [RelayCommand(CanExecute = nameof(CanStartFlash))]
    private Task Start() => RunCurrentHalfAsync(useTrigger: true);

    /// <summary>Flash the half with the user putting it into its bootloader by hand.</summary>
    [RelayCommand(CanExecute = nameof(CanFlashManually))]
    private Task Manual() => RunCurrentHalfAsync(useTrigger: false);

    /// <summary>Abort the running half; the run lands back on Prepare when the cancel lands.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelFlash))]
    private void CancelFlash() => _runCts?.Cancel();

    /// <summary>Back to the instructions for the same half.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryHalf))]
    private void RetryHalf() => EnterPrepare();

    /// <summary>Mark the failed half skipped and move on to the next one.</summary>
    [RelayCommand(CanExecute = nameof(CanSkipHalf))]
    private void SkipHalf()
    {
        _doneLines.Add(Loc.Instance.Format("Flash_DoneSkipped", CurrentHalfLabel));
        Advance();
    }

    private async Task RunCurrentHalfAsync(bool useTrigger)
    {
        if (CurrentHalf is not { } item)
        {
            return;
        }

        FailureText = string.Empty;
        ProgressText = string.Empty;
        ManualHintText = null;
        Stage = FlashHalfStage.Running;

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            var result = await _run.FlashHalfAsync(
                item.Half, item.Bytes, useTrigger, _facts?.ConfigId, new RunProgress(this), ct);
            switch (result.Outcome)
            {
                case FlashOutcome.Flashed:
                case FlashOutcome.FlashedNotReported:
                    _anyFlashed = true;
                    RecordHistory(item.Half);
                    _doneLines.Add(Loc.Instance.Format(
                        result.Outcome == FlashOutcome.Flashed ? "Flash_DoneFlashed" : "Flash_DoneNotReported",
                        CurrentHalfLabel));
                    Advance();
                    break;
                case FlashOutcome.CopyFailed:
                    Stage = FlashHalfStage.Failed;
                    FailureText = Loc.Instance.Format("Flash_FailedCopy", result.Error ?? string.Empty);
                    break;
                case FlashOutcome.NotAccepted:
                    Stage = FlashHalfStage.Failed;
                    FailureText = Loc.Instance["Flash_FailedNotAccepted"];
                    break;
                case FlashOutcome.WrongHalf:
                    Stage = FlashHalfStage.Failed;
                    FailureText = Loc.Instance.Format(
                        "Flash_FailedWrongHalf", result.Error ?? string.Empty, SideLower(item.Half));
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            EnterPrepare(); // CancelFlashCommand — a cancelled half leaves no Done line
        }
    }

    /// <summary>Only GitHub builds are recorded; a failed record never interrupts the run.</summary>
    private void RecordHistory(FirmwareHalf half)
    {
        if (_planSha is not { } sha)
        {
            return; // a file flash is not recorded
        }

        try
        {
            _history.Record(_context.ConfigKey, half.Role, sha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.Warn("Flash", $"flash history for {half.Role} failed: {e.Message}");
        }
    }

    /// <summary>The half ended (flashed, not reported or skipped): the next one, or the Done step.</summary>
    private void Advance()
    {
        if (_queueIndex + 1 < _queue.Count)
        {
            _queueIndex++;
            ShowCurrentHalf();
            return;
        }

        Flashed = _anyFlashed;
        Step = FlashStep.Done;
    }

    /// <summary>A step from the run: the progress line and the manual hint for the current half.</summary>
    private void OnRunProgress(FlashProgress progress)
    {
        if (CurrentHalf is not { } item)
        {
            return;
        }

        var side = SideLower(item.Half);
        ProgressText = progress.Phase switch
        {
            FlashPhase.Triggering => Loc.Instance.Format("Flash_Triggering", side),
            FlashPhase.WaitingForDrive => Loc.Instance.Format("Flash_WaitingForDrive", side),
            FlashPhase.Copying => Loc.Instance["Flash_Copying"],
            FlashPhase.WaitingForRestart => Loc.Instance.Format("Flash_WaitingForRestart", side),
            _ => Loc.Instance["Flash_CheckingBoard"],
        };
        ManualHintText = progress.ShowManualHint ? ManualHint(side) : null;
    }

    /// <summary>How to start the bootloader by hand; on Linux the drive may also need mounting.</summary>
    private static string ManualHint(string side)
    {
        var hint = Loc.Instance.Format("Flash_ManualHint", side);
        return OperatingSystem.IsLinux()
            ? hint + " " + Loc.Instance["Flash_MountHint"]
            : hint;
    }

    /// <summary>
    /// The run's progress sink; a plain callback is enough —
    /// <see cref="FlashRun.FlashHalfAsync"/> reports inline and the run
    /// awaits on the caller's context.
    /// </summary>
    private sealed class RunProgress(FlashViewModel owner) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => owner.OnRunProgress(value);
    }
}
