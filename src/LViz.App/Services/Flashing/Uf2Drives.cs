using System.Text;
using LViz.Core.Firmware;

namespace LViz.App.Services.Flashing;

/// <summary>
/// A mounted UF2 bootloader drive: the small USB stick a ZMK half shows
/// after it rebooted into its bootloader (e.g. <c>NICENANO</c>,
/// <c>GLV80LHBOOT</c>). Every such drive carries an <c>INFO_UF2.TXT</c>
/// at its root, which is what makes it recognisable.
/// </summary>
/// <param name="RootPath">Mount root, e.g. <c>/Volumes/NICENANO</c> or <c>E:\</c>.</param>
/// <param name="Label">Windows volume label (drive name when empty), otherwise the folder name.</param>
/// <param name="InfoText">The first 1 KiB of <c>INFO_UF2.TXT</c> as ASCII — contains the Board-ID.</param>
public sealed record Uf2Drive(string RootPath, string Label, string InfoText)
{
    /// <summary>
    /// The half this drive belongs to, when its label says so: MoErgo's
    /// bootloaders name the side (<c>GLV80LHBOOT</c>, <c>GO60RHBOOT</c>).
    /// <see cref="FirmwareSide.Unknown"/> for every other drive
    /// (<c>NICENANO</c> and the like give no side).
    /// </summary>
    public FirmwareSide Side =>
        Label.EndsWith("LHBOOT", StringComparison.OrdinalIgnoreCase) ? FirmwareSide.Left
        : Label.EndsWith("RHBOOT", StringComparison.OrdinalIgnoreCase) ? FirmwareSide.Right
        : FirmwareSide.Unknown;
}

/// <summary>
/// Where the flash flow looks for bootloader drives. An interface so the
/// watcher can be driven by a scripted sequence of mount tables in tests.
/// </summary>
public interface IUf2DriveSource
{
    /// <summary>The bootloader drives mounted right now.</summary>
    IReadOnlyList<Uf2Drive> List();
}

/// <summary>
/// <see cref="IUf2DriveSource"/> over the real mounts. Only these roots are
/// scanned — macOS <c>/Volumes/*</c>; Linux <c>/media/*/*</c>,
/// <c>/run/media/*/*</c> and <c>/mnt/*</c>; Windows removable drives that
/// report ready — because probing arbitrary (possibly network) mounts can
/// hang the poll loop. A candidate whose <c>INFO_UF2.TXT</c> read fails is
/// skipped; the listing itself never throws.
/// </summary>
public sealed class SystemUf2DriveSource : IUf2DriveSource
{
    internal const string InfoFileName = "INFO_UF2.TXT";
    private const int InfoTextBytes = 1024;

    public IReadOnlyList<Uf2Drive> List()
    {
        var drives = new List<Uf2Drive>();
        foreach (var (root, label) in CandidateRoots())
        {
            try
            {
                var infoPath = Path.Combine(root, InfoFileName);
                if (!File.Exists(infoPath))
                    continue;
                drives.Add(new Uf2Drive(root, label ?? Path.GetFileName(root), ReadInfoText(infoPath)));
            }
            catch (IOException) { /* a half-booting stick mid-enumeration: skip it */ }
            catch (UnauthorizedAccessException) { }
        }

        drives.Sort(static (a, b) => string.CompareOrdinal(a.RootPath, b.RootPath));
        return drives;
    }

    private static IEnumerable<(string Root, string? Label)> CandidateRoots()
    {
        if (OperatingSystem.IsWindows())
            return WindowsRemovableRoots();
        if (OperatingSystem.IsMacOS())
            return SingleLevelRoots("/Volumes");

        // Linux: udisks mounts under /run/media/<user>/, udisks2 historically
        // under /media/<user>/, manual mounts directly under /mnt. An
        // unmounted device simply appears in none of them and LViz keeps
        // waiting — the flash window tells the user to mount it.
        return Grandchildren("/media")
            .Concat(Grandchildren("/run/media"))
            .Concat(SingleLevelRoots("/mnt"));
    }

