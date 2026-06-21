namespace LViz.Core.Settings;

/// <summary>
/// A named, reusable ordered list of <see cref="PipelineStep"/>s — the unit a
/// trigger (today a <see cref="SignalBinding"/>, later app-focus) runs. The
/// library lives in <see cref="UserSettings.ActionPipelines"/>; triggers bind by
/// <see cref="Name"/> so a pipeline can be re-pointed or shared without rewiring
/// the trigger. Names are the user's identity for a pipeline — treated
/// case-sensitively on lookup to match what the user typed.
/// </summary>
public sealed record ActionPipeline(string Name, List<PipelineStep> Steps);
