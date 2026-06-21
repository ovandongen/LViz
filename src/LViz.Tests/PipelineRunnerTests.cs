using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="PipelineRunner"/>: steps run in order through the right
/// sink (keyboard push / capability control / host executor / delay), a
/// device-absent step is skipped without aborting the run, and a positive delay
/// is awaited before the following step.
/// </summary>
public class PipelineRunnerTests
{
    private sealed class RecordingHost(List<string> log) : IHostActionExecutor
    {
        public void Launch(string target) => log.Add($"launch:{target}");
        public void Shell(string command) => log.Add($"shell:{command}");
    }

    private sealed class RecordingControl(List<string> log) : ICapabilityControl
    {
        public Task<ControlResult> SendPointingActionAsync(ICapabilityDevice target, byte actionByte, uint value, CancellationToken ct = default)
        { log.Add($"pointing:{target.DisplayName}:{actionByte}:{value}"); return Task.FromResult(new ControlResult(ControlOutcome.Sent)); }
        public Task<ControlResult> SendRgbAsync(ICapabilityDevice target, RgbSet set, CancellationToken ct = default)
        { log.Add($"rgb:{target.DisplayName}"); return Task.FromResult(new ControlResult(ControlOutcome.Sent)); }
        public Task<ControlResult> SetLayerBaseAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { log.Add($"setBase:{layerIndex}"); return Task.FromResult(new ControlResult(ControlOutcome.Sent)); }
        public Task<ControlResult> ActivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { log.Add($"activate:{layerIndex}"); return Task.FromResult(new ControlResult(ControlOutcome.Confirmed)); }
        public Task<ControlResult> DeactivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { log.Add($"deactivate:{layerIndex}"); return Task.FromResult(new ControlResult(ControlOutcome.Confirmed)); }
    }

    private static (PipelineRunner Runner, List<string> Log, FakeCapabilityRouter Router) Build(
        params ActionPipeline[] library)
    {
        var log = new List<string>();
        var router = new FakeCapabilityRouter();
        router.Lookup["ball"] = new FakeDevice { DisplayName = "Bean" };
        var runner = new PipelineRunner(
            router, new RecordingControl(log), new RecordingHost(log), i => log.Add($"layer:{i}"),
            () => library);
        return (runner, log, router);
    }

    private static ActionPipeline Pipe(string name, params PipelineStep[] steps) => new(name, steps.ToList());
    private static PipelineStep Run(string refName) => new(PipelineStepKind.Pipeline, PipelineRef: refName);
    private static PipelineStep Layer(int i) => new(PipelineStepKind.KeyboardLayer, LayerIndex: i);

    [Fact]
    public async Task RunAsync_RunsStepsInOrder_ThroughEachSink()
    {
        var (runner, log, _) = Build();
        var pipeline = new ActionPipeline("p", new List<PipelineStep>
        {
            new(PipelineStepKind.KeyboardLayer, LayerIndex: 1),
            new(PipelineStepKind.Rgb, TargetDeviceKey: "ball", Rgb: new RgbSet(On: true)),
            new(PipelineStepKind.Pointing, TargetDeviceKey: "ball", PointingActionByte: 0xEB, PointingValue: 1),
            new(PipelineStepKind.Launch, LaunchTarget: "app"),
            new(PipelineStepKind.Shell, ShellCommand: "cmd"),
            new(PipelineStepKind.Delay, DelayMs: 0),
        });

        await runner.RunAsync(pipeline);

        Assert.Equal(
            new[] { "layer:1", "rgb:Bean", "pointing:Bean:235:1", "launch:app", "shell:cmd" },
            log);
    }

    [Fact]
    public async Task RunAsync_DeviceTargetedLayer_SendsChosenLayerAction()
    {
        var (runner, log, _) = Build();
        var pipeline = new ActionPipeline("p", new List<PipelineStep>
        {
            // Targeted layer steps go through CapabilityControl, not the push delegate.
            new(PipelineStepKind.KeyboardLayer, TargetDeviceKey: "ball", LayerIndex: 2, LayerAction: PipelineLayerAction.SetBase),
            new(PipelineStepKind.KeyboardLayer, TargetDeviceKey: "ball", LayerIndex: 3, LayerAction: PipelineLayerAction.Activate),
            new(PipelineStepKind.KeyboardLayer, TargetDeviceKey: "ball", LayerIndex: 3, LayerAction: PipelineLayerAction.Deactivate),
            // Empty target still uses the overlay-aware push delegate.
            new(PipelineStepKind.KeyboardLayer, LayerIndex: 0),
        });

        await runner.RunAsync(pipeline);

        Assert.Equal(new[] { "setBase:2", "activate:3", "deactivate:3", "layer:0" }, log);
    }