    private static IEnumerable<(string Root, string? Label)> WindowsRemovableRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        // Fixed and network drives are deliberately out: a network mount can
        // hang the 500 ms poll, and the root of a system drive is never a
        // bootloader stick.
        return drives
            .Where(d => d.DriveType == DriveType.Removable)
            .Select(Describe)
            .Where(r => r is not null)
            .Select(r => r!.Value)!;
    }

    private static (string Root, string? Label)? Describe(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady)
                return null;
            var label = drive.VolumeLabel;
            return (drive.RootDirectory.FullName, string.IsNullOrEmpty(label) ? drive.Name : label);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Root, string? Label)> SingleLevelRoots(string parent) =>
        SafeDirectories(parent).Select(dir => (dir, (string?)null));

    /// <summary>Child-of-child folders, for the <c>&lt;parent&gt;/&lt;user&gt;/&lt;volume&gt;</c> mount layout.</summary>
    private static IEnumerable<(string Root, string? Label)> Grandchildren(string parent) =>
        SafeDirectories(parent).SelectMany(mid => SafeDirectories(mid).Select(dir => (dir, (string?)null)));

    /// <summary>
    /// The child folders of <paramref name="parent"/>; a missing or unreadable
    /// parent (it usually just isn't there on this distro) means none.
    /// </summary>
    private static IReadOnlyList<string> SafeDirectories(string parent)
    {
        var dirs = new List<string>();
        try
        {
            dirs.AddRange(Directory.GetDirectories(parent));
        }
        catch (IOException) { /* missing or unstable mount parent */ }
        catch (UnauthorizedAccessException) { }
        return dirs;
    }

    /// <summary>The first 1 KiB of the INFO file, decoded as ASCII.</summary>
    private static string ReadInfoText(string infoPath)
    {
        using var stream = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[InfoTextBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.AsSpan(total));
            if (read == 0)
                break;
            total += read;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }
}

/// <summary>
/// Waits for the bootloader drive to appear (or for it to disappear after
/// the copy) by polling an <see cref="IUf2DriveSource"/> every
/// <see cref="PollInterval"/>. The flash flow snapshots the mounts before
/// triggering <c>0xB0</c> and then waits for a root that was not in that
/// snapshot, so a drive that was already mounted never counts as "the half
/// rebooted". The timeout is counted by the summed delays handed to the
/// injected <paramref name="delay"/>, not by the wall clock (the same trick
/// as <c>GitHubClient.WaitForDeviceAuthorizationAsync</c>), so a test whose
/// delay returns instantly still gets a bounded loop.
/// </summary>
/// <param name="source">Where to list the mounted bootloader drives.</param>
/// <param name="delay">How to wait between polls; tests inject an instant one.</param>
public sealed class Uf2DriveWatcher(IUf2DriveSource source, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>How long the watcher waits between two listings.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The roots of the currently mounted drives, for a later <see cref="WaitForNewAsync"/>.</summary>
    public IReadOnlySet<string> Snapshot()
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var drive in source.List())
            roots.Add(drive.RootPath);
        return roots;
    }

    /// <summary>
    /// The first drive whose root is not in <paramref name="before"/> — a
    /// drive already listed there is never returned. Checks once at once,
    /// then after each poll delay; null once the summed delays reach
    /// <paramref name="timeout"/>. <see cref="Timeout.InfiniteTimeSpan"/>
    /// waits until <paramref name="ct"/> cancels the wait.
    /// </summary>
    public async Task<Uf2Drive?> WaitForNewAsync(
        IReadOnlySet<string> before, TimeSpan timeout, CancellationToken ct = default)
    {
        var (hit, drive) = await PollAsync(
            listed =>
            {
                var fresh = listed.FirstOrDefault(d => !before.Contains(d.RootPath));
                return (fresh is not null, fresh!);
            },
            timeout, ct);
        return hit ? drive : null;
    }

    /// <summary>
    /// True once no listed drive has <paramref name="rootPath"/> anymore —
    /// the half rebooted and unmounted itself, which is the normal end of a
    /// copy. False on timeout.
    /// </summary>
    public async Task<bool> WaitGoneAsync(string rootPath, TimeSpan timeout, CancellationToken ct = default)
    {
        var (hit, _) = await PollAsync(
            listed => (!listed.Any(d => string.Equals(d.RootPath, rootPath, StringComparison.Ordinal)), true),
            timeout, ct);
        return hit;
    }

    /// <summary>
    /// The one poll loop behind both waits: probe the listing at once, then
    /// again after every delay; cancel-check before every probe.
    /// </summary>
    private async Task<(bool Hit, T Value)> PollAsync<T>(
        Func<IReadOnlyList<Uf2Drive>, (bool Hit, T Value)> probe, TimeSpan timeout, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var (hit, value) = probe(source.List());
            if (hit)
                return (true, value);
            if (timeout != Timeout.InfiniteTimeSpan && waited >= timeout)
                return (false, default!);

            await _delay(PollInterval, ct);
            waited += PollInterval;
        }
    }
}
