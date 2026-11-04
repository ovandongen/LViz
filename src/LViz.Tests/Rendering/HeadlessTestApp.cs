using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

[assembly: AvaloniaTestApplication(typeof(LViz.Tests.Rendering.HeadlessTestApp))]

namespace LViz.Tests.Rendering;

/// <summary>
/// The Avalonia app behind every <c>[AvaloniaFact]</c>: headless, but drawing
/// real pixels with Skia (<c>UseHeadlessDrawing = false</c>), with Inter as the
/// default font and the Font Awesome icons registered, like the real app.
/// Plain <c>[Fact]</c> tests don't start it. No theme or app resources are
/// loaded, so only self-drawn controls (custom <c>Render</c>) render as in the
/// app; XAML views with <c>StaticResource</c> lookups don't belong here.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static readonly object IconLock = new();
    private static bool _iconsRegistered;

    public override void OnFrameworkInitializationCompleted()
    {
        // The headless session builds a fresh Application per test, but
        // IconProvider.Current is process-wide and refuses a second "fa"
        // provider: register it once per process.
        lock (IconLock)
        {
            if (!_iconsRegistered)
            {
                IconProvider.Current.Register<FontAwesomeIconProvider>();
                _iconsRegistered = true;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Where rendering tests write their PNGs: <c>docs/ideas/ring-snapshots/</c>
/// under the repository (or worktree) root, found by walking up from the test
/// binaries to the folder holding <c>LViz.sln</c>. <c>docs/</c> is gitignored,
/// so the pictures never reach git; reviewers open them next to
/// <c>docs/ideas/lviz-key-ring-reference.html</c>. Without a repo root (an
/// unusual test runner) they go to the temp folder instead.
/// </summary>
public static class SnapshotPaths
{
    public static string Folder { get; } = FindFolder();

    /// <summary>The full path for one snapshot file; the folder exists.</summary>
    public static string File(string name) => Path.Combine(Folder, name);

    private static string FindFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "LViz.sln")))
            dir = dir.Parent;

        var folder = dir is null
            ? Path.Combine(Path.GetTempPath(), "lviz-ring-snapshots")
            : Path.Combine(dir.FullName, "docs", "ideas", "ring-snapshots");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
