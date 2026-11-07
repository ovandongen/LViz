using System.Text.Json;

namespace LViz.Core.GitHub;

/// <summary>Repository reads: installed repos, branches, a branch head.</summary>
public sealed partial class GitHubClient
{
    /// <summary>
    /// Every repository behind the user's GitHub App installations:
    /// <c>GET /user/installations</c> first (LViz only sees repos whose
    /// installation it belongs to), then
    /// <c>GET /user/installations/{id}/repositories</c> per installation. Both
    /// are paged 100 at a time until the collected entries reach the
    /// response's <c>total_count</c> or a page comes back empty.
    ///
    /// <para>
    /// Entries without a <c>name</c> or an <c>owner.login</c> are skipped, a
    /// missing <c>default_branch</c> becomes <c>main</c> and a missing
    /// <c>private</c> becomes false. Repos that appear under several
    /// installations are listed once; the result is sorted by owner, then
    /// name, case-insensitively.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepo>> ListInstalledReposAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        var installations = await GetPagedAsync(
            "/user/installations", accessToken, "installations", ct);

        var byFullName = new Dictionary<string, GitHubRepo>(StringComparer.OrdinalIgnoreCase);

        foreach (var installation in installations)
        {
            var id = GetInt64(installation, "id");
            if (id is null)
            {
                continue;
            }

            var entries = await GetPagedAsync(
                $"/user/installations/{id.Value}/repositories", accessToken, "repositories", ct);

            foreach (var entry in entries)
            {
                var name = GetString(entry, "name");
                var owner = GetString(GetObject(entry, "owner"), "login");
                if (name is null || owner is null)
                {
                    continue;
                }

                var repo = new GitHubRepo(
                    owner,
                    name,
                    GetString(entry, "default_branch") ?? "main",
                    GetBool(entry, "private"));

                // The same repo can sit behind several installations; the
                // first sighting wins.
                byFullName.TryAdd(repo.FullName, repo);
            }
        }

        return byFullName.Values
            .OrderBy(repo => repo.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Branch names of a repository, in API order:
    /// <c>GET /repos/{owner}/{repo}/branches</c>, 100 per page, paging while a
    /// page is full. Owner and repo are url-escaped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(
        string accessToken,
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var path = RepoPath(owner, repo) + "/branches";
        var names = new List<string>();

        for (var page = 1; ; page++)
        {
            using var request = ApiRequest($"{path}?per_page={PageSize}&page={page}", accessToken);
            var json = await SendForJsonAsync(request, ct);

            if (json.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var received = 0;
            foreach (var entry in json.EnumerateArray())
            {
                received++;
                var name = GetString(entry, "name");
                if (name is not null)
                {
                    names.Add(name);
                }
            }

            if (received < PageSize)
            {
                break;
            }
        }

        return names;
    }

    /// <summary>
    /// The commit a branch points at: <c>GET /repos/{owner}/{repo}/commits/{branch}</c>
    /// answered as the bare SHA text (accept <c>application/vnd.github.sha</c>).
    /// The branch is escaped segment by segment, so <c>feature/x y</c> goes out
    /// as <c>feature/x%20y</c>. The trimmed answer must be exactly 40 hex
    /// characters — anything else is <see cref="GitHubErrorKind.Unexpected"/>.
    /// Returned lower-case, which is how GitHub writes SHAs.
    /// </summary>
    public async Task<string> GetBranchHeadAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken ct = default)
    {
        var path = $"{RepoPath(owner, repo)}/commits/{EscapePathSegments(branch)}";
        using var request = ApiRequest(path, accessToken, ShaAccept);

        using var response = await SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        var sha = text.Trim().ToLowerInvariant();
        if (sha.Length != ShaLength || !sha.All(Uri.IsHexDigit))
        {
            throw UnexpectedError($"GitHub {Describe(request)} returned an unexpected commit sha");
        }

        return sha;
    }
}
