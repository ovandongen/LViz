namespace LViz.Core.ZmkConfig;

/// <summary>
/// A zmk-config file (west.yml, build.yaml, .conf) the readers cannot parse.
/// <paramref name="line"/> is 1-based, as shown to the user.
/// </summary>
public sealed class ZmkConfigFormatException(string file, int line, string message)
    : Exception($"{file}:{line}: {message}")
{
    /// <summary>The file the reader was given, e.g. <c>config/west.yml</c>.</summary>
    public string File { get; } = file;

    /// <summary>1-based line the problem sits on.</summary>
    public int Line { get; } = line;
}
