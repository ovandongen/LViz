using System.Text.Json.Serialization;

namespace LViz.Core.Settings;

/// <summary>
/// A point in an app-focus auto-switch session's lifecycle that an
/// <see cref="AppLayerBinding"/> can fire on. The auto-switch engine raises one
/// of these alongside its layer push/revert; the binding then runs a pipeline
/// <em>in addition to</em> the layer change (never replacing it).
/// </summary>
/// <remarks>Serialized as the case-insensitive enum name, like
/// <see cref="AutoSwitchFallbackMode"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AppLayerMoment>))]
public enum AppLayerMoment
{
    /// <summary>A rule-matched app gained focus and its layer was pushed.</summary>
    Enter,

    /// <summary>Focus left the rule-matched app and the session ended.</summary>
    Leave,

    /// <summary>The user exited the app layer (double-tap exit key or a
    /// <c>&amp;trans</c>/<c>&amp;none</c> exit gesture) while still focused on the app.</summary>
    Exit,

    /// <summary>The user double-tapped the exit key again to restore the app layer.</summary>
    Reenter,
}

/// <summary>
/// Binds an app-focus auto-switch lifecycle <see cref="AppLayerMoment"/> to the
/// <see cref="ActionPipeline"/> LViz runs when it occurs — the local third sibling
/// of <see cref="SignalBinding"/> (numeric device id) and <see cref="EventBinding"/>
/// (host IPC string id). The auto-switch engine already pushes/reverts the keyboard
/// layer on these moments; this fires a pipeline <em>alongside</em> that push, never
/// replacing it (see <c>signal-capability-spec.md</c>).
///
/// <para><see cref="ProcessMatch"/> empty matches <em>any</em> rule-matched app;
/// otherwise it is compared case-insensitively against the firing rule's
/// <see cref="AppLayerRule.ProcessMatch"/>. Global, not per-keyboard-profile, like
/// its two siblings. <see cref="PipelineName"/> references an
/// <see cref="ActionPipeline.Name"/>; a dangling name resolves to nothing.</para>
/// </summary>
public sealed record AppLayerBinding(string ProcessMatch, AppLayerMoment Moment, string PipelineName);
