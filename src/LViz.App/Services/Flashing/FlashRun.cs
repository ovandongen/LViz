using LViz.Core.Diagnostics;
using LViz.Core.Firmware;

namespace LViz.App.Services.Flashing;

/// <summary>
/// Which step of flashing one half the flow is in. The flash window's
/// view-model turns these into text — the run itself stays UI-free.
/// </summary>
public enum FlashPhase
{
    /// <summary>Sending the <c>0xB0 core.system.bootloader</c> command.</summary>
    Triggering,

    /// <summary>Waiting for the half's bootloader drive to mount.</summary>
    WaitingForDrive,

    /// <summary>Writing the <c>.uf2</c> onto the mounted bootloader drive.</summary>
    Copying,

    /// <summary>
    /// Waiting for the drive to unmount again — a UF2 bootloader flashes as
    /// the file arrives and reboots, which takes its drive with it.
    /// </summary>
    WaitingForRestart,

    /// <summary>Waiting for the flashed central to report its config ID again.</summary>
    CheckingBoard,
}

/// <summary>One step reported while <see cref="FlashRun"/> flashes one half.</summary>
/// <param name="Phase">The step the run is in.</param>
/// <param name="ShowManualHint">
/// True when the bootloader drive did not come by itself (or could not): the
/// flash window should show the manual instructions — put the half into its
/// bootloader by hand — while the run keeps waiting.
/// </param>
/// <param name="TriggerProblem">
/// The bootloader command's <c>Detail</c>/outcome text when the trigger was
/// not confirmed, otherwise null. For the log and a small note, not translated.
/// </param>
public sealed record FlashProgress(FlashPhase Phase, bool ShowManualHint, string? TriggerProblem);

/// <summary>How flashing one half ended.</summary>
public enum FlashOutcome
{
    /// <summary>
    /// The half flashed: its drive came, took the file and went; the central
    /// also reported its config ID again.
    /// </summary>
    Flashed,

    /// <summary>
    /// The copy finished and the drive went, but the central did not report
    /// within <see cref="FlashRun.ReportWait"/> — a late note for the flash
    /// window, not an error.
    /// </summary>
    FlashedNotReported,

    /// <summary>Copying onto the drive failed; the result's error carries the message.</summary>
    CopyFailed,

    /// <summary>
    /// The drive is still mounted <see cref="FlashRun.RestartWait"/> after the
    /// copy — a bootloader ignores a UF2 that isn't for its chip, so this is
    /// most likely firmware for another board.
    /// </summary>
    NotAccepted,

    /// <summary>
    /// The drive that appeared names the other side in its label (see
    /// <see cref="Uf2Drive.Side"/>), so nothing was copied: the wrong half is
    /// in its bootloader.
    /// </summary>
    WrongHalf,
}

/// <summary>
/// The end state of flashing one half. <paramref name="Error"/> is the copy
/// error for <see cref="FlashOutcome.CopyFailed"/>, the drive's label for
/// <see cref="FlashOutcome.WrongHalf"/>, null otherwise.
/// </summary>
public sealed record FlashHalfResult(FlashOutcome Outcome, string? Error);

