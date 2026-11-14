using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using LViz.App.Services.Flashing;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using LViz.Tests.GitHub;
using Xunit;

namespace LViz.Tests.Flashing;

/// <summary>
/// <see cref="FirmwareBuildService"/> — the builds login, finding a build by
/// commit or branch, downloading it into the cache, and the peripheral
/// advice. Runs on real <see cref="GitHubClient"/>s over
/// <see cref="FakeHttpHandler"/> route tables (one per login, so a test can
/// see which token went where), real <see cref="GitHubSession"/>s on one
/// <see cref="InMemorySecretStore"/> holding non-expiring tokens, and a
/// temp-folder <see cref="FirmwareCache"/> — nothing touches the network or a
/// keychain. "Signed in" means valid tokens written into the store under
/// both apps' secret keys before the service call.
/// </summary>
public sealed class FirmwareBuildServiceTests : IDisposable
{
    private const string BuildsClientId = "Iv-buildstest";
    private const string MainClientId = "Iv-maintest";
    private const string UserAgent = "LViz-tests/1.0";
    private const string Login = "octocat";
    private const string BuildsToken = "ghu_buildstoken";
    private const string MainToken = "ghu_maintoken";

    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config-cosmos82";
    private const string RepoRoot = "/repos/ovandongen/zmk-config-cosmos82";

    private const string Sha = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";
    private const string OlderSha = "bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00";

    private const string RunsByShaPath = RepoRoot + "/actions/runs?head_sha=" + Sha + "&per_page=20";
    private const string RunsByBranchPath = RepoRoot + "/actions/runs?branch=main&per_page=20";
    private const string ZipPath = RepoRoot + "/actions/artifacts/7/zip";

    private readonly FakeHttpHandler _builds = new();
    private readonly FakeHttpHandler _main = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _buildsRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<HttpResponseMessage>> _mainRoutes = new(StringComparer.Ordinal);
    private readonly InMemorySecretStore _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lviz-build-service-{Guid.NewGuid():N}");
    private readonly FirmwareCache _cache;
    private readonly FirmwareBuildService _service;

