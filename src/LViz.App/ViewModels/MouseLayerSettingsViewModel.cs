using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Mouse-layer tab of the Settings window. Reads/writes
/// <see cref="MainWindowViewModel.MouseLayer"/> (the engine) directly. Setters
/// apply immediately (no commit-on-close), the same pattern the auto-switch
/// fallback uses, since the underlying state is a small self-contained record.
/// When the engine is absent (tests), the snapshot falls back to the persisted
/// per-profile settings.
/// </summary>
public sealed partial class MouseLayerSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly MainWindowViewModel _main;

    public MouseLayerSettingsViewModel(ISettingsService settingsService, MainWindowViewModel main)
    {
        _settingsService = settingsService;
        _main = main;
        Reload();
    }

    private MouseLayerSettings _snapshot = new();

    /// <summary>Re-reads the active keyboard's settings (called on keyboard switch).</summary>
    internal void Reload()
    {
        _snapshot = _main.MouseLayer?.CurrentSettings ?? LoadPersisted();
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(SelectedLayer));
        OnPropertyChanged(nameof(IdleTimeoutMs));
        OnPropertyChanged(nameof(IsEndlessTimeout));
        OnPropertyChanged(nameof(IsIdleTimeoutEnabled));
        OnPropertyChanged(nameof(IsExitOnTransparentKey));
        OnPropertyChanged(nameof(IsExitOnEmptyKey));
    }

    private void Update(MouseLayerSettings next)
    {
        _snapshot = next;
        _main.MouseLayer?.ApplySettings(next);
    }

    /// <summary>Per-profile mouse-layer settings straight from disk — the
    /// fallback used when no engine is wired up (tests). Mirrors the load the
    /// engine itself does on construction.</summary>
    private MouseLayerSettings LoadPersisted() =>
        _settingsService.Load().MouseLayer
            .GetForProfile(_main.SelectedKeyboard.Id, new MouseLayerSettings());

    public bool IsEnabled
    {
        get => _snapshot.Enabled;
        set
        {
            if (_snapshot.Enabled == value) return;
            Update(_snapshot with { Enabled = value });
            OnPropertyChanged();
        }
    }

    public LayerViewModel? SelectedLayer
    {
        get
        {
            var idx = _snapshot.MouseLayerIndex;
            if (idx is null) return null;
            return _main.LayerTabs.Layers.FirstOrDefault(l => l.Index == idx.Value);
        }
        set
        {
            var newIdx = value?.Index;
            if (_snapshot.MouseLayerIndex == newIdx) return;
            Update(_snapshot with { MouseLayerIndex = newIdx });
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Idle-timeout in ms. Slider clamps to 200–2000 in XAML; we clamp here
    /// too as belt-and-braces for keyboard nudges that bypass the slider.
    /// </summary>
    public int IdleTimeoutMs
    {
        get => _snapshot.IdleTimeoutMs;
        set
        {
            var clamped = Math.Clamp(value, 200, 2000);
            if (_snapshot.IdleTimeoutMs == clamped) return;
            Update(_snapshot with { IdleTimeoutMs = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When true, mouse-layer push has no idle-driven revert: the push stays
    /// in flight until an external exit gesture (transparent / empty-key tap,
    /// profile switch, shutdown) tears it down. Disables the idle-timeout
    /// slider in the UI but doesn't otherwise modify
    /// <see cref="IdleTimeoutMs"/> so the value is preserved if the
    /// user toggles back.
    ///
    /// <para>Endless mode requires at least one of
    /// <see cref="IsExitOnTransparentKey"/> /
    /// <see cref="IsExitOnEmptyKey"/> to be enabled (otherwise the
    /// push could never be released). Turning endless on with neither set
    /// auto-enables transparent-key exit; the per-flag setters refuse to
    /// disable the last enabled flag while endless is on.</para>
    /// </summary>
    public bool IsEndlessTimeout
    {
        get => _snapshot.EndlessTimeout;
        set
        {
            if (_snapshot.EndlessTimeout == value) return;
            var next = _snapshot with { EndlessTimeout = value };
            bool autoEnabledTrans = false;
            if (value && !next.ExitOnTransparentKey && !next.ExitOnEmptyKey)
            {
                next = next with { ExitOnTransparentKey = true };
                autoEnabledTrans = true;
            }
            Update(next);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdleTimeoutEnabled));
            if (autoEnabledTrans) OnPropertyChanged(nameof(IsExitOnTransparentKey));
        }
    }

    /// <summary>Inverse of <see cref="IsEndlessTimeout"/>, for XAML
    /// IsEnabled bindings on the idle-timeout slider/textbox.</summary>
    public bool IsIdleTimeoutEnabled => !_snapshot.EndlessTimeout;

    /// <summary>
    /// When true, a keypress on a literal <c>&amp;trans</c> binding of the
    /// active layer reverts the mouse-layer push immediately (early exit
    /// before idle). Independent of <see cref="IsExitOnEmptyKey"/>
    /// and <see cref="IsEndlessTimeout"/>; works with any timeout.
    /// Cannot be unset while endless is on if it's the last enabled flag.
    /// </summary>
    public bool IsExitOnTransparentKey
    {
        get => _snapshot.ExitOnTransparentKey;
        set
        {
            if (_snapshot.ExitOnTransparentKey == value) return;
            if (!value
                && _snapshot.EndlessTimeout
                && !_snapshot.ExitOnEmptyKey)
            {
                // Endless requires ≥1 exit gesture; bounce the UI back.
                OnPropertyChanged();
                return;
            }
            Update(_snapshot with { ExitOnTransparentKey = value });
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When true, a keypress on a literal <c>&amp;none</c> binding of the
    /// active layer reverts the mouse-layer push immediately. See
    /// <see cref="IsExitOnTransparentKey"/> for sibling semantics
    /// and the endless-mode "≥1 gesture required" rule.
    /// </summary>
    public bool IsExitOnEmptyKey
    {
        get => _snapshot.ExitOnEmptyKey;
        set
        {
            if (_snapshot.ExitOnEmptyKey == value) return;
            if (!value
                && _snapshot.EndlessTimeout
                && !_snapshot.ExitOnTransparentKey)
            {
                OnPropertyChanged();
                return;
            }
            Update(_snapshot with { ExitOnEmptyKey = value });
            OnPropertyChanged();
        }
    }
}
