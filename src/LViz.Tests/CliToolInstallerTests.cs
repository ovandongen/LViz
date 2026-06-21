using LViz.App.Services;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers <see cref="CliToolInstaller"/> via its test seam (injected link dir +
/// target resolver + OS branch): install creates a symlink to the resolved binary,
/// status reflects it, uninstall removes it, and the unsupported branches (Windows,
/// dev launch with a null target) refuse. Symlink creation is unsupported on some
/// sandboxes, so install assertions are skipped (not failed) when that's the case.
/// </summary>
public sealed class CliToolInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _target;

    public CliToolInstallerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lviz-cli-{Guid.NewGuid():N}");
        _dir = Path.Combine(root, "bin");        // the on-PATH link directory
        var appDir = Path.Combine(root, "app");  // separate dir for the binary so a
        Directory.CreateDirectory(_dir);         // case-insensitive FS can't confuse
        Directory.CreateDirectory(appDir);       // the lowercase link with the target
        _target = Path.Combine(appDir, "LViz");
        File.WriteAllText(_target, "#!/bin/sh\n"); // stand-in for the app binary
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { /* best effort */ }
    }

    private CliToolInstaller MakeUnix(Func<string?>? resolve = null) =>
        new(_dir, resolve ?? (() => _target), isWindows: false);

    [Fact]
    public void Windows_IsUnsupported()
    {
        var installer = new CliToolInstaller(_dir, () => _target, isWindows: true);

        Assert.Equal(CliToolState.Unsupported, installer.GetStatus().State);
        Assert.False(installer.Install().Success);
    }

    [Fact]
    public void DevLaunch_NullTarget_IsUnsupported()
    {
        var installer = MakeUnix(resolve: () => null);

        Assert.Equal(CliToolState.Unsupported, installer.GetStatus().State);
        Assert.False(installer.Install().Success);
    }

    [Fact]
    public void NotInstalled_WhenNoLinkPresent()
    {
        var installer = MakeUnix();

        Assert.Equal(CliToolState.NotInstalled, installer.GetStatus().State);
    }

    [Fact]
    public void Install_CreatesLink_ThenStatusInstalled_ThenUninstallRemoves()
    {
        var installer = MakeUnix();
        var linkPath = Path.Combine(_dir, CliToolInstaller.CommandName);

        var install = installer.Install();
        if (!install.Success && install.Message.Contains("symlink", StringComparison.OrdinalIgnoreCase))
            return; // symlinks unsupported in this environment

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget is not null);
        Assert.Equal(_target, new FileInfo(linkPath).LinkTarget);
        Assert.Equal(CliToolState.Installed, installer.GetStatus().State);

        // Idempotent: installing again over the existing link still succeeds.
        Assert.True(installer.Install().Success);

        var uninstall = installer.Uninstall();
        Assert.True(uninstall.Success, uninstall.Message);
        Assert.Equal(CliToolState.NotInstalled, installer.GetStatus().State);
    }

    [Fact]
    public void Uninstall_WhenNotInstalled_Succeeds()
    {
        var installer = MakeUnix();

        Assert.True(installer.Uninstall().Success);
    }
}
