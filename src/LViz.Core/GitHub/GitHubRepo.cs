namespace LViz.Core.GitHub;

/// <summary>
/// One repository the user's "LViz for ZMK" GitHub App installation can see.
/// <paramref name="Owner"/> and <paramref name="Name"/> are the GitHub
/// handles as they appear in the URL, so <see cref="FullName"/> is what a
/// <c>github:owner/repo:branch:path</c> reference uses.
/// </summary>
/// <param name="Owner">Owner login (a user or an organisation).</param>
/// <param name="Name">Repository name without the owner.</param>
/// <param name="DefaultBranch">The repo's default branch; <c>main</c> when the
/// API did not say.</param>
/// <param name="IsPrivate">Whether GitHub reports the repo as private.</param>
public sealed record GitHubRepo(string Owner, string Name, string DefaultBranch, bool IsPrivate)
{
    /// <summary><c>owner/name</c>, the identity used for deduplication.</summary>
    public string FullName => $"{Owner}/{Name}";
}
