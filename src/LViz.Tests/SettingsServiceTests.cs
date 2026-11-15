using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The settings store: <c>settings.json</c> is read once and cached, and
/// <see cref="SettingsService.Update"/> is the only write path — locked so
/// concurrent writers lose nothing, never throwing, and announcing every change
/// through <c>Changed</c>.
/// </summary>
public class SettingsServiceTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(),
        $"lviz-store-{Guid.NewGuid():N}.json");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Current_MissingFile_ReturnsDefaults_AndTheSameInstanceTwice()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);

            var first = svc.Current;
            var second = svc.Current;

            Assert.Same(first, second);
            Assert.Equal(UserSettings.CurrentSchemaVersion, first.SchemaVersion);
            Assert.Equal("Corne", first.Keyboard);
            // Reading defaults does not create the file.
            Assert.False(File.Exists(path));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Update_WritesTheFile()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);

            svc.Update(s => s with { Keyboard = "GO60" }, "Test");

            Assert.Equal("GO60", svc.Current.Keyboard);
            Assert.Equal("GO60", new SettingsService(path).Current.Keyboard);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Update_SameInstance_WritesNothingAndRaisesNothing()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);
            var fired = 0;
            svc.Changed += (_, _) => fired++;

            svc.Update(s => s, "Test");

            Assert.False(File.Exists(path));
            Assert.Equal(0, fired);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Update_RaisesChanged_WithOldAndNew()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);
            UserSettings? old = null;
            UserSettings? next = null;
            var fired = 0;
            svc.Changed += (o, n) =>
            {
                old = o;
                next = n;
                fired++;
            };

            svc.Update(s => s with { Keyboard = "GO60" }, "Test");

            Assert.Equal(1, fired);
            Assert.NotNull(old);
            Assert.NotNull(next);
            Assert.Equal("Corne", old!.Keyboard);
            Assert.Equal("GO60", next!.Keyboard);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task Update_FromManyThreads_LosesNothing()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);

            var writers = Enumerable.Range(0, 4).Select(group => Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    var id = $"hint-{group}-{i}";
                    svc.Update(s => s with
                    {
                        DismissedLiveFeatureHints = [.. s.DismissedLiveFeatureHints, id],
                    }, "Test");
                }
            })).ToArray();
            await Task.WhenAll(writers);

            Assert.Equal(100, svc.Current.DismissedLiveFeatureHints.Distinct().Count());
            Assert.Equal(100, new SettingsService(path).Current.DismissedLiveFeatureHints.Distinct().Count());
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Update_WriteFails_KeepsTheValueInMemory()
    {
        // The settings path sits under a plain file, so its directory can never
        // be created and every write to it fails.
        var blocker = Path.Combine(Path.GetTempPath(), $"lviz-store-blocker-{Guid.NewGuid():N}");
        var path = Path.Combine(blocker, "settings.json");
        try
        {
            File.WriteAllText(blocker, "not a directory");
            var svc = new SettingsService(path);
            var fired = 0;
            svc.Changed += (_, _) => fired++;

            svc.Update(s => s with { Keyboard = "GO60" }, "Test");

            Assert.Equal("GO60", svc.Current.Keyboard);
            Assert.Equal(1, fired);
        }
        finally
        {
            if (File.Exists(blocker)) File.Delete(blocker);
        }
    }

    [Fact]
    public void Update_MutateThrows_ChangesNothing_AndDoesNotThrow()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);
            var fired = 0;
            svc.Changed += (_, _) => fired++;

            svc.Update(_ => throw new InvalidOperationException("mutate blew up"), "Test");

            Assert.Equal("Corne", svc.Current.Keyboard);
            Assert.False(File.Exists(path));
            Assert.Equal(0, fired);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Changed_HandlerThrows_UpdateStillStoresAndTheOtherHandlerRuns()
    {
        var path = TempPath();
        try
        {
            var svc = new SettingsService(path);
            var secondRan = false;
            svc.Changed += (_, _) => throw new InvalidOperationException("handler blew up");
            svc.Changed += (_, _) => secondRan = true;

            svc.Update(s => s with { Keyboard = "GO60" }, "Test");

            Assert.True(secondRan);
            Assert.Equal("GO60", svc.Current.Keyboard);
            Assert.Equal("GO60", new SettingsService(path).Current.Keyboard);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Current_V1File_MigratesOnce()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path,
                """{"SchemaVersion": 1, "Keyboard": "GO60", "LayoutJsonPaths": {"GO60": "/x/a.keymap"}}""");
            var svc = new SettingsService(path);

            var loaded = svc.Current;

            Assert.Equal(UserSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("GO60", loaded.Keyboard);
            Assert.Equal(new[] { "/x/a.keymap" }, loaded.RecentKeymapPaths["GO60"]);
            // The migration happened on the first read, so a second read hands
            // back the same instance instead of reading and migrating again.
            Assert.Same(loaded, svc.Current);
            // And it was persisted: a fresh instance reads the migrated file.
            Assert.Equal(new[] { "/x/a.keymap" }, new SettingsService(path).Current.RecentKeymapPaths["GO60"]);
        }
        finally
        {
            Delete(path);
        }
    }
}
