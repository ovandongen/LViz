using System.Diagnostics;
using System.IO;
using LViz.Core.Diagnostics;

namespace LViz.App.Services;

/// <summary>
/// Spawns host-side processes for the <c>Launch</c> and <c>Shell</c> pipeline
/// steps. Per the signal spec the pipeline library is trusted, user-owned local
/// config: the wire only references a slot, never defines one, so what runs here
/// is always something the user authored. Processes are launched detached — we
/// never wait on, capture, or kill the child; a step's job is to start it.
/// </summary>
public interface IHostActionExecutor
{
    /// <summary>Open an app / file / URL with the OS default handler (macOS
    /// <c>open</c>, Windows shell-execute, Linux <c>xdg-open</c>).</summary>
    void Launch(string target);

    /// <summary>Run a command line through the platform shell (<c>/bin/sh -c</c>
    /// on macOS/Linux, <c>cmd /c</c> on Windows).</summary>
    void Shell(string command);
}

/// <summary>
/// Per-OS <see cref="IHostActionExecutor"/>. Every spawn is wrapped so a bad
/// target (missing app, malformed command) logs a warning and never bubbles into
/// the pipeline runner — one failed step shouldn't tear down the rest of the run.
/// </summary>
public sealed class HostActionExecutor : IHostActionExecutor
{
    public void Launch(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Shell-execute so paths, URLs, and registered file types all
                // resolve through the OS the same way a double-click would.
                Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                // `open <name>` reads a bare arg as a path relative to cwd, so an
                // app name like "Notes.app" silently fails. Launch apps by name
                // with -a; pass real paths and URLs straight through. ArgumentList
                // keeps the target intact regardless of spaces.
                if (!IsUrl(target) && !File.Exists(target) && !Directory.Exists(target))
                    psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(target);
                Start(psi);
            }
            else
            {
                // xdg-open handles URLs and files/MIME types only — no app-by-name
                // concept on Linux; that's the Shell step's job.
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Start(psi);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Pipeline", $"launch '{target}' failed: {ex.Message}");
        }
    }

    public void Shell(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { UseShellExecute = false }
                : new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            // ArgumentList avoids the brittle manual quoting of a single command
            // string; the shell itself parses the command we hand it.
            psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            psi.ArgumentList.Add(command);
            Start(psi);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Pipeline", $"shell '{command}' failed: {ex.Message}");
        }
    }

    // True for an absolute URI with a real scheme (http, https, mailto, …) but
    // not a bare filesystem path, which Uri parses as the implicit file scheme.
    private static bool IsUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.Scheme != Uri.UriSchemeFile;

    // We don't keep the handle: the step is fire-and-forget. Dispose the returned
    // Process so its OS handle isn't leaked while the child keeps running.
    private static void Start(ProcessStartInfo psi) => Process.Start(psi)?.Dispose();
}
