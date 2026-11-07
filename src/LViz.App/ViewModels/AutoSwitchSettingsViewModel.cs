using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Auto-switch tab of the Settings window: the engine's fallback / exit-key
/// options (read and written on <see cref="MainWindowViewModel.AutoSwitch"/>
/// directly), the app-to-layer rules edit buffer with its live "would fire"
/// preview, and the running-process picker that feeds the new-rule field.
/// Rule edits are committed to the engine only by <see cref="CommitAppLayerRules"/>.
/// </summary>
public sealed partial class AutoSwitchSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private bool _disposed;

    public AutoSwitchSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _main.AutoSwitch.PropertyChanged += OnEnginePropertyChanged;

        // Seed the edit buffer from the committed list. The engine owns
        // AutoSwitch.AppLayerRules; this VM reads/writes EditingRules.
        // CommitAppLayerRules() pushes the buffer back on window close.
        foreach (var r in _main.AutoSwitch.AppLayerRules)
            EditingRules.Add(r);
        EditingRules.CollectionChanged += OnEditingRulesChanged;

        RefreshRunningProcesses();
    }

    // Fallback mode — bound to two radio buttons. Avalonia ToggleButton's
    // IsChecked binds bidirectionally to a bool per radio; both radios share
    // the GroupName so only one is ever true. Each property routes
    // through MainWindowViewModel.AutoSwitchFallbackMode (persisted per-keyboard).

    public bool IsFallbackPrevious
    {
        get => _main.AutoSwitch.FallbackMode == AutoSwitchFallbackMode.Previous;
        set
        {
            if (!value || IsFallbackPrevious) return;
            _main.AutoSwitch.FallbackMode = AutoSwitchFallbackMode.Previous;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFallbackBase));
        }
    }

    public bool IsFallbackBase
    {
        get => _main.AutoSwitch.FallbackMode == AutoSwitchFallbackMode.Base;
        set
        {
            if (!value || IsFallbackBase) return;
            _main.AutoSwitch.FallbackMode = AutoSwitchFallbackMode.Base;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFallbackPrevious));
        }
    }

    /// <summary>
    /// Per-keyboard opt-in: a keypress on a literal <c>&amp;trans</c> binding
    /// of the active layer exits an in-flight managed-app push (to the
    /// configured fallback). Independent of <see cref="IsExitOnEmptyKey"/>
    /// and of the double-tap exit-key — any enabled gesture triggers the exit.
    /// </summary>
    public bool IsExitOnTransparentKey
    {
        get => _main.AutoSwitch.ExitOnTransparentKey;
        set
        {
            if (_main.AutoSwitch.ExitOnTransparentKey == value) return;
            _main.AutoSwitch.ExitOnTransparentKey = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Per-keyboard opt-in: a keypress on a literal <c>&amp;none</c> binding
    /// of the active layer exits an in-flight managed-app push. See
    /// <see cref="IsExitOnTransparentKey"/> for sibling semantics.
    /// </summary>
    public bool IsExitOnEmptyKey
    {
        get => _main.AutoSwitch.ExitOnEmptyKey;
        set
        {
            if (_main.AutoSwitch.ExitOnEmptyKey == value) return;
            _main.AutoSwitch.ExitOnEmptyKey = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Human-readable summary of the active keyboard's exit-tap
    /// key (e.g. "#42") or a localized "(none)" when unset. Drives the
    /// label next to the "Pick…" button.</summary>
    public string ExitTapSummary
    {
        get
        {
            var key = _main.AutoSwitch.ExitTapKey;
            if (key is null) return Loc.Instance["Settings_AutoSwitch_ExitKeysNone"];
            return $"#{key.Value}";
        }
    }

    /// <summary>True when an exit-tap key is configured for the active
    /// keyboard. Drives the IsEnabled state of the Clear button.</summary>
    public bool HasExitTap => _main.AutoSwitch.ExitTapKey is not null;

    /// <summary>Snapshot of the currently-configured exit-tap key, used by
    /// the SettingsWindow code-behind to seed the picker dialog.</summary>
    public int? ExitTapKey => _main.AutoSwitch.ExitTapKey;

    /// <summary>Applies a freshly-picked exit-tap key. Called by the
    /// SettingsWindow code-behind after the picker dialog closes with an
    /// OK result.</summary>
    public void ApplyExitTapKey(int? index)
    {
        _main.AutoSwitch.SetExitTapKey(index);
    }

    [RelayCommand]
    private void ClearExitTap() => _main.AutoSwitch.SetExitTapKey(null);

    /// <summary>Active keyboard profile. The SettingsWindow code-behind reads
    /// this when building an <see cref="ExitKeyPickerViewModel"/> so the
    /// picker renders the keyboard the user is configuring.</summary>
    public Core.Layout.IKeyboardProfile ActiveKeyboardProfile => _main.SelectedKeyboard;

    /// <summary>
    /// In-progress edit buffer for app→layer rules. All add/remove/move
    /// commands mutate this collection; the engine only sees the changes
    /// after <see cref="CommitAppLayerRules"/> runs on window close.
    /// </summary>
    public ObservableCollection<AppLayerRule> EditingRules { get; } = new();

    /// <summary>True when the rules list is empty. Drives the empty-state
    /// hint visibility on the Auto-switch tab.</summary>
    public bool HasNoEditingRules => EditingRules.Count == 0;

    private void OnEditingRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoEditingRules));
        OnPropertyChanged(nameof(WouldFireText));
    }

    /// <summary>
    /// Pushes the edit buffer back to <see cref="MainWindowViewModel"/> so the
    /// engine sees the new ruleset and the change is persisted. Called by
    /// <see cref="Views.SettingsWindow"/> on close; same auto-commit UX as the
    /// pre-refactor live writes, just batched.
    /// </summary>
    public void CommitAppLayerRules()
    {
        _main.AutoSwitch.ApplyAppLayerRules(EditingRules);
    }

    /// <summary>Text typed into the "new rule" process-name field. Capped at
    /// 200 chars in the XAML; keeps the settings file from ballooning on
    /// malformed input.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAppLayerRuleCommand))]
    private string _newRuleProcessMatch = "";

    /// <summary>Layer selected in the "new rule" dropdown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAppLayerRuleCommand))]
    private LayerViewModel? _newRuleLayer;

    private bool CanAddAppLayerRule() =>
        !string.IsNullOrWhiteSpace(NewRuleProcessMatch) && NewRuleLayer is not null;

    [RelayCommand(CanExecute = nameof(CanAddAppLayerRule))]
    private void AddAppLayerRule()
    {
        if (NewRuleLayer is null) return;
        var trimmed = NewRuleProcessMatch.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        // Upsert by process name (case-insensitive) so re-adding the same app
        // with a different layer updates in place instead of appending a
        // duplicate-shadowed entry.
        for (var i = 0; i < EditingRules.Count; i++)
        {
            if (string.Equals(EditingRules[i].ProcessMatch, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                if (EditingRules[i].LayerIndex != NewRuleLayer.Index)
                    EditingRules[i] = new AppLayerRule(EditingRules[i].ProcessMatch, NewRuleLayer.Index);
                NewRuleProcessMatch = "";
                return;
            }
        }
        EditingRules.Add(new AppLayerRule(trimmed, NewRuleLayer.Index));
        NewRuleProcessMatch = "";
    }

    [RelayCommand]
    private void RemoveAppLayerRule(AppLayerRule? rule)
    {
        if (rule is null) return;
        EditingRules.Remove(rule);
    }

    [RelayCommand]
    private void MoveAppLayerRuleUp(AppLayerRule? rule) => MoveRuleBy(rule, -1);

    [RelayCommand]
    private void MoveAppLayerRuleDown(AppLayerRule? rule) => MoveRuleBy(rule, +1);

    private void MoveRuleBy(AppLayerRule? rule, int delta)
    {
        if (rule is null) return;
        var i = EditingRules.IndexOf(rule);
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= EditingRules.Count) return;
        EditingRules.Move(i, j);
    }

    /// <summary>
    /// Localized "Would fire: layer N" readout for the currently focused app.
    /// Resolved against <see cref="EditingRules"/> (not the engine's committed
    /// list) so the preview reflects in-progress edits before they're
    /// committed on close.
    /// </summary>
    public string WouldFireText
    {
        get
        {
            var match = AppLayerRuleMatcher.Match(_main.AutoSwitch.ActiveWindow, EditingRules);
            if (match is null) return Loc.Instance["Settings_AutoSwitch_WouldFireNone"];
            return Loc.Instance.Format("Settings_AutoSwitch_WouldFireFormat", FormatLayerLabel(match.LayerIndex));
        }
    }

    /// <summary>"Symbol (L:1)" for a known layer, or "(invalid layer)" if the
    /// rule points at an index no longer present in the active keymap. Shared
    /// by the rules-list rows and the WouldFire readout.</summary>
    private string FormatLayerLabel(int layerIndex)
    {
        var layer = _main.LayerTabs.Layers.FirstOrDefault(l => l.Index == layerIndex);
        if (layer is null) return Loc.Instance["Settings_AutoSwitch_LayerInvalid"];
        return Loc.Instance.Format("Settings_AutoSwitch_LayerFormat", layer.DisplayName, layer.Index);
    }

    // --- Process picker -------------------------------------------------

    /// <summary>Full snapshot of running-process names, dedup'd + sorted. Source
    /// of truth for the visible <see cref="RunningProcessNames"/> after the
    /// filter is applied.</summary>
    private readonly List<string> _allRunningProcessNames = new();

    /// <summary>Filtered, visible picker list. Case-insensitive substring match
    /// against <see cref="ProcessFilter"/>; equals the full snapshot when the
    /// filter is empty.</summary>
    public ObservableCollection<string> RunningProcessNames { get; } = new();

    /// <summary>True when the filtered list is empty. XAML swaps the picker
    /// list for the localized empty-state hint when this is false.</summary>
    public bool HasRunningProcesses => RunningProcessNames.Count > 0;

    /// <summary>Filter text typed into the picker. Live-applied to the visible
    /// list on every keystroke.</summary>
    [ObservableProperty]
    private string _processFilter = "";

    partial void OnProcessFilterChanged(string value) => ApplyProcessFilter();

    /// <summary>Invoked by the SettingsWindow code-behind right before the
    /// picker flyout opens. Cross-OS via <see cref="System.Diagnostics.Process.GetProcesses"/>
    /// — same source as <c>IActiveWindowMonitor.ProcessName</c>, so what the
    /// user picks here matches what the monitor will report later. Clears the
    /// active filter so each open starts fresh.</summary>
    public void RefreshRunningProcesses()
    {
        var names = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch
            {
                // Access denied on protected processes is normal — skip.
            }
            finally
            {
                p.Dispose();
            }
        }
        _allRunningProcessNames.Clear();
        _allRunningProcessNames.AddRange(names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        ProcessFilter = "";
        ApplyProcessFilter();
    }

    private void ApplyProcessFilter()
    {
        var filter = ProcessFilter?.Trim() ?? "";
        RunningProcessNames.Clear();
        foreach (var n in _allRunningProcessNames)
        {
            if (filter.Length == 0 ||
                n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                RunningProcessNames.Add(n);
            }
        }
        OnPropertyChanged(nameof(HasRunningProcesses));
    }

    /// <summary>Picker item selected → drop the name into the textbox. The
    /// flyout closes itself on selection (Avalonia default).</summary>
    [RelayCommand]
    private void PickProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        NewRuleProcessMatch = name!;
    }

    /// <summary>

    /// <summary>Re-raises everything that depends on the active keyboard
    /// (exit-key state is stored per keyboard). Called by
    /// <see cref="SettingsViewModel"/> when the selected keyboard changes.</summary>
    internal void OnActiveKeyboardChanged()
    {
        OnPropertyChanged(nameof(ActiveKeyboardProfile));
        OnPropertyChanged(nameof(ExitTapKey));
        OnPropertyChanged(nameof(ExitTapSummary));
        OnPropertyChanged(nameof(HasExitTap));
        OnPropertyChanged(nameof(IsExitOnTransparentKey));
        OnPropertyChanged(nameof(IsExitOnEmptyKey));
    }

    // Re-raises derived properties off the auto-switch engine's own
    // PropertyChanged, translating exactly as SettingsViewModel._mainPropagators
    // does for MainWindowViewModel properties.
    private static readonly Dictionary<string, Action<AutoSwitchSettingsViewModel>> _enginePropagators = new()
    {
        // ActiveWindow (not MatchedAppLayerRule) drives the WouldFire preview —
        // the preview is computed locally against the edit buffer, so the
        // engine's cached match is the wrong signal.
        [nameof(AutoSwitchEngine.ActiveWindow)] = s => s.OnPropertyChanged(nameof(WouldFireText)),
        [nameof(AutoSwitchEngine.FallbackMode)] = s =>
        {
            s.OnPropertyChanged(nameof(IsFallbackPrevious));
            s.OnPropertyChanged(nameof(IsFallbackBase));
        },
        [nameof(AutoSwitchEngine.ExitOnTransparentKey)] = s =>
            s.OnPropertyChanged(nameof(IsExitOnTransparentKey)),
        [nameof(AutoSwitchEngine.ExitOnEmptyKey)] = s =>
            s.OnPropertyChanged(nameof(IsExitOnEmptyKey)),
        [nameof(AutoSwitchEngine.ExitTapKey)] = s =>
        {
            s.OnPropertyChanged(nameof(ExitTapKey));
            s.OnPropertyChanged(nameof(ExitTapSummary));
            s.OnPropertyChanged(nameof(HasExitTap));
        },
    };

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _enginePropagators.TryGetValue(name, out var propagate))
            propagate(this);
    }

    /// <summary>Detaches the engine and edit-buffer hooks so a closed Settings
    /// window's VM graph is collectible.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.AutoSwitch.PropertyChanged -= OnEnginePropertyChanged;
        EditingRules.CollectionChanged -= OnEditingRulesChanged;
    }
}
