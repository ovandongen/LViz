using LViz.Core.Diagnostics;
using LViz.Core.Settings;

namespace LViz.App.Services;

/// <summary>
/// The shared tail of every action-pipeline trigger: resolve a pipeline name
/// against a live settings snapshot and run it off the calling thread. Each
/// trigger source (host IPC <c>event</c>, device <c>signal.fire</c>, app-focus
/// lifecycle) keeps its own binding lookup but funnels the resolve-and-run step
/// here so the not-found / failure logging stays in one place.
/// </summary>
internal static class PipelineNameDispatch
{
    /// <summary>
    /// Finds the pipeline named <paramref name="pipelineName"/> in
    /// <paramref name="settings"/> and runs it fire-and-forget. Returns false (and
    /// logs) when no such pipeline exists, so a caller that owes a client a response
    /// (the IPC path) can report it; fire-only callers ignore the result.
    /// <paramref name="subsystem"/> tags the diagnostic lines.
    /// </summary>
    public static bool RunByName(IPipelineRunner runner, UserSettings settings, string pipelineName, string subsystem)
    {
        var pipeline = settings.ActionPipelines.FirstOrDefault(p =>
            string.Equals(p.Name, pipelineName, StringComparison.Ordinal));
        if (pipeline is null)
        {
            DiagnosticLog.Warn(subsystem, $"pipeline '{pipelineName}' not found; ignored");
            return false;
        }

        DiagnosticLog.Info(subsystem, $"running '{pipeline.Name}'");
        // Off the calling thread: pipeline steps await I/O and delays.
        _ = Task.Run(async () =>
        {
            try { await runner.RunAsync(pipeline).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warn(subsystem, $"pipeline '{pipeline.Name}' run failed: {ex.Message}"); }
        });
        return true;
    }
}
