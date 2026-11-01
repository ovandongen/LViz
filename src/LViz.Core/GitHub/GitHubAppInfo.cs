namespace LViz.Core.GitHub;

/// <summary>
/// Identity of the LViz GitHub App used for the device-flow sign-in and for
/// installing it on a user's repositories. Static registration data — the
/// <c>ClientId</c> is public by design (the device flow embeds it in URLs).
/// </summary>
public static class GitHubAppInfo
{
    /// <summary>OAuth app client id used for the device flow.</summary>
    public const string ClientId = "Iv23li2p4AppNBG53eHQ";

    /// <summary>App slug as it appears in github.com/apps/&lt;slug&gt;.</summary>
    public const string Slug = "lviz-for-zmk";

    /// <summary>URL that opens the app's repository-installation page.</summary>
    public static string InstallUrl => $"https://github.com/apps/{Slug}/installations/new";

    /// <summary>
    /// <see cref="Persistence.ISecretStore"/> key the serialized
    /// <see cref="GitHubTokens"/> login is stored under.
    /// </summary>
    public const string SecretKey = "github";
}
