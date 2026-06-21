using System.Text;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;

namespace LViz.App.Services;

/// <summary>
/// Parses and executes the line-based IPC commands the CLI client sends to the
/// running LViz app (see <c>lviz-host-agent-cli-spec.md</c> §6). The wire form is
/// dumb: one command per connection, the first token is the verb, the response is
/// one or more lines whose first line is <c>ok</c> or <c>err &lt;reason&gt;</c>.
///
/// <para>This is the host-side mirror of <see cref="SignalDispatcher"/>: a
/// <c>signal.fire</c> arrives as a numeric id over HID, an <c>event</c> as a string
/// id over IPC, and both resolve through the user's binding table to a named
/// <see cref="ActionPipeline"/> run on the shared <see cref="IPipelineRunner"/>.
/// Settings are live-read per command so binding edits apply without a restart.</para>
/// </summary>
public sealed class IpcCommandService
{
    private readonly ISettingsService _settings;
    private readonly ICapabilityRouter _router;
    private readonly IPipelineRunner _runner;

    public IpcCommandService(ISettingsService settings, ICapabilityRouter router, IPipelineRunner runner)
    {
        _settings = settings;
        _router = router;
        _runner = runner;
    }

    /// <summary>
    /// Executes one command line and returns the response text. Never throws — a
    /// malformed or failing command becomes an <c>err</c> line so the client always
    /// gets an ack.
    /// </summary>
    public string Handle(string? line)
    {
        try
        {
            var trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) return "err empty command";

            var space = trimmed.IndexOf(' ');
            var verb = space < 0 ? trimmed : trimmed[..space];
            var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();

            return verb switch
            {
                "event" => HandleEvent(rest),
                "status" => HandleStatus(),
                "devices" => HandleDevices(),
                _ => $"err unknown command '{verb}'",
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Ipc", $"command '{line}' failed: {ex.Message}");
            return $"err {ex.Message}";
        }
    }

    // ── event <id> ────────────────────────────────────────────────────────────
    // Resolve the string id through EventBindings → ActionPipeline and run it off
    // this thread (the runner serializes runs through its own gate). Returns ok
    // once a pipeline is found and started — fire-and-forget, matching signal.fire.
    private string HandleEvent(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "err event requires an id (e.g. 'event ci:ok')";

        var s = _settings.Load();
        var binding = s.EventBindings.FirstOrDefault(b =>
            string.Equals(b.EventId, id, StringComparison.Ordinal));
        if (binding is null)
        {
            DiagnosticLog.Debug("Ipc", $"event '{id}': no binding; rejected");
            return $"err no binding for '{id}'";
        }

        DiagnosticLog.Debug("Ipc", $"event '{id}' → '{binding.PipelineName}'");
        return PipelineNameDispatch.RunByName(_runner, s, binding.PipelineName, "Ipc")
            ? "ok"
            : $"err pipeline '{binding.PipelineName}' not found";
    }

    // ── status ────────────────────────────────────────────────────────────────
    private string HandleStatus()
    {
        var devices = _router.Devices;
        var sb = new StringBuilder();
        sb.AppendLine("ok");
        sb.AppendLine("LViz is running.");
        sb.AppendLine($"devices: {devices.Count}");
        foreach (var d in devices)
            sb.AppendLine($"  {d.DisplayName}{(d.IsKeyboard ? " (keyboard)" : "")} [{CapabilitySummary(d)}]");
        return sb.ToString().TrimEnd();
    }

    // ── devices ───────────────────────────────────────────────────────────────
    private string HandleDevices()
    {
        var devices = _router.Devices;
        var sb = new StringBuilder();
        sb.AppendLine("ok");
        if (devices.Count == 0)
        {
            sb.AppendLine("(no devices connected)");
            return sb.ToString().TrimEnd();
        }

        foreach (var d in devices)
        {
            sb.AppendLine($"{d.DisplayName}{(d.IsKeyboard ? " (keyboard)" : "")}");
            sb.AppendLine($"  key: {d.StableKey}");
            if (d.Manifest is { } m)
            {
                if (!string.IsNullOrEmpty(m.ConfigId)) sb.AppendLine($"  configId: {m.ConfigId}");
                foreach (var cap in m.Capabilities)
                    sb.AppendLine($"  - {cap.Id} ({cap.Role})");
            }
            else
            {
                sb.AppendLine("  (no manifest)");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string CapabilitySummary(RoutedDeviceInfo d) =>
        d.Manifest is { } m && m.Capabilities.Count > 0
            ? string.Join(", ", m.Capabilities.Select(c => c.Id))
            : "no manifest";
}