    [Fact]
    public async Task RunAsync_DeviceTargetedLayer_DefaultsToSetBase_WhenNoAction()
    {
        var (runner, log, _) = Build();
        var pipeline = new ActionPipeline("p", new List<PipelineStep>
        {
            new(PipelineStepKind.KeyboardLayer, TargetDeviceKey: "ball", LayerIndex: 4),
        });

        await runner.RunAsync(pipeline);

        Assert.Equal(new[] { "setBase:4" }, log);
    }

    [Fact]
    public async Task RunAsync_DeviceAbsent_SkipsStep_ButRunsTheRest()
    {
        var (runner, log, _) = Build();
        var pipeline = new ActionPipeline("p", new List<PipelineStep>
        {
            new(PipelineStepKind.Rgb, TargetDeviceKey: "missing", Rgb: new RgbSet(On: true)),
            new(PipelineStepKind.KeyboardLayer, LayerIndex: 2),
        });

        await runner.RunAsync(pipeline);

        Assert.Equal(new[] { "layer:2" }, log);
    }

    [Fact]
    public async Task RunAsync_PositiveDelay_AwaitsBeforeNextStep()
    {
        var (runner, log, _) = Build();
        var pipeline = new ActionPipeline("p", new List<PipelineStep>
        {
            new(PipelineStepKind.Delay, DelayMs: 30),
            new(PipelineStepKind.KeyboardLayer, LayerIndex: 3),
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.RunAsync(pipeline);
        sw.Stop();

        Assert.Equal(new[] { "layer:3" }, log);
        Assert.True(sw.ElapsedMilliseconds >= 20, $"delay step should have awaited, took {sw.ElapsedMilliseconds}ms");
    }

    // ─── Sub-pipelines ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SubPipeline_RunsNestedStepsInline()
    {
        var child = Pipe("child", Layer(7), Layer(8));
        var (runner, log, _) = Build(child);
        var parent = Pipe("parent", Layer(1), Run("child"), Layer(2));

        await runner.RunAsync(parent);

        Assert.Equal(new[] { "layer:1", "layer:7", "layer:8", "layer:2" }, log);
    }

    [Fact]
    public async Task RunAsync_NestedSubPipelines_RunToFullDepth()
    {
        var c = Pipe("c", Layer(3));
        var b = Pipe("b", Layer(2), Run("c"));
        var (runner, log, _) = Build(b, c);
        var a = Pipe("a", Layer(1), Run("b"));

        await runner.RunAsync(a);

        Assert.Equal(new[] { "layer:1", "layer:2", "layer:3" }, log);
    }

    [Fact]
    public async Task RunAsync_DanglingSubPipeline_SkipsButRunsTheRest()
    {
        var (runner, log, _) = Build(/* no "gone" pipeline */);
        var p = Pipe("p", Layer(1), Run("gone"), Layer(2));

        await runner.RunAsync(p);

        Assert.Equal(new[] { "layer:1", "layer:2" }, log);
    }

    [Fact]
    public async Task RunAsync_DirectSelfCycle_BreaksWithoutHanging()
    {
        // A references itself — the cyclic call is skipped, the rest still runs.
        var a = Pipe("a", Layer(1), Run("a"), Layer(2));
        var (runner, log, _) = Build(a);

        await runner.RunAsync(a).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "layer:1", "layer:2" }, log);
    }

    [Fact]
    public async Task RunAsync_IndirectCycle_BreaksAtReentry()
    {
        // a → b → a: b's call back into a is skipped (a already on the stack).
        var b = Pipe("b", Layer(2), Run("a"), Layer(3));
        var (runner, log, _) = Build(b);
        var a = Pipe("a", Layer(1), Run("b"), Layer(4));

        await runner.RunAsync(a).WaitAsync(TimeSpan.FromSeconds(2));

        // a:1 → b:2 → (a skipped) → b:3 → a:4
        Assert.Equal(new[] { "layer:1", "layer:2", "layer:3", "layer:4" }, log);
    }

    [Fact]
    public async Task RunAsync_DiamondReuse_RunsSharedPipelineEachTime()
    {
        // a → b → d and a → c → d. d is not a cycle; it runs once per path.
        var d = Pipe("d", Layer(9));
        var b = Pipe("b", Run("d"));
        var c = Pipe("c", Run("d"));
        var (runner, log, _) = Build(b, c, d);
        var a = Pipe("a", Run("b"), Run("c"));

        await runner.RunAsync(a);

        Assert.Equal(new[] { "layer:9", "layer:9" }, log);
    }
}
