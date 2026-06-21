using LViz.Core.Diagnostics;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;

namespace LViz.App.Services;

/// <summary>Outcome of one app-originated control action.</summary>
public enum ControlOutcome
{
    /// <summary>Fire-and-forget action (setBase / pointing) written to the device. No confirm expected.</summary>
    Sent,

    /// <summary>Confirm-flagged action (activate / deactivate) the device acked OK.</summary>
    Confirmed,

    /// <summary>Confirm-flagged action that never produced a 0xF7 ack within the timeout.</summary>
    TimedOut,

    /// <summary>The write threw (dead handle) or the device acked the action as failed.</summary>
    Failed,
}

/// <summary>
/// Result of one control action. <see cref="Detail"/> carries a human-readable
/// reason for the non-success outcomes (UI / diagnostics).
/// </summary>
public readonly record struct ControlResult(ControlOutcome Outcome, string? Detail = null)
{
    /// <summary>True when the action was delivered (fire-and-forget) or confirmed.</summary>
    public bool Success => Outcome is ControlOutcome.Sent or ControlOutcome.Confirmed;
}

/// <summary>
/// LViz acting as the <em>originator</em> of a capability action: it sends a
/// layer action (setBase / activate / deactivate) or a pointing action to a
/// chosen handler device and, for confirm-flagged actions, awaits the device's
/// 0xF7 ack via a <see cref="ConfirmTracker"/>. Mirrors the reference host's
/// setBase / activate / deactivate + confirm flow.
///
/// <para>This is the counterpart to <c>CapabilityRouter</c>'s forwarding: routing
/// relays a device's emitted trigger verbatim (emit byte == handle byte), whereas
/// control synthesizes a fresh report with <see cref="ActionReportBuilder"/> and
/// owns the ref bookkeeping. Operates over any <see cref="ICapabilityDevice"/>, so
/// the keyboard adapter and bus devices are driven the same way.</para>
/// </summary>
public interface ICapabilityControl
{
    /// <summary>Routable pointing action (0xE1/0xE2/0xE9/0xEB). Fire-and-forget — idempotent, not confirm-flagged.</summary>
    Task<ControlResult> SendPointingActionAsync(ICapabilityDevice target, byte actionByte, uint value, CancellationToken cancellationToken = default);

    /// <summary>core.rgb.set (0xD1): set the device's global RGB state. Absolute and
    /// idempotent — only the fields present on <paramref name="set"/> are applied.
    /// Fire-and-forget (the device's 0xD0 receipt is not awaited here).</summary>
    Task<ControlResult> SendRgbAsync(ICapabilityDevice target, RgbSet set, CancellationToken cancellationToken = default);

    /// <summary>setBase (0xF4): set the default layer. Absolute, fire-and-forget.</summary>
    Task<ControlResult> SetLayerBaseAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default);

    /// <summary>activate (0xF3): activate one layer. Confirm-flagged — awaits the 0xF7 ack.</summary>
    Task<ControlResult> ActivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default);

    /// <summary>deactivate (0xF2): deactivate one layer. Confirm-flagged — awaits the 0xF7 ack.</summary>
    Task<ControlResult> DeactivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default);
}

public sealed class CapabilityControl : ICapabilityControl
{
    private readonly TimeSpan _confirmTimeout;

    // One tracker for the whole surface: refs are globally unique within it
    // (NextFreeRef skips in-flight refs), so concurrent confirmable sends — even
    // to the same device, where both transient subscriptions see every 0xF7 —
    // can't cross-resolve: a stray ref is a no-op Resolve.
    private readonly ConfirmTracker _confirms = new();

    public CapabilityControl(TimeSpan? confirmTimeout = null)
        => _confirmTimeout = confirmTimeout ?? TimeSpan.FromSeconds(2);

    public Task<ControlResult> SendPointingActionAsync(ICapabilityDevice target, byte actionByte, uint value, CancellationToken cancellationToken = default)
        => SendFireAndForgetAsync(target, ActionReportBuilder.PointingAction(actionByte, value), cancellationToken);

    public Task<ControlResult> SendRgbAsync(ICapabilityDevice target, RgbSet set, CancellationToken cancellationToken = default)
        => SendFireAndForgetAsync(target, ActionReportBuilder.RgbSet(set), cancellationToken);

    public Task<ControlResult> SetLayerBaseAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default)
        => SendFireAndForgetAsync(target, ActionReportBuilder.SetLayerBase(layerIndex), cancellationToken);

    public Task<ControlResult> ActivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default)
        => SendConfirmableAsync(target, reference => ActionReportBuilder.ActivateLayer(reference, layerIndex), cancellationToken);

    public Task<ControlResult> DeactivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken cancellationToken = default)
        => SendConfirmableAsync(target, reference => ActionReportBuilder.DeactivateLayer(reference, layerIndex), cancellationToken);

    private static async Task<ControlResult> SendFireAndForgetAsync(ICapabilityDevice target, byte[] report, CancellationToken cancellationToken)
    {
        try
        {
            await target.SendReportAsync(report, cancellationToken).ConfigureAwait(false);
            return new ControlResult(ControlOutcome.Sent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancelled — propagate, not a device failure
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("CapCtl", $"control send to {target.DisplayName} failed: {ex.Message}");
            return new ControlResult(ControlOutcome.Failed, ex.Message);
        }
    }

    private async Task<ControlResult> SendConfirmableAsync(
        ICapabilityDevice target, Func<ushort, byte[]> buildReport, CancellationToken cancellationToken)
    {
        // Subscribe before sending so a fast ack can't be missed; the tracker is
        // armed (Register) before the report carrying the ref goes out.
        Action<ReadOnlyMemory<byte>> handler = report =>
        {
            var ack = RawHidProtocol.TryParseConfirm(report.Span);
            if (ack is not null) _confirms.Resolve(ack);
        };
        target.ReportReceived += handler;
        try
        {
            var (reference, completion) = _confirms.Register(_confirmTimeout, cancellationToken);
            await target.SendReportAsync(buildReport(reference), cancellationToken).ConfigureAwait(false);
            try
            {
                var ok = await completion.ConfigureAwait(false);
                return ok
                    ? new ControlResult(ControlOutcome.Confirmed)
                    : new ControlResult(ControlOutcome.Failed, "device reported the action failed");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ControlResult(ControlOutcome.TimedOut, "no confirmation within the timeout");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancelled — propagate, not a device failure
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("CapCtl", $"control send to {target.DisplayName} failed: {ex.Message}");
            return new ControlResult(ControlOutcome.Failed, ex.Message);
        }
        finally
        {
            target.ReportReceived -= handler;
        }
    }
}
