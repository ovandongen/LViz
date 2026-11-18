using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.Core.GitHub;

namespace LViz.App.ViewModels;

/// <summary>
/// The OAuth device flow of either GitHub App — the keymap App of the
/// "Open from GitHub…" window and the builds App of the flash window —
/// written once: ask GitHub for a code, show it, copy it to the clipboard,
/// open the verification page, and notice the approval, the denial or the
/// expiry on its own. The host owns the error line (it receives translated
/// text through the <c>showError</c> hook) and decides what happens after a
/// sign-in (the <c>onSignedIn</c> hook); this class knows no windows.
/// </summary>
public sealed partial class GitHubSignInViewModel : ObservableObject
{
    private readonly IGitHubLogin _login;
    private readonly IDesktopInteraction _desktop;
    private readonly Action<string?> _showError;
    private readonly Func<Task> _onSignedIn;

    // Replaced wholesale per sign-in attempt, so a Cancel can only ever hit
    // the wait it was meant for and a stale source is never reused.
    private CancellationTokenSource? _signInCts;

    public GitHubSignInViewModel(
        IGitHubLogin login,
        IDesktopInteraction desktop,
        Action<string?> showError,
        Func<Task> onSignedIn)
    {
        _login = login;
        _desktop = desktop;
        _showError = showError;
        _onSignedIn = onSignedIn;
    }

    /// <summary>The short code the user types on github.com; null while no flow runs.</summary>
    [ObservableProperty]
    private string? _userCode;

    /// <summary>The github.com page where the code is entered.</summary>
    [ObservableProperty]
    private string? _verificationUri;

    /// <summary>True while waiting for the user to approve the code on GitHub.</summary>
    [ObservableProperty]
    private bool _isWaitingForSignIn;

    /// <summary>
    /// Runs the whole device flow: ask GitHub for a code, hand it to the
    /// clipboard and the browser, wait for the approval, and then run the
    /// host's next step. <see cref="CancelCommand"/> aborts the wait; a
    /// failed attempt hands the translated text to the host's error line.
    /// Anything but <see cref="GitHubApiException"/> and
    /// <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    [RelayCommand]
    private async Task Start()
    {
        _showError(null);

        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();
        var token = _signInCts.Token;

        GitHubDeviceCode code;
        try
        {
            code = await _login.StartSignInAsync();
        }
        catch (GitHubApiException e)
        {
            _showError(GitHubErrorText.For(e));
            return;
        }

        // A Cancel (the window closed) while the code was being fetched ends
        // the flow here: no clipboard, no browser, no code on screen.
        if (token.IsCancellationRequested) return;

        UserCode = code.UserCode;
        VerificationUri = code.VerificationUri;
        IsWaitingForSignIn = true;

        try
        {
            await _desktop.CopyTextAsync(code.UserCode);
            _desktop.OpenUrl(code.VerificationUri);
            await _login.CompleteSignInAsync(code, token);
        }
        catch (OperationCanceledException)
        {
            IsWaitingForSignIn = false;
            UserCode = null;
            return;
        }
        catch (GitHubApiException e)
        {
            IsWaitingForSignIn = false;
            UserCode = null;
            _showError(GitHubErrorText.For(e));
            return;
        }

        IsWaitingForSignIn = false;
        UserCode = null;
        await _onSignedIn();
    }

    /// <summary>Re-copies the code after the first automatic copy failed or got lost.</summary>
    [RelayCommand]
    private async Task CopyCode()
    {
        if (UserCode is string code)
        {
            await _desktop.CopyTextAsync(code);
        }
    }

    /// <summary>Re-opens the github.com page when the user closed the first one.</summary>
    [RelayCommand]
    private void OpenVerificationPage()
    {
        if (VerificationUri is string uri)
        {
            _desktop.OpenUrl(uri);
        }
    }

    /// <summary>
    /// Aborts the current wait (if one runs) and hides the code. Safe with
    /// no flow running and safe to call more than once; a host calls it
    /// when its window closes.
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        _signInCts?.Cancel();
        IsWaitingForSignIn = false;
        UserCode = null;
    }
}
