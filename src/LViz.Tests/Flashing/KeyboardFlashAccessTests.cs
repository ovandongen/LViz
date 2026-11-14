using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.Core.Firmware;
using LViz.Core.Layout;
using Xunit;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;
using ZmkHidProtocol.Transport;

namespace LViz.Tests.Flashing;

/// <summary>
/// The four answers the flash flow takes from the connected keyboard before
/// sending <c>0xB0</c>: connection, bus, bootloader slot count from the
/// keyboard's router manifest, and the config ID — plus the guarded send itself.
/// </summary>
public class KeyboardFlashAccessTests
{
    /// <summary>
    /// In-memory <see cref="IHidPipeline"/> double (shape as
    /// <c>KeyboardCapabilityDeviceTests.FakeHidPipeline</c>): settable connection
    /// / path / config-ID answer, records writes, and lets a test push the
    /// 0xF7 confirm back through <see cref="RaiseReport"/>.
    /// </summary>
    private sealed class FakeHid : IHidPipeline
    {
#pragma warning disable CS0067 // not raised — the flash access never listens to these
        public event Action<int>? ActiveLayerChanged;
        public event Action<int, bool>? KeyPositionEvent;
        public event Action? ConnectionChanged;
#pragma warning restore CS0067
        public event Action<ReadOnlyMemory<byte>>? ReportReceived;

        public bool IsConnected { get; set; }
        public string? OpenDevicePath { get; set; }
        public string? ConfigIdAnswer { get; set; }
        public Exception? ConfigIdError { get; set; }
        public bool CancelConfigIdQuery { get; set; }
        public TimeSpan? LastConfigIdTimeout { get; private set; }

        public List<byte[]> Sent { get; } = new();

        public string SourceLabel { get; set; } = "Raw HID (Fake)";
        public int CurrentLayer { get; set; }
        public bool IsLastChangeAppControlled { get; set; }
        public IDeviceMatcher KeyboardMatcher { get; set; } = new NullMatcher();

        public void Start() { }
        public void Stop() { }
        public void SetActiveProfile(IKeyboardProfile profile) { }
        public void PushLayer(int index) { }
        public void PushLayerSync(int index, TimeSpan timeout) { }
        public Task<DeviceInfo?> QueryDeviceInfoAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<DeviceInfo?>(null);

        public Task<string?> QueryConfigIdAsync(TimeSpan timeout, CancellationToken ct)
        {
            LastConfigIdTimeout = timeout;
            if (CancelConfigIdQuery) return Task.FromCanceled<string?>(ct);
            if (ConfigIdError is not null) return Task.FromException<string?>(ConfigIdError);
            return Task.FromResult(ConfigIdAnswer);
        }

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

    private static DeviceManifest Manifest(params string[] capabilityIds)
        => new("Keyboard", null, capabilityIds
            .Select(id => new Capability(CapabilityRole.Handles, CapabilityTier.Core, false, id))
            .ToList());

    private static FakeCapabilityRouter RouterWith(bool isKeyboard, params string[] capabilityIds)
    {
        var router = new FakeCapabilityRouter();
        router.DeviceList.Add(new RoutedDeviceInfo(
            isKeyboard ? "kb" : "bean", isKeyboard ? "Keyboard" : "Ploopy Bean",
            isKeyboard, Manifest(capabilityIds)));
        return router;
    }

    private static KeyboardFlashAccess Access(
        FakeHid hid,
        ICapabilityRouter? router = null,
        ICapabilityControl? control = null,
        Func<string, KeyboardBus>? busOfPath = null)
        => new(hid, router ?? new FakeCapabilityRouter(),
            control ?? new CapabilityControl(TimeSpan.FromMilliseconds(50)), busOfPath);

    // ─── Bus ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bus_NotConnected_IsNone_AndDoesNotLookUp()
    {
        var calls = new List<string>();
        var hid = new FakeHid { IsConnected = false, OpenDevicePath = "p1" };

        var bus = Access(hid, busOfPath: p => { calls.Add(p); return KeyboardBus.Usb; }).Bus;

        Assert.Equal(KeyboardBus.None, bus);
        Assert.Empty(calls);
    }

    [Fact]
    public void Bus_Connected_ResolvesOpenPathOnEveryRead()
    {
        var calls = new List<string>();
        var hid = new FakeHid { IsConnected = true, OpenDevicePath = "p1" };
        var access = Access(hid, busOfPath: p => { calls.Add(p); return KeyboardBus.Usb; });

        var first = access.Bus;
        var second = access.Bus;

        Assert.Equal(KeyboardBus.Usb, first);
        Assert.Equal(KeyboardBus.Usb, second);
        Assert.Equal(new[] { "p1", "p1" }, calls); // re-evaluated, not cached
    }

    [Fact]
    public void Bus_BluetoothPath_IsBluetooth()
    {
        var hid = new FakeHid { IsConnected = true, OpenDevicePath = "p1" };

        var bus = Access(hid, busOfPath: _ => KeyboardBus.Bluetooth).Bus;

        Assert.Equal(KeyboardBus.Bluetooth, bus);
    }

    // ─── BusFor: the rule over one hidapi enumeration ────────────────────────

    private static KeyboardFlashAccess.HidEntry Entry(
        string path, HidApi.BusType bus, string product = "Cosmos82", ushort vid = 0x1D50, ushort pid = 0x615E) =>
        new(path, vid, pid, product, bus);

    [Fact]
    public void BusFor_OpenEntryOnUsb_IsUsb() =>
        Assert.Equal(KeyboardBus.Usb, KeyboardFlashAccess.BusFor("usb", [Entry("usb", HidApi.BusType.Usb)]));

