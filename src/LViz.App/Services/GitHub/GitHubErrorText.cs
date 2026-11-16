using LViz.App.Localization;
using LViz.Core.GitHub;

namespace LViz.App.Services.GitHub;

/// <summary>
/// The translated UI text for <see cref="GitHubApiException"/> kinds, owned
/// by no window: every view-model that shows a failed GitHub call maps it
/// through here.
/// </summary>
public static class GitHubErrorText
{
    /// <summary>
    /// Translated text for a failed GitHub call. Exception messages are
    /// English diagnostics-log material; only Unexpected surfaces its
    /// message, where nothing else describes the failure.
    /// </summary>
    public static string For(GitHubApiException e) => e.Kind switch
    {
        GitHubErrorKind.Network => Loc.Instance["GitHub_ErrorNetwork"],
        GitHubErrorKind.Unauthorized => Loc.Instance["GitHub_SignInAgain"],
        GitHubErrorKind.RateLimited => Loc.Instance["GitHub_ErrorRateLimited"],
        GitHubErrorKind.DeviceCodeExpired => Loc.Instance["GitHub_ErrorCodeExpired"],
        GitHubErrorKind.AccessDenied => Loc.Instance["GitHub_ErrorAccessDenied"],
        GitHubErrorKind.NotFound => Loc.Instance["GitHub_ErrorNotFound"],
        GitHubErrorKind.TooLarge => Loc.Instance["GitHub_ErrorTooLarge"],
        GitHubErrorKind.Conflict => Loc.Instance["GitHub_ErrorConflict"],
        _ => Loc.Instance.Format("GitHub_ErrorUnexpectedFormat", e.Message),
    };
}