/// <summary>
/// Flashes one half of a split board: enter the bootloader (by command or by
/// hand), wait for its drive, copy the <c>.uf2</c> onto it, wait for the half
/// to reboot, and — for the central — wait for the board to report its config
/// ID again. Steps are reported through <see cref="IProgress{T}"/> as
/// <see cref="FlashProgress"/>; there is no UI text here, the flash window
/// turns the phases into strings.
///
/// <para>The timing follows the firmware brief: a confirmed command reboots
/// the central within ~100 ms, so a drive that has not appeared within
/// <see cref="TriggerWait"/> is not coming by itself and the user has to
/// start the bootloader by hand (a peripheral's <c>ok</c> ack only means
/// "queued on the split link", so its drive appearing at all is the real
/// confirmation). The wait then continues until a drive shows up or the
/// caller cancels. A drive that was already mounted before the trigger never
/// counts — only a root that appears <em>after</em> the snapshot qualifies.</para>
/// </summary>
/// <param name="keyboard">The connected keyboard to trigger and re-check.</param>
/// <param name="drives">The watcher for bootloader drives appearing and going.</param>
/// <param name="copy">How to write the file; tests inject a fake.</param>
/// <param name="delay">How to wait between config-ID polls; tests inject an instant one.</param>
public sealed class FlashRun(
    IKeyboardFlashAccess keyboard,
    Uf2DriveWatcher drives,
    Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<Uf2CopyResult>>? copy = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>How long to wait for the drive after a confirmed trigger before asking for the manual bootloader.</summary>
    public static readonly TimeSpan TriggerWait = TimeSpan.FromSeconds(15);

    /// <summary>How long the drive may stay mounted after the copy before the flash counts as <see cref="FlashOutcome.NotAccepted"/>.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(30);

    /// <summary>How long the flashed central gets to report in again.</summary>
    public static readonly TimeSpan ReportWait = TimeSpan.FromSeconds(30);

    /// <summary>Distance between two config-ID polls.</summary>
    public static readonly TimeSpan ReportPoll = TimeSpan.FromSeconds(1);

    private readonly Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<Uf2CopyResult>> _copy =
        copy ?? ((b, root, ct) => Uf2Copier.CopyAsync(b, root, ct));

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>
    /// Whether the bootloader command can be sent for <paramref name="role"/>
    /// right now: the firmware refuses it unless the central is attached over
    /// USB (an <see cref="KeyboardBus.Unknown"/> bus passes — hidapi does not
    /// always know, and the device still refuses by itself if needed), and a
    /// peripheral needs the firmware to advertise at least one slot.
    /// </summary>
    public static bool CanTrigger(IKeyboardFlashAccess keyboard, FirmwareHalfRole role) =>
        keyboard.IsConnected
        && keyboard.Bus is KeyboardBus.Usb or KeyboardBus.Unknown
        && keyboard.BootloaderPeripheralSlots is { } slots
        && (role == FirmwareHalfRole.Central || slots >= 1);

    /// <summary>
    /// Flash <paramref name="half"/> with <paramref name="uf2"/>, reporting
    /// every step to <paramref name="progress"/>. With
    /// <paramref name="useTrigger"/> the bootloader command goes first
    /// (<see cref="BootloaderCommand.CentralTarget"/> for the central, slot 0
    /// for the other half of a normal split); without it, or when it isn't
    /// confirmed, the run reports the manual hint right away and waits for a
    /// drive the user mounts by hand. Once the drive is gone the peripheral is
    /// done; the central then gets <see cref="ReportWait"/> to answer with
    /// <paramref name="expectedConfigId"/> (or, when that is null, with any
    /// connection) — running out is <see cref="FlashOutcome.FlashedNotReported"/>,
    /// a note, not an error. Cancellation throws
    /// <see cref="OperationCanceledException"/> at any wait; only the soft
    /// outcomes come back as a result.
    /// </summary>
    public async Task<FlashHalfResult> FlashHalfAsync(
        FirmwareHalf half,
        ReadOnlyMemory<byte> uf2,
        bool useTrigger,
        string? expectedConfigId,
        IProgress<FlashProgress> progress,
        CancellationToken ct)
    {
        // Snapshot before the trigger: a drive already mounted here can never
        // count as "the half rebooted into its bootloader".
        var before = drives.Snapshot();

        void Report(FlashPhase phase, bool showManualHint, string? triggerProblem, string? driveLabel = null)
        {
            DiagnosticLog.Info(
                "Flash",
                $"half {half.Role}/{half.Side}: {phase}"
                + (showManualHint ? " (waiting for a manual bootloader)" : string.Empty)
                + (driveLabel is null ? string.Empty : $" on {driveLabel}"));
            progress.Report(new FlashProgress(phase, showManualHint, triggerProblem));
        }

        var triggerFailed = false;
        string? triggerProblem = null;
        if (useTrigger)
        {
            Report(FlashPhase.Triggering, false, null);
            var control = await keyboard.EnterBootloaderAsync(
                half.Role == FirmwareHalfRole.Central ? BootloaderCommand.CentralTarget : (byte)0, ct);
            if (control.Outcome != ControlOutcome.Confirmed)
            {
                triggerFailed = true;
                triggerProblem = control.Detail ?? control.Outcome.ToString();
                DiagnosticLog.Info(
                    "Flash", $"half {half.Role}/{half.Side}: trigger {control.Outcome}: {triggerProblem}");
            }
        }

        // Without a confirmed trigger nobody is going to reboot the half, so
        // the manual instructions start out on screen.
        var manual = !useTrigger || triggerFailed;
        Report(FlashPhase.WaitingForDrive, manual, triggerProblem);

        Uf2Drive? drive = null;
        if (!manual)
        {
            drive = await drives.WaitForNewAsync(before, TriggerWait, ct);
            if (drive is null)
            {
                Report(FlashPhase.WaitingForDrive, true, null);
            }
        }

        // The infinite wait only ends with a drive or with cancellation —
        // the null case can only be a cancelled watcher.
        drive ??= await drives.WaitForNewAsync(before, Timeout.InfiniteTimeSpan, ct)
            ?? throw new OperationCanceledException(ct);

        // A drive whose label names the other side is the wrong half in its
        // bootloader (a manual double-tap on the other half, say): copying
        // would put this half's firmware on it.
        if (drive.Side != FirmwareSide.Unknown && half.Side != FirmwareSide.Unknown && drive.Side != half.Side)
        {
            DiagnosticLog.Info(
                "Flash", $"half {half.Role}/{half.Side}: {drive.Label} is the {drive.Side} half's drive — not copying");
            return new FlashHalfResult(FlashOutcome.WrongHalf, drive.Label);
        }

        Report(FlashPhase.Copying, false, null, drive.Label);
        var copied = await _copy(uf2, drive.RootPath, ct);
        if (!copied.Ok)
        {
            DiagnosticLog.Info("Flash", $"half {half.Role}/{half.Side}: copy to {drive.Label} failed: {copied.Error}");
            return new FlashHalfResult(FlashOutcome.CopyFailed, copied.Error);
        }

        Report(FlashPhase.WaitingForRestart, false, null, drive.Label);
        if (!await drives.WaitGoneAsync(drive.RootPath, RestartWait, ct))
        {
            DiagnosticLog.Info(
                "Flash",
                $"half {half.Role}/{half.Side}: {drive.Label} still mounted after {RestartWait} — bootloader ignored the file?");
            return new FlashHalfResult(FlashOutcome.NotAccepted, null);
        }

        DiagnosticLog.Info("Flash", $"half {half.Role}/{half.Side}: {drive.Label} unmounted after the copy");
        if (half.Role == FirmwareHalfRole.Peripheral)
        {
            // The drive going is the peripheral's only confirmation — it has
            // no link to report its config ID over.
            return new FlashHalfResult(FlashOutcome.Flashed, null);
        }

        Report(FlashPhase.CheckingBoard, false, null, drive.Label);
        var waited = TimeSpan.Zero;
        while (waited < ReportWait)
        {
            var reported = expectedConfigId is not null
                ? string.Equals(await keyboard.QueryConfigIdAsync(ct), expectedConfigId, StringComparison.Ordinal)
                : keyboard.IsConnected;
            if (reported)
            {
                DiagnosticLog.Info("Flash", $"half {half.Role}/{half.Side}: the board reported in — flashed");
                return new FlashHalfResult(FlashOutcome.Flashed, null);
            }

            await _delay(ReportPoll, ct);
            waited += ReportPoll;
        }

        DiagnosticLog.Info("Flash", $"half {half.Role}/{half.Side}: the board did not report within {ReportWait}");
        return new FlashHalfResult(FlashOutcome.FlashedNotReported, null);
    }
}
