using LViz.Core.Diagnostics;

namespace LViz.Core.Persistence;

/// <summary>
/// Makes a save that writes several files all-or-nothing. <see cref="Capture"/>
/// remembers what each target file holds (or that it doesn't exist) before the
/// writes; when a later write fails, <see cref="Restore"/> puts back every file
/// that changed and deletes the ones that didn't exist. Each single write is
/// already atomic (<see cref="AtomicFile"/>); this covers the gap between them.
/// </summary>
public sealed class FileRollback
{
    private const string LogTag = "Persistence";

    private readonly IReadOnlyList<(string Path, byte[]? Content)> _before;

    private FileRollback(IReadOnlyList<(string Path, byte[]? Content)> before) => _before = before;

    /// <summary>Reads the current state of <paramref name="paths"/>. IO errors
    /// propagate: nothing has been written yet, so the caller can just fail.</summary>
    public static FileRollback Capture(IEnumerable<string> paths) =>
        new(paths
            .Distinct(StringComparer.Ordinal)
            .Select(path => (path, File.Exists(path) ? File.ReadAllBytes(path) : null))
            .ToList());

    /// <summary>
    /// Best effort: restores every captured file whose content changed and
    /// deletes every file that didn't exist at capture time. A file that can't
    /// be restored is logged and skipped; this never throws, because it runs
    /// while the caller is already handling a failure.
    /// </summary>
    public void Restore()
    {
        foreach (var (path, content) in _before)
        {
            try
            {
                if (content is null)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                {
                    AtomicFile.WriteAllBytes(path, content);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(LogTag, $"Could not roll back {path}: {ex.Message}");
            }
        }
    }
}
