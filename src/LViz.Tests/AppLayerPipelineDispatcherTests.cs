using LViz.App.Services;
using LViz.Core.Layout;
using LViz.Core.Settings;
using Xunit;
using ZmkHidProtocol.ActiveWindow;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="AppLayerPipelineDispatcher"/>: an auto-switch lifecycle
/// moment runs the bound pipeline, an empty process filter matches any app, a
/// process-scoped filter is exact, moment mismatches run nothing, and multiple
/// bindings on one moment all fire. Enter/Leave fire synchronously from the
/// engine's ActiveWindow setter; the run itself is off-thread (Task.Run), so the
/// positive cases wait on the runner's semaphore. Exit/Re-enter go through
/// Dispatcher.UIThread.Post (unavailable in xUnit) and aren't covered here.
/// </summary>
public class AppLayerPipelineDispatcherTests
{
    private sealed class RecordingRunner : IPipelineRunner
    {
        private readonly object _lock = new();
        public List<string> Ran { get; } = new();
        public SemaphoreSlim Ran1 { get; } = new(0);

        public Task RunAsync(ActionPipeline pipeline, CancellationToken cancellationToken = default)
        {
            lock (_lock) Ran.Add(pipeline.Name);
            Ran1.Release();
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lock) return Ran.ToList();
        }
    }

    private static ActiveWindowInfo Window(string process) => new(process, null, null);

    private static (AutoSwitchEngine engine, RecordingRunner runner, AppLayerPipelineDispatcher dispatcher) Build(
        IReadOnlyList<AppLayerRule> rules,
        IReadOnlyList<AppLayerBinding> bindings,
        params string[] pipelineNames)
    {
        var profile = new CorneProfile();
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                AutoSwitchKeyboardLayer = true,
                AppLayerRules = new Dictionary<string, List<AppLayerRule>> { [profile.Id] = rules.ToList() },
                AutoSwitchFallback = new Dictionary<string, AutoSwitchFallbackMode> { [profile.Id] = AutoSwitchFallbackMode.Base },
                ActionPipelines = pipelineNames
                    .Select(n => new ActionPipeline(n, new List<PipelineStep> { new(PipelineStepKind.Delay, DelayMs: 0) }))
                    .ToList(),
                AppLayerBindings = bindings.ToList(),
            },
        };
        var engine = new AutoSwitchEngine(settings, activeWindowMonitor: null, () => 0, profile);
        var runner = new RecordingRunner();
        var dispatcher = new AppLayerPipelineDispatcher(engine, settings, runner);
        return (engine, runner, dispatcher);
    }

    [Fact]
    public void Enter_RunsBoundPipeline()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Enter, "flash") },
            "flash");

        engine.ActiveWindow = Window("Code");

        Assert.True(runner.Ran1.Wait(TimeSpan.FromSeconds(2)), "enter should run the bound pipeline");
        Assert.Equal("flash", Assert.Single(runner.Snapshot()));
        dispatcher.Dispose();
    }

    [Fact]
    public void EmptyProcessFilter_RunsForAnyApp()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Firefox", 2) },
            new[] { new AppLayerBinding("", AppLayerMoment.Enter, "any") },
            "any");

        engine.ActiveWindow = Window("Firefox");

        Assert.True(runner.Ran1.Wait(TimeSpan.FromSeconds(2)), "empty filter should fire for any matched app");
        Assert.Equal("any", Assert.Single(runner.Snapshot()));
        dispatcher.Dispose();
    }

    [Fact]
    public void ProcessSpecificFilter_DoesNotRunForOtherApp()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3), new AppLayerRule("Firefox", 2) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Enter, "flash") },
            "flash");

        engine.ActiveWindow = Window("Firefox"); // matches the Firefox rule, not the Code binding

        Assert.False(runner.Ran1.Wait(150));
        Assert.Empty(runner.Snapshot());
        dispatcher.Dispose();
    }

    [Fact]
    public void Leave_RunsBoundPipeline()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Leave, "onLeave") },
            "onLeave");

        engine.ActiveWindow = Window("Code");      // Enter — no enter binding, runs nothing
        engine.ActiveWindow = Window("firefox");   // focus leaves the rule → Leave

        Assert.True(runner.Ran1.Wait(TimeSpan.FromSeconds(2)), "leave should run the bound pipeline");
        Assert.Equal("onLeave", Assert.Single(runner.Snapshot()));
        dispatcher.Dispose();
    }

    [Fact]
    public void MomentMismatch_RunsNothing()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Leave, "onLeave") },
            "onLeave");

        engine.ActiveWindow = Window("Code"); // Enter, but only a Leave binding exists

        Assert.False(runner.Ran1.Wait(150));
        Assert.Empty(runner.Snapshot());
        dispatcher.Dispose();
    }

    [Fact]
    public void MultipleBindingsOnMoment_AllRun()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[]
            {
                new AppLayerBinding("Code", AppLayerMoment.Enter, "a"),
                new AppLayerBinding("", AppLayerMoment.Enter, "b"),
            },
            "a", "b");

        engine.ActiveWindow = Window("Code");

        Assert.True(runner.Ran1.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(runner.Ran1.Wait(TimeSpan.FromSeconds(2)));
        var ran = runner.Snapshot();
        Assert.Equal(2, ran.Count);
        Assert.Contains("a", ran);
        Assert.Contains("b", ran);
        dispatcher.Dispose();
    }

    [Fact]
    public void DanglingBinding_RunsNothing()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Enter, "gone") }
            /* no pipeline named "gone" */);

        engine.ActiveWindow = Window("Code");

        Assert.False(runner.Ran1.Wait(150));
        Assert.Empty(runner.Snapshot());
        dispatcher.Dispose();
    }

    [Fact]
    public void DisposedDispatcher_StopsRunning()
    {
        var (engine, runner, dispatcher) = Build(
            new[] { new AppLayerRule("Code", 3) },
            new[] { new AppLayerBinding("Code", AppLayerMoment.Enter, "flash") },
            "flash");

        dispatcher.Dispose();
        engine.ActiveWindow = Window("Code");

        Assert.False(runner.Ran1.Wait(150));
        Assert.Empty(runner.Snapshot());
    }
}
