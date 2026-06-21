using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;

namespace LViz.Tests;

/// <summary>Settings service over a plain in-memory <see cref="UserSettings"/>.</summary>
internal sealed class InMemorySettingsService : ISettingsService
{
    public UserSettings Current { get; set; } = new();
    public UserSettings Load() => Current;
    public void Save(UserSettings settings) => Current = settings;
}

/// <summary>
/// In-memory <see cref="ICapabilityDevice"/>: records every written report and
/// lets a test push an inbound report back (to deliver a 0xF7 confirm).
/// <see cref="ThrowOnSend"/> simulates a dead handle.
/// </summary>
internal sealed class FakeDevice : ICapabilityDevice
{
    public string DisplayName { get; init; } = "Fake";
    public string TransportKey { get; init; } = "fake:0000:0000";
    public List<byte[]> Sent { get; } = new();
    public bool ThrowOnSend { get; init; }
    public bool HasReportSubscribers => ReportReceived is not null;

    public event Action<ReadOnlyMemory<byte>>? ReportReceived;

    public ValueTask SendReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnSend) throw new IOException("device gone");
        Sent.Add(report.ToArray());
        return ValueTask.CompletedTask;
    }

    public void RaiseReport(byte[] report) => ReportReceived?.Invoke(report);
}

/// <summary>
/// In-memory <see cref="ICapabilityRouter"/>: a fixed device inventory +
/// stable-key lookup, with a manual <see cref="RaiseRoutesChanged"/> for
/// subscription-reconcile tests. Discovery/forwarding are no-ops.
/// </summary>
internal sealed class FakeCapabilityRouter : ICapabilityRouter
{
    public List<RoutedDeviceInfo> DeviceList { get; } = new();
    public Dictionary<string, ICapabilityDevice> Lookup { get; } = new();

    public IReadOnlyList<RoutedDeviceInfo> Devices => DeviceList;
    public bool IsRoutingAvailable => false;
    public ICapabilityDevice? FindDevice(string stableKey) => Lookup.GetValueOrDefault(stableKey);

    public string? StableKeyFor(ICapabilityDevice device)
        => Lookup.FirstOrDefault(kv => ReferenceEquals(kv.Value, device)).Key;

    public event Action? RoutesChanged;
    public void RaiseRoutesChanged() => RoutesChanged?.Invoke();

    public void Start() { }
    public void Stop() { }
    public void RefreshConfig() { }
    public Task RescanAsync() => Task.CompletedTask;
    public void Dispose() { }
}
