using LViz.Core.Diagnostics;

namespace LViz.App.Services;

/// <summary>Outcome of querying where the <c>lviz</c> CLI symlink stands.</summary>
public enum CliToolState
{
    /// <summary>A <c>lviz</c> entry exists in the target PATH directory.</summary>
    Installed,

    /// <summary>The target directory is writable/known but no <c>lviz</c> entry exists yet.</summary>
    NotInstalled,

    /// <summary>Can't install from here — a dev launch (dotnet host / bin output) or Windows (installer's job).</summary>
    Unsupported,
}

/// <summary>Where the <c>lviz</c> command stands, plus a human-readable detail line.</summary>
public sealed record CliToolStatus(CliToolState State, string? LinkPath, string Detail);

/// <summary>Result of an install/uninstall attempt — success plus a message to surface.</summary>
public sealed record CliToolResult(bool Success, string Message);

/// <summary>
/// Creates (or removes) the <c>lviz</c> command on PATH by symlinking it to the
/// running executable — the in-app half of the single-binary CLI's PATH story
/// (see <c>lviz-host-agent-cli-spec.md</c> §8), the pattern VS Code uses for its
/// <c>code</c> command. macOS/Linux only: it links <see cref="CommandName"/> into
/// <see cref="LinkDirectory"/>. Windows PATH registration is the installer's job, and
/// a dev launch (under the <c>dotnet</c> host or a <c>bin/Debug|Release</c> output)
/// is refused — that's what the dev shim is for, since there's no stable single
/// binary to point at. Stateless and platform-branching, so it lives in the DI
/// container like <see cref="HostActionExecutor"/>.
/// </summary>
public interface ICliToolInstaller
{
    CliToolStatus GetStatus();
    CliToolResult Install();
    CliToolResult Uninstall();
}

public sealed class CliToolInstaller : ICliToolInstaller
{
    public const string CommandName = "lviz";

    /// <summary>Default on-PATH directory we link into. On PATH and conventionally user-writable on macOS/Linux.</summary>
    public const string DefaultLinkDirectory = "/usr/local/bin";

    private readonly string _linkDirectory;
    private readonly Func<string?> _resolveTarget;
    private readonly bool _isWindows;

    public CliToolInstaller() : this(DefaultLinkDirectory, ResolveInstallableTarget, OperatingSystem.IsWindows()) { }

    /// <summary>Test seam: override the link directory, the install target resolver, and the OS branch.</summary>
    internal CliToolInstaller(string linkDirectory, Func<string?> resolveTarget, bool isWindows)
    {
        _linkDirectory = linkDirectory;
        _resolveTarget = resolveTarget;
        _isWindows = isWindows;
    }

    private string LinkPath => Path.Combine(_linkDirectory, CommandName);

    public CliToolStatus GetStatus()
    {
        if (_isWindows)
            return new CliToolStatus(CliToolState.Unsupported, null,
                "On Windows the CLI is added to PATH by the installer.");

        var target = _resolveTarget();
        if (target is null)
            return new CliToolStatus(CliToolState.Unsupported, null,
                "Run an installed build to install the CLI tool (dev builds use the shim).");

        return LinkExists(LinkPath)
            ? new CliToolStatus(CliToolState.Installed, LinkPath, $"Installed: {LinkPath}")
            : new CliToolStatus(CliToolState.NotInstalled, LinkPath, $"Not installed. Will link {LinkPath} → {target}");
    }

    public CliToolResult Install()
    {
        if (_isWindows)
            return new CliToolResult(false, "Not supported on Windows — the installer handles PATH.");

        var target = _resolveTarget();
        if (target is null)
            return new CliToolResult(false, "Only available from an installed build. In dev, use the lviz shim.");

        try
        {
            Directory.CreateDirectory(_linkDirectory);
            // CreateSymbolicLink fails if the node already exists (even a dangling
            // link), so clear any prior entry first. File.Delete removes a symlink
            // node and is a no-op when nothing is there.
            File.Delete(LinkPath);
            File.CreateSymbolicLink(LinkPath, target);
            DiagnosticLog.Info("Cli", $"linked {LinkPath} → {target}");
            return new CliToolResult(true, $"Installed. '{CommandName}' is now on your PATH ({LinkPath}).");
        }
        catch (UnauthorizedAccessException)
        {
            // /usr/local/bin is root-owned on a fresh macOS; hand the user the
            // exact command rather than silently failing.
            return new CliToolResult(false,
                $"Permission denied writing {_linkDirectory}. Run:\n  sudo ln -sf \"{target}\" \"{LinkPath}\"");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Cli", $"install failed: {ex.Message}");
            return new CliToolResult(false, $"Install failed: {ex.Message}");
        }
    }

    public CliToolResult Uninstall()
    {
        if (!LinkExists(LinkPath))
            return new CliToolResult(true, "Not installed.");
        try
        {
            File.Delete(LinkPath);
            DiagnosticLog.Info("Cli", $"removed {LinkPath}");
            return new CliToolResult(true, $"Removed {LinkPath}.");
        }
        catch (UnauthorizedAccessException)
        {
            return new CliToolResult(false, $"Permission denied. Run:\n  sudo rm \"{LinkPath}\"");
        }
        catch (Exception ex)
        {
            return new CliToolResult(false, $"Remove failed: {ex.Message}");
        }
    }

    // The executable to point the symlink at — null when we shouldn't install from
    // here (dev launch). A published self-contained build's ProcessPath is the
    // single-file LViz binary, which IS the CLI (a recognized sub-command diverts
    // to client mode in Program.Main).
    private static string? ResolveInstallableTarget()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return null;

        var name = Path.GetFileNameWithoutExtension(path);
        // Launched via `dotnet LViz.App.dll` — symlinking the shared host would be wrong.
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)) return null;

        // A build-output apphost (`dotnet run`) is transient and needs its sibling
        // DLLs; don't bake a fragile link to it — that's the shim's job.
        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}bin{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{sep}bin{sep}Release{sep}", StringComparison.OrdinalIgnoreCase))
            return null;

        return path;
    }

    // True when a regular file OR a symlink (including a dangling one) sits at path.
    private static bool LinkExists(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists || fi.LinkTarget is not null;
    }
}
