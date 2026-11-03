namespace LViz.Core.GitHub;

/// <summary>
/// What a successful <c>PUT /repos/{owner}/{repo}/contents/{path}</c>
/// returned. All SHAs are lower-case, as GitHub writes them.
/// </summary>
/// <param name="CommitSha">The commit GitHub created on the branch.</param>
/// <param name="BlobSha">The file's blob SHA after the commit.</param>
/// <param name="ParentSha">
/// The commit's first parent, or null when the answer carries none (the very
/// first commit on a branch).
/// </param>
public sealed record GitHubCommitResult(string CommitSha, string BlobSha, string? ParentSha);
