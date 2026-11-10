namespace LViz.Core.GitHub;

/// <summary>
/// One file handed to <see cref="GitHubClient.CommitFilesAsync"/>: the whole
/// new text of a file and where it lives in the repo.
/// </summary>
/// <param name="Path">Repo-root-relative path with <c>/</c> separators, e.g. <c>config/west.yml</c>.</param>
/// <param name="Content">The file's entire new text, as UTF-8-decodable text.</param>
public sealed record GitHubFileWrite(string Path, string Content);

/// <summary>
/// What a successful multi-file Git Data commit returned. SHAs are
/// lower-case, as GitHub writes them.
/// </summary>
/// <param name="CommitSha">The commit created on the branch.</param>
/// <param name="ParentSha">The base commit the branch moved from.</param>
public sealed record GitHubTreeCommitResult(string CommitSha, string ParentSha);
