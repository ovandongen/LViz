using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="IpcCommandService"/>: the line-based CLI commands
/// (event/status/devices) the running app answers. Mirrors
/// <see cref="SignalDispatcherTests"/> for the event path (string id instead of
/// numeric), and checks the introspection commands' ok/payload shape.
/// </summary>
public class IpcCommandServiceTests
{
    private sealed class RecordingRunner : IPipelineRunner
    {
        public List<string> Ran { get; } = new();
        public ManualResetEventSlim Fired { get; } = new(false);

        public Task RunAsync(ActionPipeline pipeline, CancellationToken cancellationToken = default)
        {
            Ran.Add(pipeline.Name);
            Fired.Set();
            return Task.CompletedTask;
        }
    }

    private static (IpcCommandService Svc, FakeCapabilityRouter Router, RecordingRunner Runner) Build(UserSettings seed)
    {
        var settings = new InMemorySettingsService { Current = seed };
        var router = new FakeCapabilityRouter();
        var runner = new RecordingRunner();
        return (new IpcCommandService(settings, router, runner), router, runner);
    }

    private static UserSettings WithEventBinding(string id, string pipelineName) => new()
    {
        ActionPipelines = new List<ActionPipeline>
        {
            new(pipelineName, new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) }),
        },
        EventBindings = new List<EventBinding> { new(id, pipelineName) },
    };

    [Fact]
    public void BoundEvent_RunsItsPipeline_AndAcksOk()
    {
        var (svc, _, runner) = Build(WithEventBinding("ci:ok", "flash"));

        var ack = svc.Handle("event ci:ok");

        Assert.Equal("ok", ack);
        Assert.True(runner.Fired.Wait(TimeSpan.FromSeconds(2)), "bound event should run a pipeline");
        Assert.Equal("flash", Assert.Single(runner.Ran));
    }

    [Fact]
    public void UnboundEvent_RunsNothing_AndAcksErr()
    {
        var (svc, _, runner) = Build(WithEventBinding("ci:ok", "flash"));

        var ack = svc.Handle("event ci:fail");

        Assert.StartsWith("err", ack);
        Assert.False(runner.Fired.Wait(150));
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public void DanglingBinding_AcksErr()
    {
        var (svc, _, _) = Build(new UserSettings
        {
            EventBindings = new List<EventBinding> { new("ci:ok", "gone") },
        });

        var ack = svc.Handle("event ci:ok");

        Assert.StartsWith("err", ack);
    }

    [Fact]
    public void EventWithoutId_AcksErr()
    {
        var (svc, _, _) = Build(new UserSettings());

        Assert.StartsWith("err", svc.Handle("event"));
        Assert.StartsWith("err", svc.Handle("event    "));
    }

    [Fact]
    public void UnknownCommand_AcksErr()
    {
        var (svc, _, _) = Build(new UserSettings());

        Assert.StartsWith("err", svc.Handle("frobnicate now"));
        Assert.StartsWith("err", svc.Handle(""));
        Assert.StartsWith("err", svc.Handle(null));
    }

    [Fact]
    public void Status_ReportsRunningAndDevices()
    {
        var (svc, router, _) = Build(new UserSettings());
        router.DeviceList.Add(new RoutedDeviceInfo("k", "Glove80", true, null));
        router.DeviceList.Add(new RoutedDeviceInfo("p", "Ploopy Bean", false,
            new DeviceManifest("Ploopy Bean", "cfg", new[]
            {
                new Capability(CapabilityRole.Handles, CapabilityTier.Core, false, "core.pointing.dpi.set"),
            })));

        var status = svc.Handle("status");

        Assert.StartsWith("ok", status);
        Assert.Contains("devices: 2", status);
        Assert.Contains("Glove80", status);
        Assert.Contains("core.pointing.dpi.set", status);
    }

    [Fact]
    public void Devices_EmptyInventory_AcksOkWithNote()
    {
        var (svc, _, _) = Build(new UserSettings());

        var devices = svc.Handle("devices");

        Assert.StartsWith("ok", devices);
        Assert.Contains("no devices", devices);
    }

    [Fact]
    public void Devices_ListsManifestCapabilities()
    {
        var (svc, router, _) = Build(new UserSettings());
        router.DeviceList.Add(new RoutedDeviceInfo("p", "Ploopy Bean", false,
            new DeviceManifest("Ploopy Bean", "cfg-123", new[]
            {
                new Capability(CapabilityRole.Handles, CapabilityTier.Core, false, "core.pointing.dpi.set"),
                new Capability(CapabilityRole.Triggers, CapabilityTier.Core, false, "signal.fire"),
            })));

        var devices = svc.Handle("devices");

        Assert.Contains("Ploopy Bean", devices);
        Assert.Contains("key: p", devices);
        Assert.Contains("configId: cfg-123", devices);
        Assert.Contains("core.pointing.dpi.set", devices);
        Assert.Contains("signal.fire", devices);
    }
}
