using LViz.Core.Diagnostics;
using LViz.Core.Settings;

namespace LViz.App.Services;

/// <summary>
/// Bridges <see cref="AutoSwitchEngine.AppLayerTransition"/> moments to
/// action-pipeline runs — the local sibling of <see cref="SignalDispatcher"/>
/// (device <c>signal.fire</c>) and the IPC <c>event</c> path. When the auto-switch
/// engine enters/leaves/exits/re-enters an app layer it raises the moment plus the
/// rule involved; this live-reads <see cref="UserSettings.AppLayerBindings"/>,
/// selects every binding whose moment matches and whose process filter matches the
/// rule (empty filter = any app), and runs each bound pipeline off the engine's
/// thread. The layer push the engine performs is independent of this.
/// </summary>
public sealed class AppLayerPipelineDispatcher : IDisposable
{
    private readonly AutoSwitchEngine _engine;
    private readonly ISettingsService _settings;
    private readonly IPipelineRunner _runner;
    private bool _disposed;

    public AppLayerPipelineDispatcher(AutoSwitchEngine engine, ISettingsService settings, IPipelineRunner runner)
    {
        _engine = engine;
        _settings = settings;
        _runner = runner;
        _engine.AppLayerTransition += OnTransition;
    }

    private void OnTransition(AppLayerMoment moment, AppLayerRule rule)
    {
        // Live-read so binding/pipeline edits apply without a restart.
        var settings = _settings.Load();
        var matches = settings.AppLayerBindings.Where(b =>
            b.Moment == moment &&
            (string.IsNullOrEmpty(b.ProcessMatch) ||
             string.Equals(b.ProcessMatch, rule.ProcessMatch, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0)
        {
            DiagnosticLog.Debug("AppLayer", $"{moment} '{rule.ProcessMatch}': no binding; ignored");
            return;
        }

        foreach (var binding in matches)
        {
            DiagnosticLog.Debug("AppLayer", $"{moment} '{rule.ProcessMatch}' → '{binding.PipelineName}'");
            PipelineNameDispatch.RunByName(_runner, settings, binding.PipelineName, "AppLayer");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.AppLayerTransition -= OnTransition;
    }
}
