using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Settings;
using LViz.App.Services.GitHub;

namespace LViz.App.Services;

/// <summary>A newer (or first) GitHub copy of a booked keymap, ready to load.</summary>
/// <param name="LocalPath">The cache file to load.</param>
/// <param name="ReplacesLoaded">True when the entry was already loaded (a changed
/// branch, which warrants a toast); false when the cold cache was just filled.</param>
internal sealed record GitHubKeymapUpdate(string LocalPath, bool ReplacesLoaded);

/// <summary>
/// Where keymaps come from, as policy and persistence: which entry is booked
/// for which profile (<c>LayoutJsonPaths</c>), the per-profile recents list,
/// how a stored entry resolves to a loadable file (a GitHub reference through
/// the cache copy, never the network) and the background GitHub check that
/// decides whether a newer copy should be reloaded. Applying a load to the
/// screen stays on <c>MainWindowViewModel</c>.
/// <para>
/// Lives in App, not Core, because it needs <see cref="IGitHubKeymapService"/>
/// for the cache lookup; everything it persists is Core settings. Every method
/// takes the profile id explicitly, so it holds no view state.
/// </para>
/// </summary>
internal sealed class KeymapLibrary
{
    private const string Tag = "Keymaps";

    private readonly ISettingsService _settings;
    private readonly IGitHubKeymapService? _gitHub;

    public KeymapLibrary(ISettingsService settings, IGitHubKeymapService? gitHub)
    {
        _settings = settings;
        _gitHub = gitHub;
    }

    /// <summary>
    /// File-existence seam for the recents surface — tests fake the disk here;
    /// production is <see cref="File.Exists"/>. Resolving a booked entry
    /// deliberately checks the disk directly.
    /// </summary>
    public Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>Recent entries for a profile, most-recent-first. A snapshot: settings are re-read.</summary>
    public IReadOnlyList<string> RecentsFor(string profileId) =>
        _settings.Load().RecentKeymapPaths.TryGetValue(profileId, out var paths)
            ? paths
            : Array.Empty<string>();

    /// <summary>The entry booked as the profile's last-used keymap, if any.</summary>
    public string? BookedEntry(string profileId) =>
        _settings.Load().LayoutJsonPaths.TryGetValue(profileId, out var entry) && !string.IsNullOrEmpty(entry)
            ? entry
            : null;

    /// <summary>
    /// Books <paramref name="entry"/> as the profile's last-used keymap and moves
    /// it to the front of its recents, in one write. <paramref name="takeFromProfileId"/>,
    /// when given, loses its last-used slot if that slot held the same entry.
    /// </summary>
    public void Book(string profileId, string entry, string? takeFromProfileId = null)
    {
        _settings.Update(s =>
        {
            var paths = new Dictionary<string, string>(s.LayoutJsonPaths) { [profileId] = entry };
            if (takeFromProfileId is not null
                && !string.Equals(takeFromProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                && paths.TryGetValue(takeFromProfileId, out var old)
                && string.Equals(old, entry, StringComparison.Ordinal))
                paths.Remove(takeFromProfileId);
            var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths)
            {
                [profileId] = RecentKeymaps.Push(
                    s.RecentKeymapPaths.TryGetValue(profileId, out var recents)
                        ? recents
                        : Array.Empty<string>(),
                    entry),
            };
            return s with { LayoutJsonPaths = paths, RecentKeymapPaths = recentPaths };
        }, Tag);
    }

    /// <summary>
    /// Drops the profile's booking, but only when it still holds
    /// <paramref name="entry"/> (a booking that points elsewhere belongs to
    /// another keymap). No write when there is nothing to remove.
    /// </summary>
    public void Unbook(string profileId, string entry)
    {
        if (!string.Equals(BookedEntry(profileId), entry, StringComparison.Ordinal))
            return;
        _settings.Update(s =>
        {
            if (!s.LayoutJsonPaths.TryGetValue(profileId, out var current)
                || !string.Equals(current, entry, StringComparison.Ordinal))
                return s;
            var paths = new Dictionary<string, string>(s.LayoutJsonPaths);
            paths.Remove(profileId);
            return s with { LayoutJsonPaths = paths };
        }, Tag);
    }

