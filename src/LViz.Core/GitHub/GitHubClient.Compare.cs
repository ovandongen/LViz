namespace LViz.Core.GitHub;

/// <summary>
/// The compare read: which files one commit changed since another — the
/// input for deciding whether the peripheral half of a board needs a flash
/// (<c>LViz.Core.Firmware.FirmwareChangeRule</c>).
/// </summary>
public sealed partial class GitHubClient
{
    /// <summary>
    /// Hard cap of GitHub's compare answer: with more changes than this the
    /// <c>files</c> array is cut off silently, so a full-looking list is not
    /// the whole truth.
    /// </summary>
    private const int CompareFileCap = 300;

    /// <summary>
    /// The files <paramref name="headSha"/> changed since
    /// <paramref name="baseSha"/>:
    /// <c>GET /repos/{owner}/{repo}/compare/{base}...{head}</c> — three dots,
    /// so the changes are the ones since the two refs' merge-base. Both SHAs
    /// go out lower-cased and url-escaped. The endpoint needs Contents:
    /// read, which the main App has, so the caller runs this on the main
    /// login, not the builds one.
    ///
    /// <para>
    /// <see cref="GitHubCompareResult.Files"/> holds every
    /// <c>filename</c> plus every <c>previous_filename</c> (a rename touches
    /// both paths), in API order, without duplicates.
    /// <see cref="GitHubCompareResult.Truncated"/> is true when the answer
    /// filled the 300-file cap, so changes may exist beyond the listed ones.
    /// A missing <c>files</c> array is GitHub's "no changes"
    /// (<c>identical</c>): an empty list, not truncated. An unknown SHA (e.g.
    /// after a force-push) answers 404, which arrives as
    /// <see cref="GitHubErrorKind.NotFound"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseSha"/> or <paramref name="headSha"/> is blank.
    /// </exception>
    public async Task<GitHubCompareResult> CompareCommitsAsync(
        string accessToken,
        string owner,
        string repo,
        string baseSha,
        string headSha,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            throw new ArgumentException("Base sha is required", nameof(baseSha));
        }

        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new ArgumentException("Head sha is required", nameof(headSha));
        }

        var path = $"{RepoPath(owner, repo)}/compare/"
            + $"{Uri.EscapeDataString(baseSha.ToLowerInvariant())}..."
            + $"{Uri.EscapeDataString(headSha.ToLowerInvariant())}";

        using var request = ApiRequest(path, accessToken);
        var json = await SendForJsonAsync(request, ct);

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = 0;
        foreach (var file in GetArray(json, "files"))
        {
            entries++;

            var filename = GetString(file, "filename");
            if (filename is not null && seen.Add(filename))
            {
                files.Add(filename);
            }

            // A rename touches both paths: the old one too, or a rule that
            // reads the list would miss where the change came from.
            var previous = GetString(file, "previous_filename");
            if (previous is not null && seen.Add(previous))
            {
                files.Add(previous);
            }
        }

        return new GitHubCompareResult(files, entries >= CompareFileCap);
    }
}

/// <summary>
/// What a compare between two commits found: every changed path (a rename
/// contributes both its old and its new path), in API order without
/// duplicates, and whether GitHub cut the list at its 300-file cap so more
/// changes may exist than are named.
/// </summary>
public sealed record GitHubCompareResult(IReadOnlyList<string> Files, bool Truncated);
