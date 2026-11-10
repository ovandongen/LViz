namespace LViz.Core.ZmkConfig;

/// <summary>
/// The files of a zmk-config root that LViz reads, keyed by their
/// '/'-separated paths relative to <see cref="Root"/> (ordinal). The key is
/// also the name the readers report in their exceptions, so it stays
/// <c>config/west.yml</c>, <c>build.yaml</c>, and so on.
/// </summary>
public sealed record ZmkConfigFileSet(string Root, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>
    /// Loads <c>config/west.yml</c>, <c>build.yaml</c> (else
    /// <c>build.yml</c>), every <c>config/*.conf</c>, and every
    /// <c>Kconfig.defconfig</c> under <c>boards/</c> and
    /// <c>config/boards/</c> (recursive). A missing file is simply absent;
    /// IO errors propagate.
    /// </summary>
    public static ZmkConfigFileSet Read(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        ReadIfPresent(files, root, "config/west.yml");
        if (!ReadIfPresent(files, root, "build.yaml"))
            ReadIfPresent(files, root, "build.yml");

        var confDir = Path.Combine(root, "config");
        if (Directory.Exists(confDir))
        {
            foreach (var path in Directory.EnumerateFiles(confDir, "*.conf"))
                Add(files, root, path);
        }

        foreach (var sub in new[] { "boards", "config/boards" })
        {
            var dir = LocalPath(root, sub);
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Directory.EnumerateFiles(dir, "Kconfig.defconfig", SearchOption.AllDirectories))
                Add(files, root, path);
        }

        return new ZmkConfigFileSet(root, files);
    }

    /// <summary>
    /// A copy with one file replaced or added, keys as in
    /// <see cref="Files"/> (the setup plan's guard uses it).
    /// </summary>
    public ZmkConfigFileSet With(string relativePath, string text)
    {
        var files = new Dictionary<string, string>(Files, StringComparer.Ordinal);
        files[relativePath] = text;
        return new ZmkConfigFileSet(Root, files);
    }

    private static bool ReadIfPresent(Dictionary<string, string> files, string root, string relativePath)
    {
        var path = LocalPath(root, relativePath);
        if (!File.Exists(path))
            return false;
        files[relativePath] = File.ReadAllText(path);
        return true;
    }

    private static void Add(Dictionary<string, string> files, string root, string absolutePath)
    {
        var relative = Path.GetRelativePath(root, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        files[relative] = File.ReadAllText(absolutePath);
    }

    private static string LocalPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
