using System.Buffers.Binary;
using LViz.App.Services;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;
using Xunit;

namespace LViz.Tests;

public class CapabilityControlTests
{
    private static byte[] Confirm(ushort reference, bool ok)
        => new byte[] { HidConstants.Outbound.Confirm, (byte)(reference & 0xFF), (byte)(reference >> 8), (byte)(ok ? 1 : 0) };

    private static ushort RefOf(byte[] report) => (ushort)(report[1] | (report[2] << 8));

    private static CapabilityControl LongTimeout() => new(TimeSpan.FromSeconds(5));

    // ─── Fire-and-forget (pointing + setBase) ────────────────────────────────

    [Fact]
    public async Task SendPointingAction_EmitsActionBytes_ReturnsSent()
    {
        var device = new FakeDevice();
        var control = LongTimeout();

        var result = await control.SendPointingActionAsync(device, HidConstants.PointingAction.DpiSet, 800);

        Assert.Equal(ControlOutcome.Sent, result.Outcome);
        Assert.True(result.Success);
        var sent = Assert.Single(device.Sent);
        Assert.Equal(HidConstants.PointingAction.DpiSet, sent[0]);
        Assert.Equal(800u, BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(1, 4)));
        Assert.Equal(HidConstants.ReportSize, sent.Length);
        Assert.False(device.HasReportSubscribers); // fire-and-forget never subscribes
    }

    [Fact]
    public async Task SetLayerBase_EmitsSetBaseBytes_ReturnsSent()
    {
        var device = new FakeDevice();
        var control = LongTimeout();

        var result = await control.SetLayerBaseAsync(device, layerIndex: 4);

        Assert.Equal(ControlOutcome.Sent, result.Outcome);
        var sent = Assert.Single(device.Sent);
        Assert.Equal(HidConstants.Inbound.SetLayerBase, sent[0]);
        Assert.Equal((byte)4, sent[1]);
    }

    [Fact]
    public async Task FireAndForget_DeadHandle_ReturnsFailed()
    {
        var device = new FakeDevice { ThrowOnSend = true };
        var control = LongTimeout();

        var result = await control.SetLayerBaseAsync(device, layerIndex: 1);

        Assert.Equal(ControlOutcome.Failed, result.Outcome);
        Assert.False(result.Success);
    }

    // ─── Confirm-flagged (activate / deactivate) ─────────────────────────────

    [Fact]
    public async Task ActivateLayer_EmitsActivateBytes_ConfirmResolves()
    {
        var device = new FakeDevice();
        var control = LongTimeout();

        // The report is written synchronously before the await on the ack, so it
        // is already recorded by the time the call returns its pending task.
        var pending = control.ActivateLayerAsync(device, layerIndex: 3);
        var sent = Assert.Single(device.Sent);
        Assert.Equal(HidConstants.Inbound.ActivateLayer, sent[0]);
        Assert.Equal((byte)3, sent[3]);

        device.RaiseReport(Confirm(RefOf(sent), ok: true));
        var result = await pending;

        Assert.Equal(ControlOutcome.Confirmed, result.Outcome);
        Assert.True(result.Success);
        Assert.False(device.HasReportSubscribers); // unsubscribed in finally
    }

    [Fact]
    public async Task DeactivateLayer_EmitsDeactivateBytes_ConfirmResolves()
    {
        var device = new FakeDevice();
        var control = LongTimeout();

        var pending = control.DeactivateLayerAsync(device, layerIndex: 2);
        var sent = Assert.Single(device.Sent);
        Assert.Equal(HidConstants.Inbound.DeactivateLayer, sent[0]);
        Assert.Equal((byte)2, sent[3]);

        device.RaiseReport(Confirm(RefOf(sent), ok: true));

        Assert.Equal(ControlOutcome.Confirmed, (await pending).Outcome);
    }

    [Fact]
    public async Task ActivateLayer_DeviceReportsNotOk_ReturnsFailed()
    {
        var device = new FakeDevice();
        var control = LongTimeout();

        var pending = control.ActivateLayerAsync(device, layerIndex: 1);
        device.RaiseReport(Confirm(RefOf(device.Sent[0]), ok: false));

        var result = await pending;
        Assert.Equal(ControlOutcome.Failed, result.Outcome);
        Assert.False(device.HasReportSubscribers);
    }

    [Fact]
    public async Task ActivateLayer_NoConfirm_TimesOut()
    {
        var device = new FakeDevice();
        var control = new CapabilityControl(TimeSpan.FromMilliseconds(50));

        var result = await control.ActivateLayerAsync(device, layerIndex: 5);

        Assert.Equal(ControlOutcome.TimedOut, result.Outcome);
        Assert.False(result.Success);
        Assert.False(device.HasReportSubscribers); // unsubscribed even on timeout
    }

    [Fact]
    public async Task ActivateLayer_StrayConfirmRefIgnored_ThenTimesOut()
    {
        var device = new FakeDevice();
        var control = new CapabilityControl(TimeSpan.FromMilliseconds(60));

        var pending = control.ActivateLayerAsync(device, layerIndex: 1);
        // A 0xF7 for a ref we never issued must not resolve the waiter.
        device.RaiseReport(Confirm((ushort)(RefOf(device.Sent[0]) + 1), ok: true));

        Assert.Equal(ControlOutcome.TimedOut, (await pending).Outcome);
    }

    [Fact]
    public async Task ActivateLayer_CallerCancellation_Throws()
    {
        var device = new FakeDevice();
        var control = LongTimeout();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => control.ActivateLayerAsync(device, layerIndex: 1, cts.Token));
    }
}
