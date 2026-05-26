using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace LViz.App.Services;

/// <summary>
/// In-app auto-update facade over <see cref="UpdateManager"/>. Singleton —
/// owns one update-source instance for the process lifetime. Consumers
/// (SettingsViewModel, App.axaml.cs background poke) call
/// <see cref="CheckForUpdatesAsync"/> / <see cref="DownloadAndApplyAsync"/>
/// and listen on the three events for progress UI.
/// </summary>
public sealed class UpdateService
{
    // Update source URL — leave wired up before the repo is public; check
    // calls will 404 silently until release assets exist.
    private const string GitHubRepoUrl = "https://github.com/ovandongen/LViz";

    private readonly ISettingsService _settingsService;
    private readonly Lazy<UpdateManager?> _updateManager;

    public UpdateService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        // Lazy: UpdateManager's ctor calls VelopackLocator.Current, which
        // throws when VelopackApp.Build() hasn't run (unit tests, etc.).
        // Defer to first use and degrade to no-op when locator is missing.
        _updateManager = new Lazy<UpdateManager?>(() =>
        {
            try
            {
                var source = new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false);
                return new UpdateManager(source);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("Update", $"UpdateManager unavailable: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>True only when running as a Velopack-installed app — false for
    /// <c>dotnet run</c>, raw self-contained-publish runs, etc. Check/download
    /// no-op when false so dev runs don't surface scary "update failed" UI.</summary>
    public bool IsInstalled => _updateManager.Value?.IsInstalled ?? false;

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<int>? DownloadProgress;
    public event Action<UpdateInfo>? UpdateReadyToRestart;

    /// <summary>
    /// Checks the update source for a newer release. Returns the
    /// <see cref="UpdateInfo"/> on hit, null on no-update / not-installed /
    /// network failure. Always stamps <see cref="UserSettings.LastUpdateCheckUtc"/>
    /// regardless of outcome so the Settings UI can show "Last checked: …".
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var mgr = _updateManager.Value;
            if (mgr is null || !mgr.IsInstalled)
            {
                DiagnosticLog.Info("Update", "Skipping check — not running as a Velopack install.");
                return null;
            }

            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            PersistLastChecked();

            if (info is not null)
            {
                DiagnosticLog.Info("Update", $"Update available: {info.TargetFullRelease.Version}");
                UpdateAvailable?.Invoke(info);
            }
            return info;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Update", $"Check failed: {ex.Message}");
            PersistLastChecked();
            return null;
        }
    }

    /// <summary>
    /// Downloads the staged update in the background, applies on next restart.
    /// Caller is responsible for first showing the user the "available"
    /// notification — this method commits to the install.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var mgr = _updateManager.Value;
        if (mgr is null) return;
        try
        {
            await mgr.DownloadUpdatesAsync(info, p => DownloadProgress?.Invoke(p), cancelToken: ct).ConfigureAwait(false);
            DiagnosticLog.Info("Update", $"Update {info.TargetFullRelease.Version} downloaded; will apply on restart.");
            UpdateReadyToRestart?.Invoke(info);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Update", $"Download failed: {ex.Message}");
        }
    }

    /// <summary>Applies the staged update and restarts the app. Call from a
    /// UI affordance after <see cref="UpdateReadyToRestart"/> fires.</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        var mgr = _updateManager.Value;
        if (mgr is null) return;
        try
        {
            mgr.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Update", $"Apply/restart failed: {ex.Message}");
        }
    }

    private void PersistLastChecked()
    {
        try
        {
            var s = _settingsService.Load();
            _settingsService.Save(s with { LastUpdateCheckUtc = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Update", $"Persist LastUpdateCheckUtc failed: {ex.Message}");
        }
    }
}