    [Fact]
    public void BusFor_BluetoothWithAUsbTwin_IsUsb()
    {
        // A central that is paired and plugged in at once shows up twice, and
        // LViz may talk to it over Bluetooth; the firmware only needs USB up.
        var entries = new[] { Entry("ble", HidApi.BusType.Bluetooth), Entry("usb", HidApi.BusType.Usb) };

        Assert.Equal(KeyboardBus.Usb, KeyboardFlashAccess.BusFor("ble", entries));
    }

    [Fact]
    public void BusFor_BluetoothOnly_IsBluetooth() =>
        Assert.Equal(KeyboardBus.Bluetooth, KeyboardFlashAccess.BusFor("ble", [Entry("ble", HidApi.BusType.Bluetooth)]));

    [Fact]
    public void BusFor_UsbEntryOfAnotherKeyboard_DoesNotCount()
    {
        // Upstream ZMK boards share 1D50:615E: only the same product name is a twin.
        var entries = new[]
        {
            Entry("ble", HidApi.BusType.Bluetooth),
            Entry("other", HidApi.BusType.Usb, product: "Slabwing"),
            Entry("moergo", HidApi.BusType.Usb, vid: 0x16C0, pid: 0x27DB),
        };

        Assert.Equal(KeyboardBus.Bluetooth, KeyboardFlashAccess.BusFor("ble", entries));
    }

    [Fact]
    public void BusFor_NoEntryForThePath_IsUnknown() =>
        Assert.Equal(KeyboardBus.Unknown, KeyboardFlashAccess.BusFor("gone", [Entry("usb", HidApi.BusType.Usb)]));

    // ─── BootloaderPeripheralSlots ───────────────────────────────────────────

    [Fact]
    public void BootloaderPeripheralSlots_KeyboardManifestWithBootloader_IsCount()
    {
        var hid = new FakeHid { IsConnected = true };
        var router = RouterWith(true, "core.layer.state", "core.system.bootloader/1");

        Assert.Equal(1, Access(hid, router).BootloaderPeripheralSlots);
    }

    [Fact]
    public void BootloaderPeripheralSlots_WithoutBootloaderCapability_IsNull()
    {
        var hid = new FakeHid { IsConnected = true };
        var router = RouterWith(true, "core.layer.state");

        Assert.Null(Access(hid, router).BootloaderPeripheralSlots);
    }

    [Fact]
    public void BootloaderPeripheralSlots_NonKeyboardEntryWithCapability_IsNull()
    {
        var hid = new FakeHid { IsConnected = true };
        var router = new FakeCapabilityRouter();
        router.DeviceList.Add(new RoutedDeviceInfo("kb", "Keyboard", true, Manifest("core.layer.state")));
        router.DeviceList.Add(new RoutedDeviceInfo("bean", "Ploopy Bean", false, Manifest("core.system.bootloader/2")));

        Assert.Null(Access(hid, router).BootloaderPeripheralSlots);
    }

    [Fact]
    public void BootloaderPeripheralSlots_NotConnected_IsNull()
    {
        var hid = new FakeHid { IsConnected = false };
        var router = RouterWith(true, "core.system.bootloader/1");

        Assert.Null(Access(hid, router).BootloaderPeripheralSlots);
    }

    // ─── EnterBootloader ─────────────────────────────────────────────────────

    [Fact]
    public async Task EnterBootloader_NotConnected_FailsWithoutSending()
    {
        var hid = new FakeHid { IsConnected = false };

        var result = await Access(hid).EnterBootloaderAsync(BootloaderCommand.CentralTarget);

        Assert.Equal(ControlOutcome.Failed, result.Outcome);
        Assert.Equal("keyboard not connected", result.Detail);
        Assert.Empty(hid.Sent);
    }

    [Fact]
    public async Task EnterBootloader_Connected_SendsPacketAndConfirms()
    {
        var hid = new FakeHid { IsConnected = true, OpenDevicePath = "p1" };

        var pending = Access(hid).EnterBootloaderAsync(BootloaderCommand.CentralTarget);

        var sent = Assert.Single(hid.Sent);
        Assert.Equal(HidConstants.ReportSize, sent.Length);
        Assert.Equal(BootloaderCommand.Opcode, sent[0]);
        Assert.Equal(BootloaderCommand.CentralTarget, sent[3]);
        var reference = (ushort)(sent[1] | (sent[2] << 8));
        Assert.Equal(BootloaderCommand.Build(reference, BootloaderCommand.CentralTarget), sent);

        // 0xF7 confirm echoing the ref (uint16 LE at bytes 1-2), ok = 1.
        hid.RaiseReport(new byte[] { HidConstants.Outbound.Confirm, (byte)(reference & 0xFF), (byte)(reference >> 8), 1 });

        var result = await pending;
        Assert.Equal(ControlOutcome.Confirmed, result.Outcome);
        Assert.True(result.Success);
    }

    // ─── QueryConfigId ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryConfigId_ReturnsPipelineAnswer_WithOneSecondBudget()
    {
        var hid = new FakeHid { ConfigIdAnswer = "cfg-42" };

        var configId = await Access(hid).QueryConfigIdAsync();

        Assert.Equal("cfg-42", configId);
        Assert.Equal(TimeSpan.FromSeconds(1), hid.LastConfigIdTimeout);
    }

    [Fact]
    public async Task QueryConfigId_ThrowingPipeline_ReturnsNull()
    {
        var hid = new FakeHid { ConfigIdError = new IOException("handle dead") };

        Assert.Null(await Access(hid).QueryConfigIdAsync());
    }

    [Fact]
    public async Task QueryConfigId_CallerCancellation_Propagates()
    {
        var hid = new FakeHid { CancelConfigIdQuery = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Access(hid).QueryConfigIdAsync(cts.Token));
    }
}
