using LViz.App.Services;
using LViz.Core.Layout;
using ZmkHidProtocol.Protocol;
using ZmkHidProtocol.Transport;
using Xunit;

namespace LViz.Tests;

public class KeyboardCapabilityDeviceTests
{
    /// <summary>
    /// In-memory <see cref="IHidPipeline"/> double. Records reports written via
    /// <see cref="SendReportAsync"/> and lets a test push inbound reports back
    /// through <see cref="RaiseReport"/>. Everything else is an inert stub — the
    /// adapter only touches reports, the source label, and the send path.
    /// </summary>
    private sealed class FakeHidPipeline : IHidPipeline
    {
        // Required by IHidPipeline but irrelevant to the adapter, which only
        // touches reports / source label / the send path.
#pragma warning disable CS0067
        public event Action<int>? ActiveLayerChanged;
        public event Action<int, bool>? KeyPositionEvent;
        public event Action? ConnectionChanged;
#pragma warning restore CS0067
        public event Action<ReadOnlyMemory<byte>>? ReportReceived;

        public bool IsConnected { get; set; }
        public string SourceLabel { get; set; } = "";
        public int CurrentLayer { get; set; }
        public bool IsLastChangeAppControlled { get; set; }
        public string? OpenDevicePath { get; set; }
        public IDeviceMatcher KeyboardMatcher { get; set; } = new NullMatcher();

        public List<byte[]> Sent { get; } = new();
        public bool HasReportSubscribers => ReportReceived is not null;

        public void Start() { }
        public void Stop() { }
        public void SetActiveProfile(IKeyboardProfile profile) { }
        public void PushLayer(int index) { }
        public void PushLayerSync(int index, TimeSpan timeout) { }
        public Task<DeviceInfo?> QueryDeviceInfoAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<DeviceInfo?>(null);
        public Task<string?> QueryConfigIdAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<string?>(null);

        public ValueTask SendReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct)
        {
            Sent.Add(report.ToArray());
            return ValueTask.CompletedTask;
        }

        public void Dispose() { }

        public void RaiseReport(ReadOnlyMemory<byte> report) => ReportReceived?.Invoke(report);

        private sealed class NullMatcher : IDeviceMatcher
        {
            public bool Matches(int vendorId, int productId, string? productName) => false;
            public bool MatchesName(string? productName) => false;
        }
    }

    [Fact]
    public void ReportReceived_ForwardsReportsFromPipeline()
    {
        var pipeline = new FakeHidPipeline();
        var device = new KeyboardCapabilityDevice(pipeline);
        var received = new List<byte[]>();
        device.ReportReceived += r => received.Add(r.ToArray());

        pipeline.RaiseReport(new byte[] { 0xF8, 0x01, 0x02 });

        Assert.Single(received);
        Assert.Equal(new byte[] { 0xF8, 0x01, 0x02 }, received[0]);
    }

    [Fact]
    public void ReportReceived_Unsubscribe_UnwiresThePipeline()
    {
        var pipeline = new FakeHidPipeline();
        var device = new KeyboardCapabilityDevice(pipeline);
        var received = new List<byte[]>();
        Action<ReadOnlyMemory<byte>> handler = r => received.Add(r.ToArray());

        device.ReportReceived += handler;
        device.ReportReceived -= handler;

        Assert.False(pipeline.HasReportSubscribers);
        pipeline.RaiseReport(new byte[] { 0xF8 });
        Assert.Empty(received);
    }

    [Fact]
    public async Task SendReportAsync_DelegatesToPipeline()
    {
        var pipeline = new FakeHidPipeline();
        var device = new KeyboardCapabilityDevice(pipeline);

        await device.SendReportAsync(new byte[] { 0xF9, 0x00 }, CancellationToken.None);

        Assert.Single(pipeline.Sent);
        Assert.Equal(new byte[] { 0xF9, 0x00 }, pipeline.Sent[0]);
    }

    [Fact]
    public void DisplayName_UsesPipelineSourceLabel_WhenPresent()
    {
        var pipeline = new FakeHidPipeline { SourceLabel = "Raw HID (Corne)" };
        var device = new KeyboardCapabilityDevice(pipeline);

        Assert.Equal("Raw HID (Corne)", device.DisplayName);
    }

    [Fact]
    public void DisplayName_FallsBackToKeyboard_WhenLabelEmpty()
    {
        var pipeline = new FakeHidPipeline { SourceLabel = "" };
        var device = new KeyboardCapabilityDevice(pipeline);

        Assert.Equal("Keyboard", device.DisplayName);
    }

    [Fact]
    public void TransportKey_IsStableConstant_IndependentOfOpenPath()
    {
        var device = new KeyboardCapabilityDevice(new FakeHidPipeline { OpenDevicePath = "/dev/hidraw3" });

        Assert.Equal("keyboard", device.TransportKey);
    }
}
