namespace LViz.Core.GitHub;

/// <summary>
/// One GitHub Actions workflow run as the Actions API reports it. LViz uses
/// these to find the firmware build of a commit it just pushed to a
/// zmk-config; <see cref="Id"/> feeds
/// <see cref="GitHubClient.ListRunArtifactsAsync"/>.
/// </summary>
/// <param name="Id">Run id, as it appears in the run's URL.</param>
/// <param name="Name">Workflow name, e.g. <c>Build firmware</c>; empty when
/// the API did not say.</param>
/// <param name="HeadBranch">Branch the run was made on; empty when the API
/// did not say.</param>
/// <param name="HeadSha">Commit the run built, lower-case.</param>
/// <param name="Status">Raw <c>status</c>: <c>queued</c>, <c>in_progress</c>,
/// <c>completed</c>, <c>waiting</c>, <c>requested</c> or <c>pending</c>.</param>
/// <param name="Conclusion">Raw <c>conclusion</c> (<c>success</c>,
/// <c>failure</c>, <c>cancelled</c>, …), null while the run has not concluded.</param>
/// <param name="HtmlUrl">The run's page on github.com; empty when the API did
/// not say.</param>
/// <param name="CreatedAt">When the run was created;
/// <see cref="DateTimeOffset.MinValue"/> when the API gave no parsable time.</param>
/// <param name="CommitMessage">Message of the commit the run built, null when
/// the answer carried none.</param>
public sealed record GitHubWorkflowRun(
    long Id,
    string Name,
    string HeadBranch,
    string HeadSha,
    string Status,
    string? Conclusion,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    string? CommitMessage)
{
    /// <summary>Whether the run has finished, whatever its outcome.</summary>
    public bool IsCompleted => Status == "completed";

    /// <summary>Whether the run finished and concluded <c>success</c>.</summary>
    public bool Succeeded => IsCompleted && Conclusion == "success";
}

/// <summary>
/// One artifact a workflow run uploaded. The stock ZMK workflow uploads one
/// merged artifact named <c>firmware</c> holding every built <c>.uf2</c>.
/// </summary>
/// <param name="Id">Artifact id; feeds
/// <see cref="GitHubClient.DownloadArtifactZipAsync"/>.</param>
/// <param name="Name">Artifact name as uploaded, e.g. <c>firmware</c>.</param>
/// <param name="SizeInBytes">Size GitHub reports; 0 when it did not say.</param>
/// <param name="Expired">Whether GitHub has already deleted the artifact, so
/// downloading it would fail.</param>
public sealed record GitHubArtifact(long Id, string Name, long SizeInBytes, bool Expired);
