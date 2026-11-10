using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LViz.Core.GitHub;

/// <summary>
/// Multi-file commits through GitHub's Git Data API: read a base commit,
/// create a tree with new blobs, commit it, and move a branch ref — the four
/// calls that land several files in one commit.
/// </summary>
public sealed partial class GitHubClient
{
    /// <summary>Git file mode of a regular, non-executable text file.</summary>
    private const string BlobFileMode = "100644";

    /// <summary>
    /// Commits <paramref name="files"/> as one commit on
    /// <paramref name="branch"/>, in four calls: the base commit
    /// (<c>GET .../git/commits/{baseCommitSha}</c>) gives its tree; a
    /// <c>POST .../git/trees</c> puts one <c>100644</c> blob entry per file,
    /// in the given order, on top of that tree; a
    /// <c>POST .../git/commits</c> commits the new tree with
    /// <paramref name="message"/> and <paramref name="baseCommitSha"/> as the
    /// only parent; a <c>PATCH .../git/refs/heads/{branch}</c> (escaped
    /// segment by segment) moves the branch to it with <c>force</c> false.
    ///
    /// <para>
    /// Nothing is retried. If the ref update fails the branch is untouched
    /// (the stray tree and commit are GitHub's to clean up): a 422 there —
    /// the base commit is no longer the branch tip — arrives as
    /// <see cref="GitHubErrorKind.Conflict"/>, as does a 409; a 422 in any
    /// earlier step stays <see cref="GitHubErrorKind.Unexpected"/>. Any
    /// answer missing its <c>sha</c> (or a base commit missing its
    /// <c>tree</c>) is <see cref="GitHubErrorKind.Unexpected"/>. Returned
    /// SHAs are lower-case, which is how GitHub writes them.
    /// </para>
    /// </summary>
    /// <param name="baseCommitSha">Commit the branch must still point at; must not be blank.</param>
    /// <param name="files">Files to write, in order; at least one, and no path twice.</param>
    /// <param name="message">Commit message; must not be blank.</param>
    public async Task<GitHubTreeCommitResult> CommitFilesAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        string baseCommitSha,
        IReadOnlyList<GitHubFileWrite> files,
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Commit message is required", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(baseCommitSha))
        {
            throw new ArgumentException("Base commit SHA is required", nameof(baseCommitSha));
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required", nameof(files));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ValidateRepoFilePath(file.Path, nameof(files));
            if (!seen.Add(file.Path))
            {
                throw new ArgumentException($"File path '{file.Path}' is listed twice", nameof(files));
            }
        }

        var root = RepoPath(owner, repo);

        using var baseCommitRequest = ApiRequest($"{root}/git/commits/{baseCommitSha}", accessToken);
        var baseCommitJson = await SendForJsonAsync(baseCommitRequest, ct);
        var baseTreeSha = GetString(GetObject(baseCommitJson, "tree"), "sha")
            ?? throw UnexpectedError($"GitHub {Describe(baseCommitRequest)} returned no sha");

        var entries = new JsonArray();
        foreach (var file in files)
        {
            entries.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["mode"] = BlobFileMode,
                ["type"] = "blob",
                ["content"] = file.Content,
            });
        }

        var treeBody = new JsonObject
        {
            ["base_tree"] = baseTreeSha,
            ["tree"] = entries,
        };

        using var treeRequest = ApiRequest(
            $"{root}/git/trees", accessToken, method: HttpMethod.Post, content: JsonContent.Create(treeBody));
        var treeJson = await SendForJsonAsync(treeRequest, ct);
        var newTreeSha = GetString(treeJson, "sha")
            ?? throw UnexpectedError($"GitHub {Describe(treeRequest)} returned no sha");

        var commitBody = new JsonObject
        {
            ["message"] = message,
            ["tree"] = newTreeSha,
            ["parents"] = new JsonArray { baseCommitSha },
        };

        using var commitRequest = ApiRequest(
            $"{root}/git/commits", accessToken, method: HttpMethod.Post, content: JsonContent.Create(commitBody));
        var commitJson = await SendForJsonAsync(commitRequest, ct);
        var newCommitSha = GetString(commitJson, "sha")
            ?? throw UnexpectedError($"GitHub {Describe(commitRequest)} returned no sha");

        var refPath = $"{root}/git/refs/heads/{EscapePathSegments(branch)}";
        var refBody = new JsonObject
        {
            ["sha"] = newCommitSha,
            ["force"] = false,
        };

        using var refRequest = ApiRequest(
            refPath, accessToken, method: HttpMethod.Patch, content: JsonContent.Create(refBody));

        try
        {
            await SendForJsonAsync(refRequest, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 422)
        {
            // The base commit is no longer the tip of the branch: someone
            // pushed since it was read. A 409 already arrives as Conflict.
            throw new GitHubApiException(
                GitHubErrorKind.Conflict, $"GitHub PATCH {refPath} returned 422", 422);
        }

        return new GitHubTreeCommitResult(
            newCommitSha.ToLowerInvariant(),
            baseCommitSha.ToLowerInvariant());
    }

    /// <summary>
    /// A repo-root-relative <c>/</c> path for a tree entry: not blank, not
    /// absolute, no <c>\</c>, and no empty, <c>.</c> or <c>..</c> segment.
    /// </summary>
    private static void ValidateRepoFilePath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path is required", paramName);
        }

        if (path.StartsWith('/'))
        {
            throw new ArgumentException($"File path '{path}' must be relative to the repo root", paramName);
        }

        if (path.Contains('\\'))
        {
            throw new ArgumentException($"File path '{path}' must use '/' separators", paramName);
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                throw new ArgumentException($"File path '{path}' has an invalid segment", paramName);
            }
        }
    }
}
