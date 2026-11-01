namespace LViz.Core.GitHub;

/// <summary>
/// What went wrong in a GitHub call. The App maps these to translated text;
/// the exception <see cref="System.Exception.Message"/> is English and for the
/// diagnostics log only.
/// </summary>
public enum GitHubErrorKind
{
    /// <summary>No answer from GitHub: DNS, TLS, connection reset or timeout.</summary>
    Network,

    /// <summary>HTTP 401, or a session that cannot be refreshed.</summary>
    Unauthorized,

    /// <summary>HTTP 404 — repo, branch, file or user does not exist (or is not visible).</summary>
    NotFound,

    /// <summary>Rate limited; retry after the response's <c>retry-after</c>.</summary>
    RateLimited,

    /// <summary>The device code expired before the user authorized it.</summary>
    DeviceCodeExpired,

    /// <summary>The user denied the sign-in on github.com.</summary>
    AccessDenied,

    /// <summary>A download exceeded the configured size limit.</summary>
    TooLarge,

    /// <summary>Anything unexpected: other status codes, bad or incomplete JSON.</summary>
    Unexpected,
}

/// <summary>
/// One failed GitHub call. Carries the <see cref="Kind"/> for the UI and the
/// HTTP status code when the failure came from an answer (null for transport
/// failures and sign-in-flow rejections, which arrive as HTTP 200).
///
/// <para>
/// Messages never contain a query string, a request body or a token — only the
/// HTTP method and the URL path.
/// </para>
/// </summary>
public sealed class GitHubApiException(GitHubErrorKind kind, string message, int? statusCode = null)
    : Exception(message)
{
    /// <summary>What kind of failure this is; drives the translated UI text.</summary>
    public GitHubErrorKind Kind { get; } = kind;

    /// <summary>HTTP status code, or null when there was no HTTP answer.</summary>
    public int? StatusCode { get; } = statusCode;
}
