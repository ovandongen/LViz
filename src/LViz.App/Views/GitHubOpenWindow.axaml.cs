using Avalonia.Controls;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal "Open from GitHub…" window: sign in (OAuth device flow), pick a
/// repository and branch, and choose a keymap when the repo holds several.
/// Its <c>DataContext</c> must be a <see cref="GitHubOpenViewModel"/>; the
/// VM closes the dialog by raising <see cref="GitHubOpenViewModel.CloseRequested"/>
/// (null = cancel, a <see cref="GitHubOpenSelection"/> = chosen keymap),
/// which this code-behind turns into <c>Close(result)</c> — so
/// <c>ShowDialog</c> returns exactly that. The window supplies the clipboard
/// hook and starts the VM's restore on <see cref="Window.Opened"/>;
/// closing the window via its chrome runs the VM's Cancel so a pending
/// sign-in wait does not outlive the window. The browser hook
/// (<c>OpenUrl</c>) is wired by App.axaml.cs, not here.
/// </summary>
public partial class GitHubOpenWindow : Window
{
    private GitHubOpenViewModel? _boundVm;

    // Set as soon as the close is VM-driven (CloseRequested → Close) or the
    // chrome close has handed off to Cancel — the re-entrant Closing that
    // follows must not cancel (or close) a second time.
    private bool _closeHandled;

    public GitHubOpenWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
        Closing += OnWindowClosing;
        Opened += (_, _) =>
        {
            if (_boundVm is { } vm)
                _ = vm.InitializeAsync();
        };
    }

    private void BindViewModel()
    {
        if (_boundVm is { } old)
        {
            old.CloseRequested -= OnCloseRequested;
            _boundVm = null;
        }

        if (DataContext is not GitHubOpenViewModel vm)
            return;

        vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        vm.CloseRequested += OnCloseRequested;
        _boundVm = vm;
    }

    private void OnCloseRequested(GitHubOpenSelection? result)
    {
        _closeHandled = true;
        Close(result);
    }

    /// <summary>
    /// Window X / Escape: the VM's Cancel aborts a pending device-flow wait
    /// and raises <c>CloseRequested(null)</c>, which performs the real
    /// close. Like the main window's handler, this one cancels its own
    /// event and lets the nested Close finish; <see cref="_closeHandled"/>
    /// keeps the re-entrant path from looping.
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeHandled || _boundVm is null)
            return;

        _closeHandled = true;
        e.Cancel = true;
        _boundVm.CancelCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_boundVm is { } vm)
            vm.CloseRequested -= OnCloseRequested;
        _boundVm = null;
        base.OnClosed(e);
    }
}
