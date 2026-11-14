using LViz.App.Services.Flashing;
using Xunit;

namespace LViz.Tests.Flashing;

/// <summary>
/// Finding the bootloader drive: the watcher's two polls (a drive that was
/// not in the pre-trigger snapshot appearing; a copied drive vanishing),
/// with the timeout counted by the summed delays so an instant test delay
/// still terminates, and the live mount source staying silent instead of
/// throwing on whatever the test machine has mounted.
/// </summary>
public sealed class Uf2DrivesTests
{
    private const string A = "/Volumes/A";
    private const string B = "/Volumes/B";

    /// <summary>
    /// Scripted mount table: every <see cref="Enqueue"/> is one answer to
    /// <see cref="List"/>, and the last answer keeps repeating once the
    /// script runs out — a drive that showed up stays mounted.
    /// </summary>
    private sealed class QueueSource : IUf2DriveSource
    {
        private readonly Queue<IReadOnlyList<Uf2Drive>> _answers = new();
        private IReadOnlyList<Uf2Drive>? _last;

        public void Enqueue(params string[] roots) =>
            _answers.Enqueue(roots.Select(r => new Uf2Drive(r, r, "UF2 Bootloader")).ToList());

        public IReadOnlyList<Uf2Drive> List()
        {
            if (_answers.Count > 0)
                _last = _answers.Dequeue();
            return _last ?? Array.Empty<Uf2Drive>();
        }
    }

    /// <summary>A delay that returns at once but records the interval it was asked for.</summary>
    private static Func<TimeSpan, CancellationToken, Task> InstantDelay(List<TimeSpan> calls) =>
        (interval, _) =>
        {
            calls.Add(interval);
            return Task.CompletedTask;
        };

    private static IReadOnlySet<string> Roots(params string[] roots) =>
        roots.ToHashSet(StringComparer.Ordinal);

    // --- WaitForNewAsync ---

    [Fact]
    public async Task WaitForNew_ReturnsDriveThatAppearedLater_AfterTwoDelays()
    {
        var source = new QueueSource();
        source.Enqueue(A);
        source.Enqueue(A);
        source.Enqueue(A, B);
        var delays = new List<TimeSpan>();
        var watcher = new Uf2DriveWatcher(source, InstantDelay(delays));

        var drive = await watcher.WaitForNewAsync(Roots(A), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(drive);
        Assert.Equal(B, drive.RootPath);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, d => Assert.Equal(Uf2DriveWatcher.PollInterval, d));
    }

    [Fact]
    public async Task WaitForNew_NothingNew_NullWhenSummedDelaysReachTimeout()
    {
        var source = new QueueSource();
        source.Enqueue(A);
        var delays = new List<TimeSpan>();
        var watcher = new Uf2DriveWatcher(source, InstantDelay(delays));

        var drive = await watcher.WaitForNewAsync(Roots(A), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Null(drive);
        // Two seconds of budget in 500 ms steps, one final check after the
        // fourth delay — and nothing past that.
        Assert.Equal(4, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(500), d));
    }

    [Fact]
    public async Task WaitForNew_DriveAlreadyInBefore_IsNeverReturned()
    {
        var source = new QueueSource();
        source.Enqueue(A, B);
        var watcher = new Uf2DriveWatcher(source, InstantDelay(new List<TimeSpan>()));

        var drive = await watcher.WaitForNewAsync(Roots(A, B), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(drive);
    }

    [Fact]
    public async Task WaitForNew_CancelledToken_Throws()
    {
        var source = new QueueSource();
        source.Enqueue(A);
        var delays = new List<TimeSpan>();
        var watcher = new Uf2DriveWatcher(source, InstantDelay(delays));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // An infinite timeout only ever ends through cancellation.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            watcher.WaitForNewAsync(Roots(A), Timeout.InfiniteTimeSpan, cts.Token));
        Assert.Empty(delays);
    }

    // --- WaitGoneAsync ---

    [Fact]
    public async Task WaitGone_DriveVanishes_ReturnsTrue()
    {
        var source = new QueueSource();
        source.Enqueue(B);
        source.Enqueue(B);
        source.Enqueue();
        var delays = new List<TimeSpan>();
        var watcher = new Uf2DriveWatcher(source, InstantDelay(delays));

        Assert.True(await watcher.WaitGoneAsync(B, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task WaitGone_DriveStays_ReturnsFalseOnTimeout()
    {
        var source = new QueueSource();
        source.Enqueue(B);
        var delays = new List<TimeSpan>();
        var watcher = new Uf2DriveWatcher(source, InstantDelay(delays));

        Assert.False(await watcher.WaitGoneAsync(B, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(2, delays.Count);
    }

    // --- Snapshot / PollInterval / the live source ---

    [Fact]
    public async Task Snapshot_CurrentRoots()
    {
        var source = new QueueSource();
        source.Enqueue(B, A);
        var watcher = new Uf2DriveWatcher(source);

        var roots = await watcher.SnapshotAsync();

        Assert.Equal(2, roots.Count);
        Assert.Contains(A, roots);
        Assert.Contains(B, roots);
    }

    [Fact]
    public async Task HungListing_DoesNotBlockTheCaller_AndCancelStopsTheWait()
    {
        // A mount that hangs on access (a stalled network share) blocks the
        // listing: the caller gets its task back at once, and a cancel ends
        // the wait while the listing is still stuck.
        using var release = new ManualResetEventSlim();
        var watcher = new Uf2DriveWatcher(new HangingSource(release));
        using var cts = new CancellationTokenSource();

        var wait = watcher.WaitForNewAsync(new HashSet<string>(), Timeout.InfiniteTimeSpan, cts.Token);
        Assert.False(wait.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        release.Set();
    }

    private sealed class HangingSource(ManualResetEventSlim release) : IUf2DriveSource
    {
        public IReadOnlyList<Uf2Drive> List()
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return [];
        }
    }

    [Fact]
    public void PollInterval_Is500Ms() =>
        Assert.Equal(TimeSpan.FromMilliseconds(500), Uf2DriveWatcher.PollInterval);

    [Fact]
    public void SystemSource_List_DoesNotThrowOnThisMachine()
    {
        // No assertions on the contents — this machine may have zero or
        // several drives mounted, and /Volumes or /mnt may not even exist.
        var drives = new SystemUf2DriveSource().List();
        Assert.NotNull(drives);
    }
}
