namespace LViz.Core.GitHub;

/// <summary>
/// Identity of the second LViz GitHub App, used to read workflow runs and
/// download firmware builds; it is asked for only when the user flashes from
/// LViz. The GitHub device-flow sign-in and repository installation work the
/// same way as for <see cref="GitHubAppInfo"/>. Static registration data —
/// the <c>ClientId</c> is public by design (the device flow embeds it in
/// URLs).
/// </summary>
public static class GitHubBuildsAppInfo
{
    /// <summary>OAuth app client id used for the device flow.</summary>
    public const string ClientId = "Iv23liD6wl5A88yIsI7k";

    /// <summary>App slug as it appears in github.com/apps/&lt;slug&gt;.</summary>
    public const string Slug = "lviz-for-zmk-builds";

    /// <summary>URL that opens the app's repository-installation page.</summary>
    public static string InstallUrl => $"https://github.com/apps/{Slug}/installations/new";

    /// <summary>
    /// <see cref="Persistence.ISecretStore"/> key the serialized
    /// <see cref="GitHubTokens"/> login is stored under, next to (never on
    /// top of) the <see cref="GitHubAppInfo.SecretKey"/> login.
    /// </summary>
    public const string SecretKey = "github-builds";
}
