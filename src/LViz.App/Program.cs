using System.Diagnostics;
using Avalonia;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using Velopack;

namespace LViz.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before the single-instance mutex: Velopack reinvokes the
        // exe with --veloapp-* arguments during install/update/uninstall hooks,
        // and those subprocesses would otherwise be blocked by the mutex held
        // by the primary running instance.
        VelopackApp.Build().SetArgs(args).Run();

        using var mutex = new Mutex(true, "LViz-SingleInstance", out bool isNew);
        if (!isNew) return;

        DiagnosticLog.LogEnvironment();
        // ZmkHidProtocol's LibLog routes through System.Diagnostics.Trace.
        // Without a listener, every RawHid log line vanishes — install a
        // bridge that forwards them into DiagnosticLog so HID discovery /
        // connect / read activity is visible in diagnostic.log.
        Trace.Listeners.Add(new ZmkHidProtocolTraceListener());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
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
