using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Protocol;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="SignalDispatcher"/>: a bound signal.fire id runs its
/// pipeline, an unbound id runs nothing, and a non-signal report is ignored.
/// Dispatch happens off the read thread (Task.Run), so the bound case waits on
/// the runner's signal.
/// </summary>
public class SignalDispatcherTests
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

    private static byte[] SignalFire(byte id)
    {
        var buf = new byte[32];
        buf[0] = HidConstants.Signal.Fire;
        buf[1] = id;
        return buf;
    }

    private static byte[] LayerState()
    {
        var buf = new byte[32];
        buf[0] = HidConstants.Outbound.LayerState;
        return buf;
    }

    private static (SignalDispatcher Dispatcher, FakeDevice Device, RecordingRunner Runner) Build(UserSettings seed)
    {
        var settings = new InMemorySettingsService { Current = seed };
        var router = new FakeCapabilityRouter();
        var device = new FakeDevice();
        router.DeviceList.Add(new RoutedDeviceInfo("k", "Keyboard", true, null));
        router.Lookup["k"] = device;
        var runner = new RecordingRunner();
        var dispatcher = new SignalDispatcher(router, settings, runner);
        dispatcher.Start();
        return (dispatcher, device, runner);
    }

    private static UserSettings WithBinding(int id, string pipelineName, string sourceKey = "k") => new()
    {
        ActionPipelines = new List<ActionPipeline>
        {
            new(pipelineName, new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) }),
        },
        SignalBindings = new List<SignalBinding> { new(id, sourceKey, pipelineName) },
    };

    [Fact]
    public void BoundSignal_RunsItsPipeline()
    {
        var (dispatcher, device, runner) = Build(WithBinding(7, "flash"));

        device.RaiseReport(SignalFire(7));

        Assert.True(runner.Fired.Wait(TimeSpan.FromSeconds(2)), "bound signal should run a pipeline");
        Assert.Equal("flash", Assert.Single(runner.Ran));
        dispatcher.Dispose();
    }

    [Fact]
    public void UnboundSignal_RunsNothing()
    {
        var (dispatcher, device, runner) = Build(WithBinding(7, "flash"));

        device.RaiseReport(SignalFire(9)); // no binding for 9

        Assert.False(runner.Fired.Wait(150));
        Assert.Empty(runner.Ran);
        dispatcher.Dispose();
    }

    [Fact]
    public void NonSignalReport_IsIgnored()
    {
        var (dispatcher, device, runner) = Build(WithBinding(7, "flash"));

        device.RaiseReport(LayerState());

        Assert.False(runner.Fired.Wait(150));
        Assert.Empty(runner.Ran);
        dispatcher.Dispose();
    }

    [Fact]
    public void SameId_DifferentSourceDevices_RunDistinctPipelines()
    {
        // signal 0 from the keyboard and signal 0 from the Bean are two bindings.
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                ActionPipelines = new List<ActionPipeline>
                {
                    new("kb", new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) }),
                    new("bean", new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) }),
                },
                SignalBindings = new List<SignalBinding>
                {
                    new(0, "k", "kb"),
                    new(0, "bean", "bean"),
                },
            },
        };
        var router = new FakeCapabilityRouter();
        var keyboard = new FakeDevice();
        var bean = new FakeDevice();
        router.DeviceList.Add(new RoutedDeviceInfo("k", "Keyboard", true, null));
        router.DeviceList.Add(new RoutedDeviceInfo("bean", "Ploopy Bean", false, null));
        router.Lookup["k"] = keyboard;
        router.Lookup["bean"] = bean;
        var runner = new RecordingRunner();
        var dispatcher = new SignalDispatcher(router, settings, runner);
        dispatcher.Start();

        bean.RaiseReport(SignalFire(0));

        Assert.True(runner.Fired.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal("bean", Assert.Single(runner.Ran));
        dispatcher.Dispose();
    }

    [Fact]
    public void Signal_FromUnboundSource_RunsNothing()
    {
        // A binding scoped to the keyboard ("k") must not fire on the Bean's signal.
        var settings = new InMemorySettingsService { Current = WithBinding(7, "flash", sourceKey: "k") };
        var router = new FakeCapabilityRouter();
        var bean = new FakeDevice();
        router.DeviceList.Add(new RoutedDeviceInfo("bean", "Ploopy Bean", false, null));
        router.Lookup["bean"] = bean;
        var runner = new RecordingRunner();
        var dispatcher = new SignalDispatcher(router, settings, runner);
        dispatcher.Start();

        bean.RaiseReport(SignalFire(7));

        Assert.False(runner.Fired.Wait(150));
        Assert.Empty(runner.Ran);
        dispatcher.Dispose();
    }

    [Fact]
    public void DanglingBinding_RunsNothing()
    {
        // Binding points at a pipeline name that no longer exists.
        var settings = new UserSettings
        {
            ActionPipelines = new List<ActionPipeline>(),
            SignalBindings = new List<SignalBinding> { new(7, "k", "gone") },
        };
        var (dispatcher, device, runner) = Build(settings);

        device.RaiseReport(SignalFire(7));

        Assert.False(runner.Fired.Wait(150));
        Assert.Empty(runner.Ran);
        dispatcher.Dispose();
    }
}
