namespace LViz.Tests.ZmkConfig;

/// <summary>
/// The zmk-config folders under <c>ZmkConfig/Fixtures/</c>, copied next to the
/// test assembly (see the csproj). Real configs: <c>cosmos82</c> (stock ZMK
/// v0.3, central = right half via its own Kconfig.defconfig), <c>slabwing</c>
/// (stock v0.3, unibody), <c>glove80</c> and <c>go60</c> (MoErgo fork, modules
/// already set up; go60's files have no trailing newline). Synthetic:
/// <c>zzeneg</c> (modules set up with zzeneg's raw-hid) and <c>dongle</c>
/// (central dongle + two halves).
/// </summary>
internal static class ZmkConfigFixtures
{
    /// <summary>Every fixture folder name.</summary>
    public static readonly string[] Names = ["cosmos82", "slabwing", "glove80", "go60", "zzeneg", "dongle"];

    /// <summary>Absolute path of a fixture's config root (read-only: never write into it).</summary>
    public static string Root(string name) =>
        Path.Combine(AppContext.BaseDirectory, "ZmkConfig", "Fixtures", name);

    /// <summary>Text of one file inside a fixture, by its <c>/</c>-separated relative path.</summary>
    public static string Read(string name, string relativePath) =>
        File.ReadAllText(Path.Combine(Root(name), Path.Combine(relativePath.Split('/'))));

    /// <summary>
    /// Copies a fixture into a fresh temp folder and returns that folder, for
    /// tests that write. The caller deletes it.
    /// </summary>
    public static string CopyToTemp(string name)
    {
        var source = Root(name);
        var target = Path.Combine(Path.GetTempPath(), $"lviz-zmkconfig-{name}-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        return target;
    }
}
