using System.Diagnostics.CodeAnalysis;

namespace LViz.Core.GitHub;

/// <summary>
/// A keymap that lives in a GitHub repository: owner/repo, branch, and a
/// repo-root-relative <see cref="Path"/> with <c>/</c> separators.
///
/// <para>
/// Recent-keymap lists keep plain strings, so a GitHub entry is encoded as
/// one string — <c>github:{Owner}/{Repo}:{Branch}:{Path}</c>, e.g.
/// <c>github:ovandongen/zmk-config-cosmos82:main:config/cosmos82.keymap</c>.
/// Git forbids <c>:</c> in branch names and GitHub owner/repo names cannot
/// contain it either, so <c>:</c> is a safe separator; only the path may
/// carry further colons and it is always the last segment. This record is
/// that string's parser and formatter.
/// </para>
/// </summary>
/// <param name="Owner">GitHub account or organisation name.</param>
/// <param name="Repo">Repository name.</param>
/// <param name="Branch">Branch name; may contain <c>/</c> (e.g. <c>feature/x</c>).</param>
/// <param name="Path">File path relative to the repo root, <c>/</c>-separated.</param>
public sealed record GitHubKeymapRef(string Owner, string Repo, string Branch, string Path)
{
    /// <summary>Prefix marking a recent-entry string as a GitHub reference.</summary>
    public const string Prefix = "github:";

    /// <summary>Just the file name — the last <c>/</c> segment of <see cref="Path"/>.</summary>
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary><c>{Owner}/{Repo}</c>, as GitHub URLs and the API spell it.</summary>
    public string RepoFullName => $"{Owner}/{Repo}";

    /// <summary>The recent-entry string form: <c>github:{Owner}/{Repo}:{Branch}:{Path}</c>.</summary>
    public override string ToString() => $"{Prefix}{Owner}/{Repo}:{Branch}:{Path}";

    /// <summary>
    /// Whether a recent-entry string refers to a GitHub keymap. Checks only
    /// the <see cref="Prefix"/> (ordinal); use <see cref="TryParse"/> for a
    /// well-formed reference.
    /// </summary>
    public static bool IsGitHubEntry(string? entry) =>
        entry is not null && entry.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses the recent-entry string form. Rules (all ordinal): the entry
    /// must start with <see cref="Prefix"/>; the rest splits at the first
    /// <c>:</c> into <c>owner/repo</c> and remainder, and the remainder at
    /// its first <c>:</c> into branch and path (so the path may contain
    /// <c>:</c>). The owner/repo part must hold exactly one <c>/</c> with a
    /// non-empty name on either side; the branch must be non-empty; the path
    /// must be non-empty, not start with <c>/</c>, contain no <c>\</c>, and
    /// have no empty, <c>.</c> or <c>..</c> segment. Never throws; anything
    /// else yields <c>false</c>.
    /// </summary>
    public static bool TryParse(string? entry, [NotNullWhen(true)] out GitHubKeymapRef? keymap)
    {
        keymap = null;
        if (!IsGitHubEntry(entry))
        {
            return false;
        }

        var rest = entry!.Substring(Prefix.Length);

        var repoEnd = rest.IndexOf(':');
        if (repoEnd < 0)
        {
            return false;
        }

        var ownerRepo = rest[..repoEnd];
        rest = rest[(repoEnd + 1)..];

        var branchEnd = rest.IndexOf(':');
        if (branchEnd < 0)
        {
            return false;
        }

        var branch = rest[..branchEnd];
        var path = rest[(branchEnd + 1)..];

        var slash = ownerRepo.IndexOf('/');
        if (slash <= 0 || slash == ownerRepo.Length - 1 || ownerRepo.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        if (branch.Length == 0)
        {
            return false;
        }

        if (!IsValidPath(path))
        {
            return false;
        }

        keymap = new GitHubKeymapRef(
            ownerRepo[..slash],
            ownerRepo[(slash + 1)..],
            branch,
            path);
        return true;
    }

    private static bool IsValidPath(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains('\\'))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }
}
