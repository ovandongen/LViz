using LViz.Core.Diagnostics;

namespace LViz.Core.Settings;

/// <summary>
/// Convenience helpers over <see cref="ISettingsService"/>.
/// </summary>
public static class SettingsServiceExtensions
{
    /// <summary>
    /// Load → mutate → save in one shot, swallowing and logging any failure as
    /// a warning so a persistence error never tears down a UI handler. This is
    /// the canonical settings-write path — callers pass a pure
    /// <c>s =&gt; s with { ... }</c> transform and their own diagnostic
    /// <paramref name="subsystem"/> tag.
    /// </summary>
    public static void Update(
        this ISettingsService service,
        Func<UserSettings, UserSettings> mutate,
        string subsystem)
    {
        try
        {
            service.Save(mutate(service.Load()));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(subsystem, $"Persist settings failed: {ex.Message}");
        }
    }
}
