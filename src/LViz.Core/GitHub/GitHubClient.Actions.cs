using System.Globalization;

namespace LViz.Core.GitHub;

/// <summary>
/// GitHub Actions reads: a commit's or branch's workflow runs, a run's
/// artifacts, and downloading one artifact's zip — the calls behind finding
/// and fetching a firmware build.
/// </summary>
public sealed partial class GitHubClient
{
    /// <summary>Runs asked for per page — the caller only ever wants the newest few.</summary>
    private const int RunsPerPage = 20;

    /// <summary>
    /// Workflow runs of one commit or one branch, newest first, one page of
    /// <see cref="RunsPerPage"/>:
    /// <c>GET /repos/{owner}/{repo}/actions/runs?head_sha={sha}&amp;per_page=20</c>
    /// or <c>?branch={branch}&amp;per_page=20</c>. Exactly one of
    /// <paramref name="headSha"/> and <paramref name="branch"/> must be
    /// non-blank. The sha goes out lower-cased, the branch url-escaped (so a
    /// <c>/</c> in a branch name becomes <c>%2F</c>).
    ///
    /// <para>
    /// Runs without an <c>id</c>, a <c>head_sha</c> or a <c>status</c> are
    /// skipped; <c>created_at</c> that is missing or unparsable becomes
    /// <see cref="DateTimeOffset.MinValue"/>; <c>name</c>,
    /// <c>head_branch</c> and <c>html_url</c> that are missing become empty.
    /// An answer without a <c>workflow_runs</c> array is
    /// <see cref="GitHubErrorKind.Unexpected"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Neither or both of
    /// <paramref name="headSha"/> and <paramref name="branch"/> are usable.</exception>
    public async Task<IReadOnlyList<GitHubWorkflowRun>> ListWorkflowRunsAsync(
        string accessToken,
        string owner,
        string repo,
        string? headSha,
        string? branch,
        CancellationToken ct = default)
    {
        var hasSha = !string.IsNullOrWhiteSpace(headSha);
        var hasBranch = !string.IsNullOrWhiteSpace(branch);
        if (hasSha == hasBranch)
        {
            throw new ArgumentException(
                "Exactly one of head sha or branch is required", nameof(headSha));
        }

        var query = hasSha
            ? $"head_sha={headSha!.ToLowerInvariant()}"
            : $"branch={Uri.EscapeDataString(branch!)}";

        using var request = ApiRequest(
            $"{RepoPath(owner, repo)}/actions/runs?{query}&per_page={RunsPerPage}", accessToken);

        var json = await SendForJsonAsync(request, ct);

        if (!TryGetArray(json, "workflow_runs", out var runs))
        {
            throw UnexpectedError($"GitHub {Describe(request)} returned no workflow_runs");
        }

        var result = new List<GitHubWorkflowRun>();
        foreach (var run in runs)
        {
            var id = GetInt64(run, "id");
            var sha = GetString(run, "head_sha");
            var status = GetString(run, "status");
            if (id is null || sha is null || status is null)
            {
                continue;
            }

            result.Add(new GitHubWorkflowRun(
                id.Value,
                GetString(run, "name") ?? string.Empty,
                GetString(run, "head_branch") ?? string.Empty,
                sha.ToLowerInvariant(),
                status,
                GetString(run, "conclusion"),
                GetString(run, "html_url") ?? string.Empty,
                ParseApiTime(GetString(run, "created_at")),
                GetString(GetObject(run, "head_commit"), "message")));
        }

        return result;
    }

    /// <summary>
    /// The artifacts one run uploaded, in API order:
    /// <c>GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts</c>.
    /// Entries without an <c>id</c> or a <c>name</c> are skipped, a missing
    /// <c>size_in_bytes</c> reads as 0 and a missing <c>expired</c> as false.
    /// An answer without an <c>artifacts</c> array is
    /// <see cref="GitHubErrorKind.Unexpected"/>.
    /// </summary>
    public async Task<IReadOnlyList<GitHubArtifact>> ListRunArtifactsAsync(
        string accessToken,
        string owner,
        string repo,
        long runId,
        CancellationToken ct = default)
    {
        using var request = ApiRequest(
            $"{RepoPath(owner, repo)}/actions/runs/{runId}/artifacts", accessToken);

        var json = await SendForJsonAsync(request, ct);

        if (!TryGetArray(json, "artifacts", out var artifacts))
        {
            throw UnexpectedError($"GitHub {Describe(request)} returned no artifacts");
        }

        var result = new List<GitHubArtifact>();
        foreach (var artifact in artifacts)
        {
            var id = GetInt64(artifact, "id");
            var name = GetString(artifact, "name");
            if (id is null || name is null)
            {
                continue;
            }

            result.Add(new GitHubArtifact(
                id.Value,
                name,
                GetInt64(artifact, "size_in_bytes") ?? 0L,
                GetBool(artifact, "expired")));
        }

        return result;
    }

    /// <summary>
    /// Streams one artifact's zip into <paramref name="destination"/>:
    /// <c>GET /repos/{owner}/{repo}/actions/artifacts/{artifact_id}/zip</c>.
    /// GitHub answers with a redirect to a signed storage URL that is valid
    /// for one minute; the injected <see cref="HttpClient"/> follows it, and
    /// .NET drops the <c>Authorization</c> header on the redirect, so the
    /// token never reaches the storage host. The body is never buffered: as
    /// soon as more than <paramref name="maxBytes"/> has been read — or the
    /// response announces that much in <c>Content-Length</c> — the copy stops
    /// with <see cref="GitHubErrorKind.TooLarge"/>, and a connection that
    /// drops mid-body becomes <see cref="GitHubErrorKind.Network"/>, exactly
    /// like <see cref="DownloadTarballAsync"/>.
    ///
    /// <para>
    /// An artifact GitHub has already deleted answers 410; that arrives as
    /// <see cref="GitHubErrorKind.Unexpected"/> with
    /// <see cref="GitHubApiException.StatusCode"/> set to 410 for the caller
    /// to recognise.
    /// </para>
    /// </summary>
    public async Task DownloadArtifactZipAsync(
        string accessToken,
        string owner,
        string repo,
        long artifactId,
        Stream destination,
        long maxBytes,
        CancellationToken ct = default)
    {
        using var request = ApiRequest(
            $"{RepoPath(owner, repo)}/actions/artifacts/{artifactId}/zip", accessToken);

        var response = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            if (response.Content.Headers.ContentLength is long announced && announced > maxBytes)
            {
                throw ArtifactTooLargeError(maxBytes);
            }

            var buffer = new byte[TarballChunkBytes];
            var total = 0L;

            await using (var source = await OpenBodyStreamAsync(response.Content, request, ct))
            {
                while (true)
                {
                    var read = await ReadChunkAsync(source, buffer, request, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw ArtifactTooLargeError(maxBytes);
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// An API timestamp parsed with the invariant culture;
    /// <see cref="DateTimeOffset.MinValue"/> when missing or unparsable.
    /// </summary>
    private static DateTimeOffset ParseApiTime(string? value) =>
        value is not null
        && DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static GitHubApiException ArtifactTooLargeError(long maxBytes) =>
        new(GitHubErrorKind.TooLarge, $"Firmware artifact is larger than {maxBytes / (1024 * 1024)} MB");
}
