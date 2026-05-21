using System.Runtime.InteropServices;
using System.Text;

namespace LViz.Core.Diagnostics;

/// <summary>
/// Diagnostic severity levels, ordered from most to least verbose.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Subsystem-tagged diagnostic logger with level filtering and log rotation.
/// All public methods are thread-safe.
///
/// Default minimum level is <see cref="LogLevel.Info"/>. Call
/// <see cref="SetMinimumLevel"/> to change at runtime (e.g. from a settings page).
///
/// Log files are stored in <c>%APPDATA%/LViz/</c> (or platform equivalent).
/// When the active log exceeds <see cref="MaxLogSize"/> it is rotated to <c>.log.1</c>.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>Maximum log file size before rotation (2 MB).</summary>
    public const long MaxLogSize = 2 * 1024 * 1024;

    private static readonly string LogDir = GetLogDir();
    private static readonly string LogPath = Path.Combine(LogDir, "diagnostic.log");
    private static readonly string RotatedPath = Path.Combine(LogDir, "diagnostic.log.1");
    private static readonly object Lock = new();

    private static LogLevel _minimumLevel = GetInitialLevel();

    public static LogLevel MinimumLevel => _minimumLevel;

    public static void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    public static string GetLogDirectory() => LogDir;

    public static string GetLogFilePath() => LogPath;

    // --- Level-specific convenience methods ---

    public static void Trace(string subsystem, string message) => Write(LogLevel.Trace, subsystem, message);
    public static void Debug(string subsystem, string message) => Write(LogLevel.Debug, subsystem, message);
    public static void Info(string subsystem, string message) => Write(LogLevel.Info, subsystem, message);
    public static void Warn(string subsystem, string message) => Write(LogLevel.Warn, subsystem, message);
    public static void Error(string subsystem, string message) => Write(LogLevel.Error, subsystem, message);

    /// <summary>
    /// Writes a log entry if the given level meets the minimum threshold.
    /// Format: <c>[timestamp] LEVEL [subsystem] message</c>
    /// </summary>
    public static void Write(LogLevel level, string subsystem, string message)
    {
        if (level < _minimumLevel) return;

        try
        {
            lock (Lock)
            {
                EnsureLogDirectory();
                RotateIfNeeded();

                File.AppendAllText(LogPath,
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {level.ToString().ToUpperInvariant(),-5} [{subsystem}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>
    /// Logs basic environment info. Call once at startup.
    /// </summary>
    public static void LogEnvironment()
    {
        Info("Startup", "--- Application starting ---");
        Info("Startup", $"OS: {RuntimeInformation.OSDescription}");
        Info("Startup", $"Architecture: {RuntimeInformation.OSArchitecture}");
        Info("Startup", $"Runtime: {RuntimeInformation.FrameworkDescription}");
        Info("Startup", $"Process arch: {RuntimeInformation.ProcessArchitecture}");
        Info("Startup", $"Log directory: {LogDir}");
        Info("Startup", $"Log level: {_minimumLevel} (env LVIZ_LOG_LEVEL={Environment.GetEnvironmentVariable("LVIZ_LOG_LEVEL") ?? "(not set)"})");
    }

    /// <summary>
    /// Collects a diagnostic report suitable for clipboard or file export.
    /// <paramref name="snapshot"/>, when supplied, is inserted between the
    /// environment and recent-log sections — used by the App layer to inject
    /// runtime state (active settings, loaded layout, signal-macro counts, …)
    /// that the Core logger doesn't have direct access to.
    /// </summary>
    public static string CollectDiagnosticReport(string? snapshot = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== LViz Diagnostic Report ===");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("--- Environment ---");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process arch: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(snapshot))
        {
            sb.AppendLine(snapshot.TrimEnd());
            sb.AppendLine();
        }

        sb.AppendLine("--- Current Session Log ---");
        try
        {
            if (File.Exists(LogPath))
            {
                var lines = File.ReadAllLines(LogPath);
                // Anchor to the last "Application starting" marker (written by
                // LogEnvironment at startup). Falls back to the last 100 lines
                // if no marker is found (e.g. logging started mid-run).
                var start = -1;
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Contains("--- Application starting ---"))
                    {
                        start = i;
                        break;
                    }
                }
                if (start < 0)
                    start = Math.Max(0, lines.Length - 100);

                for (var i = start; i < lines.Length; i++)
                    sb.AppendLine(lines[i]);
            }
            else
            {
                sb.AppendLine("(no log file found)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not read log: {ex.Message})");
        }

        return sb.ToString();
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var info = new FileInfo(LogPath);
            if (info.Length <= MaxLogSize) return;

            if (File.Exists(RotatedPath))
                File.Delete(RotatedPath);
            File.Move(LogPath, RotatedPath);
        }
        catch
        {
            // Best-effort rotation.
        }
    }

    private static void EnsureLogDirectory()
    {
        if (!Directory.Exists(LogDir))
            Directory.CreateDirectory(LogDir);
    }

    private static LogLevel GetInitialLevel()
    {
        var env = Environment.GetEnvironmentVariable("LVIZ_LOG_LEVEL");
        return env?.ToUpperInvariant() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }

    private static string GetLogDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LViz");
    }
}