    /// <summary>Removes one entry from the profile's recents. No write when it is not listed.</summary>
    public void RemoveRecent(string profileId, string entry)
    {
        if (!RecentsFor(profileId).Contains(entry, StringComparer.Ordinal))
            return;
        _settings.Update(s =>
        {
            if (!s.RecentKeymapPaths.TryGetValue(profileId, out var recents))
                return s;
            var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths)
            {
                [profileId] = RecentKeymaps.Remove(recents, entry),
            };
            return s with { RecentKeymapPaths = recentPaths };
        }, Tag);
    }

    /// <summary>Drops the profile's recents wholesale; siblings keep theirs. No write when empty.</summary>
    public void ClearRecents(string profileId)
    {
        if (RecentsFor(profileId).Count == 0) return;
        _settings.Update(s =>
        {
            if (!s.RecentKeymapPaths.ContainsKey(profileId))
                return s;
            var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths);
            recentPaths.Remove(profileId);
            return s with { RecentKeymapPaths = recentPaths };
        }, Tag);
    }

    /// <summary>
    /// Turns a stored entry into a file to load. A GitHub reference resolves
    /// through the service's local cache only (a cold cache doesn't resolve);
    /// a local path resolves when the file is on disk.
    /// </summary>
    public bool TryResolveEntry(string entry, out string path)
    {
        if (GitHubKeymapRef.TryParse(entry, out var keymap))
        {
            var cached = _gitHub?.LocalPathFor(keymap);
            path = cached ?? "";
            return cached is not null;
        }

        path = entry;
        return File.Exists(entry);
    }

    /// <summary>
    /// The profile's booked entry plus the file it resolves to. A local entry
    /// whose file has gone missing is logged and unbooked so startup stops
    /// complaining; a GitHub entry without a cache copy is kept (the background
    /// check fetches it). <paramref name="entry"/> is the stored entry even when
    /// nothing resolved ("" when nothing is booked).
    /// </summary>
    public bool TryGetBooked(string profileId, out string path, out string entry)
    {
        path = "";
        entry = "";
        var s = _settings.Load();
        if (!s.LayoutJsonPaths.TryGetValue(profileId, out var stored) || string.IsNullOrWhiteSpace(stored))
            return false;
        entry = stored;
        if (TryResolveEntry(stored, out path))
            return true;

        if (GitHubKeymapRef.IsGitHubEntry(stored))
        {
            DiagnosticLog.Info(Tag, $"Stored layout for {profileId} has no cached copy yet: {stored}");
            return false;
        }

        DiagnosticLog.Warn(Tag, $"Stored layout for {profileId} is missing on disk: {stored}");
        _settings.Update(curr =>
        {
            var paths = new Dictionary<string, string>(curr.LayoutJsonPaths);
            paths.Remove(profileId);
            return curr with { LayoutJsonPaths = paths };
        }, Tag);
        return false;
    }

    /// <summary>
    /// After a profile's booked GitHub entry was restored: asks GitHub whether
    /// the branch moved and returns the copy to load, or null for "nothing to
    /// do" (local entry, no service, signed out or offline, the user moved on,
    /// unchanged, or the file left the branch). Silent on failure.
    /// <paramref name="current"/> is read after the network wait: the user may
    /// have switched board or opened something else meanwhile, in which case a
    /// result is only applied for the same profile and when the entry is still
    /// loaded or nothing is.
    /// </summary>
    public async Task<GitHubKeymapUpdate?> CheckGitHubAsync(
        string entry, string profileId, Func<(string ProfileId, string? LoadedEntry)> current)
    {
        if (_gitHub is null || !GitHubKeymapRef.TryParse(entry, out var keymap))
            return null;

        try
        {
            await _gitHub.EnsureRestoredAsync();
            if (!_gitHub.IsSignedIn)
                return null;

            var result = await _gitHub.FetchRepoAsync(keymap.Owner, keymap.Repo, keymap.Branch);

            var (nowProfile, loadedEntry) = current();
            var wasLoaded = loadedEntry == entry;
            if (nowProfile != profileId || !(wasLoaded || loadedEntry is null))
                return null;
            if (wasLoaded && !result.Downloaded)
                return null;

            if (_gitHub.LocalPathFor(keymap) is not string path)
            {
                DiagnosticLog.Warn(Tag, $"'{keymap}' is gone from the newest GitHub copy; keeping the loaded one");
                return null;
            }

            DiagnosticLog.Info(Tag, wasLoaded
                ? $"'{keymap}' changed on GitHub, reloading"
                : $"'{keymap}' fetched from GitHub (no cached copy)");
            return new GitHubKeymapUpdate(path, wasLoaded);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Info(Tag, $"Background GitHub check for '{keymap}' skipped: {ex.Message}");
            return null;
        }
    }
}
