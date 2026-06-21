namespace LViz.Core.Settings;

/// <summary>
/// Binds an inbound host <em>event id</em> (the opaque string a CLI/script fires
/// over IPC, e.g. <c>"ci:ok"</c>; see <c>lviz-host-agent-cli-spec.md</c>) to the
/// <see cref="ActionPipeline"/> the host runs in response. The string sibling of
/// <see cref="SignalBinding"/>: a signal is keyed by a numeric uint8 id from a
/// device, an event by an arbitrary string id from host software — both resolve to
/// a pipeline name and run the same <see cref="ActionPipeline"/>. The event id
/// space is host-global, so bindings are global, not per-keyboard-profile.
/// <see cref="PipelineName"/> references an <see cref="ActionPipeline.Name"/> in
/// <see cref="UserSettings.ActionPipelines"/>; a dangling name resolves to nothing
/// and the event is rejected.
/// </summary>
public sealed record EventBinding(string EventId, string PipelineName);