    public FirmwareBuildServiceTests()
    {
        Directory.CreateDirectory(_root);
        _cache = new FirmwareCache(_root);

        var buildsClient = new GitHubClient(new HttpClient(_builds), BuildsClientId, UserAgent);
        var mainClient = new GitHubClient(new HttpClient(_main), MainClientId, UserAgent);
        _builds.Respond = Routing(_buildsRoutes);
        _main.Respond = Routing(_mainRoutes);

        _service = new FirmwareBuildService(
            buildsClient,
            new GitHubSession(buildsClient, _store, secretKey: GitHubBuildsAppInfo.SecretKey),
            mainClient,
            new GitHubSession(mainClient, _store),
            _cache,
            static (_, _) => Task.CompletedTask);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort: a leaked temp folder is not a test failure.
        }
    }

    // ---- FindBuildAsync by sha ----

    [Fact]
    public async Task FindBuildBySha_SucceededRunWithFirmware_IsReadyWithArtifactId()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => FakeHttpHandler.Json(RunsJson(
            Run(101, "completed", "success", "2026-11-09T10:15:30Z"))));
        RouteBuilds(ArtifactsPath(101), () => FakeHttpHandler.Json(
            ArtifactsJson((4001, "firmware", Expired: false))));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.Ready, build.State);
        Assert.Equal(101, build.Run?.Id);
        Assert.Equal(4001, build.ArtifactId);
        Assert.False(build.NewerRunPending);
    }

    [Fact]
    public async Task FindBuildBySha_ExpiredFirmwareArtifact_IsNoFirmware()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => FakeHttpHandler.Json(RunsJson(
            Run(101, "completed", "success", "2026-11-09T10:15:30Z"))));
        RouteBuilds(ArtifactsPath(101), () => FakeHttpHandler.Json(
            ArtifactsJson((4001, "firmware", Expired: true))));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.NoFirmware, build.State);
        Assert.Equal(101, build.Run?.Id);
        Assert.Null(build.ArtifactId);
    }

    [Fact]
    public async Task FindBuildBySha_InProgressRun_IsRunning()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => FakeHttpHandler.Json(RunsJson(
            Run(102, "in_progress", null, "2026-11-09T11:00:00Z"))));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.Running, build.State);
        Assert.Equal(102, build.Run?.Id);
        Assert.Null(build.ArtifactId);

        // A run that is still going has no artifact list to ask for.
        Assert.Single(_builds.Requests);
    }

    [Fact]
    public async Task FindBuildBySha_CompletedFailure_IsFailed()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => FakeHttpHandler.Json(RunsJson(
            Run(103, "completed", "failure", "2026-11-09T11:00:00Z"))));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.Failed, build.State);
        Assert.Equal(103, build.Run?.Id);
    }

    [Fact]
    public async Task FindBuildBySha_NoRuns_IsNoRun()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => FakeHttpHandler.Json(RunsJson()));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.NoRun, build.State);
        Assert.Null(build.Run);
        Assert.Null(build.ArtifactId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task FindBuild_WithoutExactlyOneOfShaAndBranch_Throws(bool useSha, bool useBranch) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _service.FindBuildAsync(
            Owner, Repo, useSha ? Sha : null, useBranch ? "main" : null));

    // ---- FindBuildAsync by branch ----

    [Fact]
    public async Task FindBuildByBranch_NewerRunStillGoing_IsReadyOnOlderRunWithPendingNewer()
    {
        await SignInAsync();
        RouteBuilds(RunsByBranchPath, () => FakeHttpHandler.Json(RunsJson(
            Run(202, "in_progress", null, "2026-11-09T12:00:00Z"),
            Run(201, "completed", "success", "2026-11-09T10:00:00Z"))));
        RouteBuilds(ArtifactsPath(201), () => FakeHttpHandler.Json(
            ArtifactsJson((4101, "firmware", Expired: false))));

        var build = await _service.FindBuildAsync(Owner, Repo, null, "main");

        Assert.Equal(FirmwareBuildState.Ready, build.State);
        Assert.Equal(201, build.Run?.Id);
        Assert.Equal(4101, build.ArtifactId);
        Assert.True(build.NewerRunPending);
    }

    [Fact]
    public async Task FindBuildByBranch_NewestRunIsTheReadyOne_HasNoPendingNewer()
    {
        await SignInAsync();
        RouteBuilds(RunsByBranchPath, () => FakeHttpHandler.Json(RunsJson(
            Run(201, "completed", "success", "2026-11-09T10:00:00Z"))));
        RouteBuilds(ArtifactsPath(201), () => FakeHttpHandler.Json(
            ArtifactsJson((4101, "firmware", Expired: false))));

        var build = await _service.FindBuildAsync(Owner, Repo, null, "main");

        Assert.Equal(FirmwareBuildState.Ready, build.State);
        Assert.False(build.NewerRunPending);
    }

    // ---- not installed ----

    [Fact]
    public async Task FindBuild_RunsAnswer404_IsNotInstalled()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.NotInstalled, build.State);
        Assert.Null(build.Run);
    }

    [Fact]
    public async Task FindBuild_RunsAnswerPlain403_IsNotInstalled()
    {
        await SignInAsync();
        RouteBuilds(RunsByShaPath, () => new HttpResponseMessage((HttpStatusCode)403));

        var build = await _service.FindBuildAsync(Owner, Repo, Sha, null);

        Assert.Equal(FirmwareBuildState.NotInstalled, build.State);
    }

    // ---- DownloadAsync ----

    [Fact]
    public async Task Download_Fresh_DownloadsOnceStoresAndSecondCallSkipsTheRequest()
    {
        await SignInAsync();
        RouteBuilds(ZipPath, () => ZipResponse(("firmware/cosmos82-zmk.uf2", Uf2Block("ZMK"))));

        var first = await _service.DownloadAsync(Owner, Repo, Sha, 7);

        var done = Assert.IsType<FirmwareDownloadResult.Done>(first);
        Assert.Single(done.Files);
        Assert.EndsWith("cosmos82-zmk.uf2", done.Files[0]);
        Assert.True(File.Exists(done.Files[0]));
        Assert.Contains(_builds.Requests,
            entry => entry.Request.RequestUri!.PathAndQuery == ZipPath);

        var second = await _service.DownloadAsync(Owner, Repo, Sha, 7);

        var cached = Assert.IsType<FirmwareDownloadResult.Done>(second);
        Assert.Equal(done.Files, cached.Files);
        // Still exactly the one zip request — the cache answered the second.
        Assert.Single(_builds.Requests);
    }

    [Fact]
    public async Task Download_ArtifactGone410_IsExpired()
    {
        await SignInAsync();
        RouteBuilds(ZipPath, () => new HttpResponseMessage((HttpStatusCode)410));

        var result = await _service.DownloadAsync(Owner, Repo, Sha, 7);

        Assert.IsType<FirmwareDownloadResult.Expired>(result);
    }

    [Fact]
    public async Task Download_ZipWithoutUf2_FailedWithTheStoresReason()
    {
        await SignInAsync();
        RouteBuilds(ZipPath, () => ZipResponse(("README.txt", Encoding.ASCII.GetBytes("docs"))));

        var result = await _service.DownloadAsync(Owner, Repo, Sha, 7);

        var failed = Assert.IsType<FirmwareDownloadResult.Failed>(result);
        Assert.Equal("the artifact has no .uf2 files", failed.Message);
    }

    // ---- AdvisePeripheralAsync ----

    [Fact]
    public async Task AdvisePeripheral_WithoutLastFlashedSha_IsUnknown_WithoutRequest()
    {
        await SignInAsync();

        var advice = await _service.AdvisePeripheralAsync(Owner, Repo, null, Sha);

        Assert.Equal(PeripheralAdvice.Unknown, advice);
        Assert.Empty(_main.Requests);
        Assert.Empty(_builds.Requests);
    }

    [Fact]
    public async Task AdvisePeripheral_SameShaIgnoringCase_IsNotNeeded_WithoutRequest()
    {
        await SignInAsync();

        var advice = await _service.AdvisePeripheralAsync(Owner, Repo, Sha.ToUpperInvariant(), Sha);

        Assert.Equal(PeripheralAdvice.NotNeeded, advice);
        Assert.Empty(_main.Requests);
    }

    [Fact]
    public async Task AdvisePeripheral_OnlyKeymapChanged_IsNotNeeded()
    {
        await SignInAsync();
        RouteMain(ComparePath(OlderSha, Sha), () => FakeHttpHandler.Json(CompareJson("config/x.keymap")));

        var advice = await _service.AdvisePeripheralAsync(Owner, Repo, OlderSha, Sha);

        Assert.Equal(PeripheralAdvice.NotNeeded, advice);
    }

    [Fact]
    public async Task AdvisePeripheral_WestYmlChanged_IsNeeded()
    {
        await SignInAsync();
        RouteMain(ComparePath(OlderSha, Sha), () => FakeHttpHandler.Json(CompareJson("config/west.yml")));

        var advice = await _service.AdvisePeripheralAsync(Owner, Repo, OlderSha, Sha);

        Assert.Equal(PeripheralAdvice.Needed, advice);
    }

    [Fact]
    public async Task AdvisePeripheral_Compare404_IsUnknown()
    {
        await SignInAsync();
        RouteMain(ComparePath(OlderSha, Sha), () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var advice = await _service.AdvisePeripheralAsync(Owner, Repo, OlderSha, Sha);

        Assert.Equal(PeripheralAdvice.Unknown, advice);
    }

    [Fact]
    public async Task AdvisePeripheral_ComparesWithTheMainToken_NotTheBuildsToken()
    {
        await SignInAsync();
        RouteMain(ComparePath(OlderSha, Sha), () => FakeHttpHandler.Json(CompareJson("config/west.yml")));

        await _service.AdvisePeripheralAsync(Owner, Repo, OlderSha, Sha);

        var (request, _) = Assert.Single(_main.Requests);
        var auth = request.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal(MainToken, auth.Parameter);
    }

    // ---- sign out ----

    [Fact]
    public async Task SignOut_ClearsTheBuildsLoginAndWipesCachedBuilds()
    {
        await SignInAsync();
        using (var zip = ZipWithUf2())
            _cache.Store(Owner, Repo, Sha, zip);
        Assert.NotNull(_cache.TryGet(Owner, Repo, Sha));

        await _service.SignOutAsync();

        Assert.False(_service.IsSignedIn);
        Assert.Null(_service.Login);
        Assert.Null(await _store.ReadAsync(GitHubBuildsAppInfo.SecretKey));
        Assert.Null(_cache.TryGet(Owner, Repo, Sha));
        Assert.False(Directory.Exists(Path.Combine(_root, Owner)));
    }

    // ---- helpers ----

    /// <summary>Answers every path from a route table; unrouted paths answer 404.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Routing(
        Dictionary<string, Func<HttpResponseMessage>> routes) =>
        request => routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
            ? respond()
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>
    /// Both logins live in the store with non-expiring access tokens, as a
    /// restored app would find them.
    /// </summary>
    private Task SignInAsync() => Task.WhenAll(
        _store.WriteAsync(GitHubBuildsAppInfo.SecretKey,
            new GitHubTokens(BuildsToken, null, "ghr_builds", null, Login).ToJson()),
        _store.WriteAsync(GitHubAppInfo.SecretKey,
            new GitHubTokens(MainToken, null, "ghr_main", null, Login).ToJson()));

    private void RouteBuilds(string pathAndQuery, Func<HttpResponseMessage> respond) =>
        _buildsRoutes[pathAndQuery] = respond;

    private void RouteMain(string pathAndQuery, Func<HttpResponseMessage> respond) =>
        _mainRoutes[pathAndQuery] = respond;

    private static string ArtifactsPath(long runId) => $"{RepoRoot}/actions/runs/{runId}/artifacts";

    private static string ComparePath(string baseSha, string headSha) =>
        $"{RepoRoot}/compare/{baseSha}...{headSha}";

    /// <summary>One workflow run as the API writes it; a null conclusion is JSON null.</summary>
    private static string Run(long id, string status, string? conclusion, string createdAt) =>
        "{\"id\":" + id
        + ",\"name\":\"Build firmware\""
        + ",\"head_branch\":\"main\""
        + ",\"head_sha\":\"" + Sha + "\""
        + ",\"status\":\"" + status + "\""
        + ",\"conclusion\":" + (conclusion is null ? "null" : "\"" + conclusion + "\"")
        + ",\"created_at\":\"" + createdAt + "\"}";

    private static string RunsJson(params string[] runs) =>
        "{\"workflow_runs\":[" + string.Join(",", runs) + "]}";

    private static string ArtifactsJson(params (long Id, string Name, bool Expired)[] artifacts) =>
        "{\"artifacts\":[" + string.Join(",", artifacts.Select(a =>
            "{\"id\":" + a.Id
            + ",\"name\":\"" + a.Name + "\""
            + ",\"size_in_bytes\":4096"
            + ",\"expired\":" + (a.Expired ? "true" : "false") + "}")) + "]}";

    private static string CompareJson(params string[] files) =>
        "{\"files\":[" + string.Join(",", files.Select(f => "{\"filename\":\"" + f + "\"}")) + "]}";

    /// <summary>One valid 512-byte UF2 block carrying the ASCII payload.</summary>
    private static byte[] Uf2Block(string payload)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        var block = new byte[Uf2File.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), Uf2File.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), Uf2File.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), 1); // block count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)bytes.Length);
        bytes.CopyTo(block.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), Uf2File.MagicEnd);
        return block;
    }

    /// <summary>An artifact zip with the given entries, as an HTTP 200 body.</summary>
    private static HttpResponseMessage ZipResponse(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var target = archive.CreateEntry(name).Open();
                target.Write(content);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(stream.ToArray()),
        };
    }

    /// <summary>An in-memory artifact zip with one valid UF2 under a folder.</summary>
    private static MemoryStream ZipWithUf2()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var target = archive.CreateEntry("firmware/cosmos82-zmk.uf2").Open();
            target.Write(Uf2Block("ZMK"));
        }

        stream.Position = 0;
        return stream;
    }
}
