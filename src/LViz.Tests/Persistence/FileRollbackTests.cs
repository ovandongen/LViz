using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.Persistence;

/// <summary>
/// <see cref="FileRollback"/>: a changed file gets its old content back, a file
/// that didn't exist is deleted again, and an untouched file is left alone.
/// </summary>
public sealed class FileRollbackTests : IDisposable
{
    private readonly string _dir;

    public FileRollbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lviz-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Restore_PutsBackChangedFiles_AndDeletesNewOnes()
    {
        var existing = Path.Combine(_dir, "existing.json");
        var created = Path.Combine(_dir, "created.dtsi");
        File.WriteAllText(existing, "before");

        var rollback = FileRollback.Capture([existing, created]);
        File.WriteAllText(existing, "after");
        File.WriteAllText(created, "new");
        rollback.Restore();

        Assert.Equal("before", File.ReadAllText(existing));
        Assert.False(File.Exists(created));
    }

    [Fact]
    public void Restore_LeavesUnchangedFilesUntouched()
    {
        var path = Path.Combine(_dir, "mine.dtsi");
        File.WriteAllText(path, "/ { };");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        FileRollback.Capture([path]).Restore();

        Assert.Equal("/ { };", File.ReadAllText(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}
