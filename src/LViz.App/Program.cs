using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using LViz.App.Cli;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using Velopack;

namespace LViz.App;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single binary (see lviz-host-agent-cli-spec.md §4): a recognized
        // sub-command means "act as the CLI client" — connect to the running app
        // over IPC, act, exit. Checked before everything else so a CLI call is
        // fast and side-effect-free (no Velopack hooks, no Avalonia, no mutex).
        // Anything else (including a bare launch) runs as the resident LViz app.
        if (args.Length > 0 && CliClient.Commands.Contains(args[0]))
            return CliClient.RunAsync(args).GetAwaiter().GetResult();

        // Must run before the single-instance mutex: Velopack reinvokes the
        // exe with --veloapp-* arguments during install/update/uninstall hooks,
        // and those subprocesses would otherwise be blocked by the mutex held
        // by the primary running instance.
        VelopackApp.Build().SetArgs(args).Run();

        using var mutex = new Mutex(true, "LViz-SingleInstance", out bool isNew);
        if (!isNew) return 0;

        DiagnosticLog.LogEnvironment();
        // ZmkHidProtocol's LibLog routes through System.Diagnostics.Trace.
        // Without a listener, every RawHid log line vanishes — install a
        // bridge that forwards them into DiagnosticLog so HID discovery /
        // connect / read activity is visible in diagnostic.log.
        Trace.Listeners.Add(new ZmkHidProtocolTraceListener());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Ship Inter as the app-wide default font. The OS fallback font (notably
            // on macOS) renders glyphs high in Fluent's control line boxes — text
            // sits above centre on buttons, combos, checkboxes, text boxes. Inter is
            // the font Fluent's metrics are tuned for, so it centres correctly and
            // renders identically across Windows / macOS / Linux.
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();

        // Allow users to force software rendering via env var or settings.json.
        if (OperatingSystem.IsWindows())
        {
            var renderMode = Environment.GetEnvironmentVariable("LVIZ_RENDER_MODE");
            var source = "LVIZ_RENDER_MODE env";

            if (string.IsNullOrEmpty(renderMode))
            {
                try
                {
                    renderMode = new SettingsService().Load().RenderingMode;
                    source = "settings.json";
                }
                catch
                {
                    // Earliest-startup settings read; can't recover and a crash
                    // here would block launch, so fall back to auto-detect.
                    renderMode = "auto";
                    source = "default (settings read failed)";
                }
            }

            if (renderMode?.Equals("software", StringComparison.OrdinalIgnoreCase) == true)
            {
                DiagnosticLog.Info("Startup", $"Rendering mode: software (via {source})");
                builder = builder.With(new Win32PlatformOptions
                {
                    RenderingMode = [Win32RenderingMode.Software]
                });
            }
            else
            {
                DiagnosticLog.Info("Startup", $"Rendering mode: {renderMode ?? "auto"} (via {source})");
            }
        }

        return builder;
    }
}
