using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using Velopack;

namespace LViz.App.ViewModels;

/// <summary>
/// Version, updates and command-line-tool sections of the Settings window.
/// Velopack (via <see cref="UpdateService"/>) does the actual check / download /
/// apply; this VM is the status surface. The auto-check toggle persists through
/// the load, mutate, save chain on every flip. The CLI section surfaces
/// <see cref="ICliToolInstaller"/>: a button that symlinks <c>lviz</c> onto PATH
/// so scripts can drive the running app. It is hidden in dev builds and on
/// Windows, where the installer or the shim owns PATH.
/// </summary>
public sealed partial class AboutSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly UpdateService _updateService;
    private readonly ICliToolInstaller _cliToolInstaller;
    private UpdateInfo? _pendingUpdate;
    private bool _disposed;

    public AboutSettingsViewModel(
        ISettingsService settingsService,
        UpdateService updateService,
        ICliToolInstaller cliToolInstaller)
    {
        _settingsService = settingsService;
        _updateService = updateService;
        _cliToolInstaller = cliToolInstaller;

        // Seed the auto-check toggle from disk so the UI reflects the persisted
        // value on each Settings open. Setter persists via _settingsService below.
        var loaded = _settingsService.Load();
        _autoCheckForUpdates = loaded.AutoCheckForUpdates;
        _lastUpdateCheckUtc = loaded.LastUpdateCheckUtc;

        _updateService.UpdateAvailable += OnUpdateAvailable;
        _updateService.DownloadProgress += OnDownloadProgress;
        _updateService.UpdateReadyToRestart += OnUpdateReadyToRestart;

        RefreshCliToolStatus();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCliToolInstalled))]
    [NotifyPropertyChangedFor(nameof(IsCliToolActionAvailable))]
    private CliToolState _cliToolState = CliToolState.Unsupported;

    [ObservableProperty]
    private string _cliToolDetail = "";

    /// <summary>Whether the CLI install/remove controls apply on this build (false in dev / on Windows).</summary>
    public bool IsCliToolActionAvailable => CliToolState != CliToolState.Unsupported;

    public bool IsCliToolInstalled => CliToolState == CliToolState.Installed;

    private void RefreshCliToolStatus()
    {
        var status = _cliToolInstaller.GetStatus();
        CliToolState = status.State;
        CliToolDetail = status.Detail;
    }

    [RelayCommand]
    private void InstallCliTool()
    {
        var result = _cliToolInstaller.Install();
        RefreshCliToolStatus();
        // On success show the fresh status ("Installed: …"); on failure keep the
        // error (with its sudo hint) over the re-read status line.
        if (!result.Success) CliToolDetail = result.Message;
    }

    [RelayCommand]
    private void UninstallCliTool()
    {
        var result = _cliToolInstaller.Uninstall();
        RefreshCliToolStatus();
        if (!result.Success) CliToolDetail = result.Message;
    }

    // ── Version & Updates ────────────────────────────────────────────────
    // Velopack handles the actual check/download/apply; this VM is the
    // status surface for the Settings UI. The auto-check toggle persists
    // through the load→mutate→save chain on every flip.

    /// <summary>"vX.Y.Z" derived from the assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/>. Strips the
    /// <c>+commitHash</c> suffix .NET source-link adds, so a CI build
    /// stamped <c>0.1.0+abc1234</c> renders as <c>v0.1.0</c>.</summary>
    public string AppVersion { get; } = ComputeAppVersion();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    private string _updateStatusMessage = "";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedDisplay))]
    private DateTime? _lastUpdateCheckUtc;

    [ObservableProperty]
    private bool _canRestartToInstall;

    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatusMessage);

    /// <summary>"Last checked: 2026-05-26 14:32" or empty when never. Bound
    /// directly on the Updates row.</summary>
    public string LastCheckedDisplay =>
        LastUpdateCheckUtc is { } utc
            ? Loc.Instance.Format("Settings_LastChecked", utc.ToLocalTime().ToString("g"))
            : "";

    [ObservableProperty]
    private bool _autoCheckForUpdates;

    partial void OnAutoCheckForUpdatesChanged(bool value) =>
        _settingsService.Update(s => s with { AutoCheckForUpdates = value }, "Settings");

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusMessage = Loc.Instance["Settings_Checking"];
        try
        {
            var info = await _updateService.CheckForUpdatesAsync();
            LastUpdateCheckUtc = DateTime.UtcNow;
            if (info is null)
            {
                UpdateStatusMessage = Loc.Instance["Settings_UpToDate"];
            }
            // If info is non-null, OnUpdateAvailable already set the status
            // and started the download.
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartToInstall))]
    private void RestartToInstall()
    {
        if (_pendingUpdate is null) return;
        _updateService.ApplyAndRestart(_pendingUpdate);
    }

    private void OnUpdateAvailable(UpdateInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _pendingUpdate = info;
            UpdateStatusMessage = Loc.Instance.Format(
                "Settings_UpdateAvailable", info.TargetFullRelease.Version.ToString());
            LastUpdateCheckUtc = DateTime.UtcNow;
            // Kick off the download immediately; user sees progress and a
            // "restart to install" affordance once it's staged.
            _ = _updateService.DownloadAndApplyAsync(info);
        });
    }

    private void OnDownloadProgress(int percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusMessage = Loc.Instance.Format("Settings_Downloading", percent);
        });
    }

    private void OnUpdateReadyToRestart(UpdateInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _pendingUpdate = info;
            CanRestartToInstall = true;
            UpdateStatusMessage = Loc.Instance.Format(
                "Settings_UpdateReady", info.TargetFullRelease.Version.ToString());
            RestartToInstallCommand.NotifyCanExecuteChanged();
        });
    }

    private static string ComputeAppVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is not null)
        {
            var plus = info.IndexOf('+');
            return "v" + (plus >= 0 ? info[..plus] : info);
        }
        return "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateService.UpdateAvailable -= OnUpdateAvailable;
        _updateService.DownloadProgress -= OnDownloadProgress;
        _updateService.UpdateReadyToRestart -= OnUpdateReadyToRestart;
    }
}
