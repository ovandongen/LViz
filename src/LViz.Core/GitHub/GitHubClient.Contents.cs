using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LViz.Core.GitHub;

/// <summary>Tarball download and the Contents API (put file, file blob SHA).</summary>
public sealed partial class GitHubClient
{
    private const int TarballChunkBytes = 81920;

    /// <summary>
    /// Streams <c>GET /repos/{owner}/{repo}/tarball/{sha}</c> into
    /// <paramref name="destination"/> in 80 KiB chunks. GitHub answers with a
    /// redirect to codeload, which the injected <see cref="HttpClient"/>
    /// follows. The body is never buffered: as soon as more than
    /// <paramref name="maxBytes"/> has been read — or the response announces
    /// that much in <c>Content-Length</c> — the copy stops and
    /// <see cref="GitHubErrorKind.TooLarge"/> is thrown, so an oversized repo
    /// cannot fill the disk. A connection that drops mid-body (transport
    /// error or timeout while reading) becomes
    /// <see cref="GitHubErrorKind.Network"/>, which is what triggers the
    /// caller's offline-cache fallback; an error writing to
    /// <paramref name="destination"/> propagates as is.
    /// </summary>
    public async Task DownloadTarballAsync(
        string accessToken,
        string owner,
        string repo,
        string sha,
        Stream destination,
        long maxBytes,
        CancellationToken ct = default)
    {
        using var request = ApiRequest($"{RepoPath(owner, repo)}/tarball/{sha}", accessToken);

        var response = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            if (response.Content.Headers.ContentLength is long announced && announced > maxBytes)
            {
                throw TooLargeError(maxBytes);
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
                        throw TooLargeError(maxBytes);
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
    /// The response body stream. A connection drop that only surfaces once
    /// the body is touched (<see cref="IsBodyInterrupted"/>) becomes
    /// <see cref="GitHubErrorKind.Network"/> so the caller falls back to its
    /// cache; a cancelled <paramref name="ct"/> is rethrown as is.
    /// </summary>
    private static async Task<Stream> OpenBodyStreamAsync(
        HttpContent content, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex) when (IsBodyInterrupted(ex, ct))
        {
            throw InterruptedDownload(request);
        }
    }

    /// <summary>
    /// One body read with the same mapping as
    /// <see cref="OpenBodyStreamAsync"/>. Only reads are mapped — destination
    /// write failures and the size limit stay the caller's to propagate.
    /// </summary>
    private static async Task<int> ReadChunkAsync(
        Stream source, Memory<byte> buffer, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(buffer, ct);
        }
        catch (Exception ex) when (IsBodyInterrupted(ex, ct))
        {
            throw InterruptedDownload(request);
        }
    }

    /// <summary>
    /// A body read killed the download: a transport or protocol error, or an
    /// HttpClient timeout mid-body (an <see cref="OperationCanceledException"/>
    /// while <paramref name="ct"/> is still live). A cancellation the caller
    /// asked for is not — that one is rethrown as is.
    /// </summary>
    private static bool IsBodyInterrupted(Exception ex, CancellationToken ct) =>
        ex is IOException or HttpRequestException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static GitHubApiException InterruptedDownload(HttpRequestMessage request) =>
        new(GitHubErrorKind.Network, $"GitHub {Describe(request)} download was interrupted");

    /// <summary>
    /// Commits one file straight to a branch:
    /// <c>PUT /repos/{owner}/{repo}/contents/{path}</c> with the content
    /// base64-encoded and <paramref name="baseBlobSha"/> — the blob SHA of the
    /// file as it was loaded (see <see cref="GitBlobSha.Compute"/>) — as the
    /// <c>sha</c> being replaced. The branch goes in the JSON body, the path is
    /// escaped segment by segment.
    ///
    /// <para>
    /// When the file on the branch no longer matches <paramref name="baseBlobSha"/>
    /// GitHub answers 409, which arrives as
    /// <see cref="GitHubErrorKind.Conflict"/> — nothing was written. A missing
    /// <c>commit.sha</c> or <c>content.sha</c> in a 200 is
    /// <see cref="GitHubErrorKind.Unexpected"/>. Returned SHAs are lower-case.
    /// </para>
    /// </summary>
    /// <param name="message">Commit message; must not be blank.</param>
    /// <param name="path">File path in the repo, e.g. <c>config/glove80.keymap</c>.</param>
    public async Task<GitHubCommitResult> PutFileAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        string path,
        byte[] content,
        string baseBlobSha,
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Commit message is required", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path is required", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(baseBlobSha))
        {
            throw new ArgumentException("Base blob SHA is required", nameof(baseBlobSha));
        }

        var body = new JsonObject
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(content),
            ["sha"] = baseBlobSha,
            ["branch"] = branch,
        };

        var requestPath = $"{RepoPath(owner, repo)}/contents/{EscapePathSegments(path)}";
        using var request = ApiRequest(
            requestPath, accessToken, method: HttpMethod.Put, content: JsonContent.Create(body));

        var json = await SendForJsonAsync(request, ct);

        var commit = GetObject(json, "commit");
        var commitSha = GetString(commit, "sha");
        var blobSha = GetString(GetObject(json, "content"), "sha");
        if (commitSha is null || blobSha is null)
        {
            throw UnexpectedError($"GitHub {Describe(request)} returned an incomplete commit");
        }

        // The first parent when there is one; a brand-new branch has none.
        var parentSha = GetString(GetArray(commit, "parents").FirstOrDefault(), "sha");

        return new GitHubCommitResult(
            commitSha.ToLowerInvariant(),
            blobSha.ToLowerInvariant(),
            parentSha?.ToLowerInvariant());
    }

    /// <summary>
    /// The current blob SHA of a file:
    /// <c>GET /repos/{owner}/{repo}/contents/{path}?ref={branch}</c> — the
    /// value a later "overwrite anyway" commits with. The path is escaped
    /// segment by segment, the whole branch name url-escaped into the query.
    /// A 404 is <see cref="GitHubErrorKind.NotFound"/>; an answer that is not
    /// an object, reports a <c>type</c> other than <c>file</c>, or has no
    /// <c>sha</c> is <see cref="GitHubErrorKind.Unexpected"/>. Returned
    /// lower-case.
    /// </summary>
    public async Task<string> GetFileShaAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path is required", nameof(path));
        }

        var requestPath =
            $"{RepoPath(owner, repo)}/contents/{EscapePathSegments(path)}?ref={Uri.EscapeDataString(branch)}";
        using var request = ApiRequest(requestPath, accessToken);

        var json = await SendForJsonAsync(request, ct);

        var sha = json.ValueKind == JsonValueKind.Object
            && GetString(json, "type") == "file"
            ? GetString(json, "sha")
            : null;

        return sha?.ToLowerInvariant()
            ?? throw UnexpectedError($"GitHub {Describe(request)} returned no file sha");
    }
}
