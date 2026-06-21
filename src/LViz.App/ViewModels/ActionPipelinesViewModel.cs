using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Protocol;

namespace LViz.App.ViewModels;

/// <summary>
/// Settings-scoped view model for the Action Pipelines tab. Shows the named
/// pipeline library (<see cref="UserSettings.ActionPipelines"/>) as compact,
/// click-to-edit cards and the <c>signal.fire</c> id → pipeline bindings
/// (<see cref="UserSettings.SignalBindings"/>); commit-on-close like the
/// auto-switch rules. Creating and editing a pipeline happens in the
/// <see cref="EditPipelineDialogViewModel"/> popup — this VM only builds the
/// editor (<see cref="CreateEditor"/>) and applies its outcome
/// (<see cref="ApplyEditorResult"/>). Transient — built per Settings open,
/// disposed with the owning <see cref="SettingsViewModel"/>.
/// </summary>
public sealed partial class ActionPipelinesViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRouter _router;
    private readonly ISettingsService _settings;
    private readonly MainWindowViewModel _main;
    private bool _disposed;

    /// <summary>The pipeline library (compact cards).</summary>
    public ObservableCollection<PipelineEditRow> Pipelines { get; } = new();

    /// <summary>signal-id → pipeline-name bindings.</summary>
    public ObservableCollection<SignalBindingRow> SignalBindings { get; } = new();

    /// <summary>host event-id (string) → pipeline-name bindings.</summary>
    public ObservableCollection<EventBindingRow> EventBindings { get; } = new();

    /// <summary>app-focus lifecycle moment → pipeline-name bindings.</summary>
    public ObservableCollection<AppLayerBindingRow> AppLayerBindings { get; } = new();

    /// <summary>Live device inventory for device-step target pickers (shared with the editor's step rows).</summary>
    public ObservableCollection<PipelineTargetOption> DeviceOptions { get; } = new();

    /// <summary>Current pipeline names, for the binding picker. Kept in sync with <see cref="Pipelines"/>.</summary>
    public ObservableCollection<string> PipelineNames { get; } = new();

    /// <summary>The step kinds, localized, for the editor's step pickers.</summary>
    public IReadOnlyList<PipelineStepKindOption> StepKinds { get; }

    /// <summary>The four routable pointing actions, for pointing-step pickers.</summary>
    public IReadOnlyList<PointingActionOption> PointingActions { get; }

    // Master trigger list: live AutoSwitch rules × the four moments. Built once per
    // Settings open (reopen Settings to pick up rules added this session). Each row's
    // AvailableTriggers is this minus triggers other rows already bound, enforcing
    // one binding per (process, moment).
    private IReadOnlyList<AppLayerTriggerOption> _allTriggers = Array.Empty<AppLayerTriggerOption>();

    public ActionPipelinesViewModel(ICapabilityRouter router, ISettingsService settings, MainWindowViewModel main)
    {
        _router = router;
        _settings = settings;
        _main = main;

        StepKinds = new[]
        {
            new PipelineStepKindOption(PipelineStepKind.KeyboardLayer, Loc.Instance["Settings_Pipeline_Kind_KeyboardLayer"]),
            new PipelineStepKindOption(PipelineStepKind.Rgb, Loc.Instance["Settings_Pipeline_Kind_Rgb"]),
            new PipelineStepKindOption(PipelineStepKind.Pointing, Loc.Instance["Settings_Pipeline_Kind_Pointing"]),
            new PipelineStepKindOption(PipelineStepKind.Launch, Loc.Instance["Settings_Pipeline_Kind_Launch"]),
            new PipelineStepKindOption(PipelineStepKind.Shell, Loc.Instance["Settings_Pipeline_Kind_Shell"]),
            new PipelineStepKindOption(PipelineStepKind.Delay, Loc.Instance["Settings_Pipeline_Kind_Delay"]),
            new PipelineStepKindOption(PipelineStepKind.Pipeline, Loc.Instance["Settings_Pipeline_Kind_Pipeline"]),
        };

        PointingActions = new[]
        {
            new PointingActionOption(HidConstants.PointingAction.DpiSet, Loc.Instance["Settings_DeviceRouting_Dpi"]),
            new PointingActionOption(HidConstants.PointingAction.DpiSetIndex, Loc.Instance["Settings_DeviceRouting_PointingDpiIndex"]),
            new PointingActionOption(HidConstants.PointingAction.DragScrollSet, Loc.Instance["Settings_DeviceRouting_PointingDragScroll"]),
            new PointingActionOption(HidConstants.PointingAction.SnipeSet, Loc.Instance["Settings_DeviceRouting_PointingSnipe"]),
        };

        SeedFromSettings();
        RebuildDeviceOptions();
        _router.RoutesChanged += OnRoutesChanged;
    }

    private void SeedFromSettings()
    {
        var s = _settings.Load();
        foreach (var p in s.ActionPipelines)
        {
            var row = new PipelineEditRow(p.Name);
            foreach (var step in p.Steps)
                // Library cards are summary-only (never an editable combo), so the
                // sub-pipeline picker options aren't needed here.
                row.Steps.Add(new StepEditRow(step, StepKinds, DeviceOptions, PointingActions, Array.Empty<string>()));
            row.RecomputeSummary();
            AttachPipelineRow(row);
        }
        RebuildPipelineNames();

        foreach (var b in s.SignalBindings)
            SignalBindings.Add(new SignalBindingRow(b.SignalId, b.SourceDeviceKey, b.PipelineName, PipelineNames, DeviceOptions));

        foreach (var b in s.EventBindings)
            EventBindings.Add(new EventBindingRow(b.EventId, b.PipelineName, PipelineNames));

        _allTriggers = BuildTriggerOptions(s);
        foreach (var b in s.AppLayerBindings)
            AttachAppLayerRow(new AppLayerBindingRow(b.ProcessMatch, b.Moment, b.PipelineName, PipelineNames));
        RecomputeAvailableTriggers();
    }

    // Triggers = each distinct rule process × the four moments. Seeded bindings' own
    // processes are folded in too, so a binding pointing at a since-deleted rule
    // still shows (and round-trips) instead of resolving to null.
    private IReadOnlyList<AppLayerTriggerOption> BuildTriggerOptions(UserSettings s)
    {
        var moments = new[]
        {
            (AppLayerMoment.Enter, Loc.Instance["AppLayerMoment_Enter"]),
            (AppLayerMoment.Leave, Loc.Instance["AppLayerMoment_Leave"]),
            (AppLayerMoment.Exit, Loc.Instance["AppLayerMoment_Exit"]),
            (AppLayerMoment.Reenter, Loc.Instance["AppLayerMoment_Reenter"]),
        };

        // Rule processes in rule order, then any extra processes referenced by
        // existing bindings — all distinct, case-insensitive. No catch-all.
        var processes = new List<string>();
        void AddProcess(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!processes.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                processes.Add(p);
        }
        foreach (var r in _main.AutoSwitch.AppLayerRules) AddProcess(r.ProcessMatch);
        foreach (var b in s.AppLayerBindings) AddProcess(b.ProcessMatch);

        var options = new List<AppLayerTriggerOption>();
        foreach (var p in processes)
            foreach (var (moment, momentLabel) in moments)
                options.Add(new AppLayerTriggerOption(p, moment, $"{p} — {momentLabel}"));
        return options;
    }

    private void AttachPipelineRow(PipelineEditRow row)
    {
        row.PropertyChanged += OnPipelineRowPropertyChanged;
        Pipelines.Add(row);
    }

    private void OnPipelineRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineEditRow.Name))
            RebuildPipelineNames();
    }

    public bool HasNoPipelines => Pipelines.Count == 0;

    // ── Editor popup ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the editor VM for a pipeline (or null to author a new one). The
    /// owning view shows it as a modal and feeds the outcome back to
    /// <see cref="ApplyEditorResult"/>. The editor edits a deep copy, so a
    /// cancelled dialog leaves the library untouched.
    /// </summary>
    public EditPipelineDialogViewModel CreateEditor(PipelineEditRow? source)
    {
        var otherPipelines = Pipelines.Where(p => !ReferenceEquals(p, source)).Select(p => p.ToPipeline()).ToList();
        return new EditPipelineDialogViewModel(
            source?.ToPipeline(), StepKinds, DeviceOptions, PointingActions, otherPipelines, _main.RunPipeline);
    }

    /// <summary>Applies the editor's outcome: Save swaps in the edited copy (or
    /// appends it for a new pipeline), Delete drops the source, Cancel is a no-op.</summary>
    public void ApplyEditorResult(PipelineEditRow? source, PipelineEditorOutcome outcome, EditPipelineDialogViewModel editor)
    {
        switch (outcome)
        {
            case PipelineEditorOutcome.Save:
                var working = editor.Working;
                working.RecomputeSummary();
                if (source is null)
                {
                    AttachPipelineRow(working);
                }
                else
                {
                    var index = Pipelines.IndexOf(source);
                    source.PropertyChanged -= OnPipelineRowPropertyChanged;
                    if (index >= 0) Pipelines[index] = working; else Pipelines.Add(working);
                    working.PropertyChanged += OnPipelineRowPropertyChanged;
                }
                RebuildPipelineNames();
                OnPropertyChanged(nameof(HasNoPipelines));
                break;

            case PipelineEditorOutcome.Delete:
                if (source is not null)
                {
                    source.PropertyChanged -= OnPipelineRowPropertyChanged;
                    Pipelines.Remove(source);
                    RebuildPipelineNames();
                    OnPropertyChanged(nameof(HasNoPipelines));
                }
                break;

            case PipelineEditorOutcome.Cancel:
                break;
        }
    }

    // ── Signal bindings ───────────────────────────────────────────────────

    [ObservableProperty]
    private int _newBindingSignalId;

    [RelayCommand]
    private void AddBinding()
    {
        var id = Math.Clamp(NewBindingSignalId, 0, 255);
        var defaultName = PipelineNames.FirstOrDefault() ?? "";
        var defaultSource = DeviceOptions.FirstOrDefault()?.StableKey ?? "";
        SignalBindings.Add(new SignalBindingRow(id, defaultSource, defaultName, PipelineNames, DeviceOptions));
        OnPropertyChanged(nameof(HasNoBindings));
    }

    [RelayCommand]
    private void RemoveBinding(SignalBindingRow? row)
    {
        if (row is null) return;
        SignalBindings.Remove(row);
        OnPropertyChanged(nameof(HasNoBindings));
    }

    public bool HasNoBindings => SignalBindings.Count == 0;

    // ── Event bindings ────────────────────────────────────────────────────

    [ObservableProperty]
    private string _newBindingEventId = "";

    [RelayCommand]
    private void AddEventBinding()
    {
        var id = (NewBindingEventId ?? "").Trim();
        if (id.Length == 0) return;
        var defaultName = PipelineNames.FirstOrDefault() ?? "";
        EventBindings.Add(new EventBindingRow(id, defaultName, PipelineNames));
        NewBindingEventId = "";
        OnPropertyChanged(nameof(HasNoEventBindings));
    }

    [RelayCommand]
    private void RemoveEventBinding(EventBindingRow? row)
    {
        if (row is null) return;
        EventBindings.Remove(row);
        OnPropertyChanged(nameof(HasNoEventBindings));
    }

    public bool HasNoEventBindings => EventBindings.Count == 0;

    // ── App-layer bindings ──────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAddAppLayerBinding))]
    private void AddAppLayerBinding()
    {
        var used = UsedTriggerKeys(except: null);
        var next = _allTriggers.FirstOrDefault(t => !used.Contains(TriggerKey(t.ProcessMatch, t.Moment)));
        if (next is null) return; // every trigger already bound — CanExecute guards this
        var defaultName = PipelineNames.FirstOrDefault() ?? "";
        AttachAppLayerRow(new AppLayerBindingRow(next.ProcessMatch, next.Moment, defaultName, PipelineNames));
        RecomputeAvailableTriggers();
        OnPropertyChanged(nameof(HasNoAppLayerBindings));
    }

    // One binding per (process, moment): can only add while an unbound trigger remains.
    private bool CanAddAppLayerBinding() => AppLayerBindings.Count < _allTriggers.Count;

    [RelayCommand]
    private void RemoveAppLayerBinding(AppLayerBindingRow? row)
    {
        if (row is null) return;
        row.PropertyChanged -= OnAppLayerRowPropertyChanged;
        AppLayerBindings.Remove(row);
        RecomputeAvailableTriggers();
        OnPropertyChanged(nameof(HasNoAppLayerBindings));
    }

    public bool HasNoAppLayerBindings => AppLayerBindings.Count == 0;

    private void AttachAppLayerRow(AppLayerBindingRow row)
    {
        row.PropertyChanged += OnAppLayerRowPropertyChanged;
        AppLayerBindings.Add(row);
    }

    private void OnAppLayerRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A trigger change frees one trigger and consumes another → refilter every row.
        if (e.PropertyName == nameof(AppLayerBindingRow.Moment) ||
            e.PropertyName == nameof(AppLayerBindingRow.ProcessMatch))
            RecomputeAvailableTriggers();
    }

    private static (string, AppLayerMoment) TriggerKey(string process, AppLayerMoment moment) =>
        (process.ToLowerInvariant(), moment);

    private HashSet<(string, AppLayerMoment)> UsedTriggerKeys(AppLayerBindingRow? except) =>
        AppLayerBindings.Where(r => !ReferenceEquals(r, except))
            .Select(r => TriggerKey(r.ProcessMatch ?? "", r.Moment))
            .ToHashSet();

    // Each row may pick any master trigger not already bound by another row (its own
    // pick always stays available). Reconciled in place so an open combo keeps its
    // selection; the Add command tracks whether any trigger is left.
    private void RecomputeAvailableTriggers()
    {
        foreach (var row in AppLayerBindings)
        {
            var usedByOthers = UsedTriggerKeys(except: row);
            var desired = _allTriggers
                .Where(t => !usedByOthers.Contains(TriggerKey(t.ProcessMatch, t.Moment)))
                .ToList();
            ReconcileTriggerList(row.AvailableTriggers, desired);
        }
        AddAppLayerBindingCommand.NotifyCanExecuteChanged();
    }

    private static void ReconcileTriggerList(
        ObservableCollection<AppLayerTriggerOption> target, List<AppLayerTriggerOption> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            if (i < target.Count && target[i].Equals(d)) continue;
            var existing = target.IndexOf(d);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, d);
        }
    }

    // ── Live device list ──────────────────────────────────────────────────

    private void OnRoutesChanged() => Dispatcher.UIThread.Post(RebuildDeviceOptions);

    private void RebuildDeviceOptions()
    {
        if (_disposed) return;
        // Reconcile in place (add new, remove gone) rather than Clear()+rebuild:
        // a Clear momentarily removes a combo's selected item, and Avalonia writes
        // the resulting null selection back through the TwoWay binding — silently
        // wiping a target the user already picked. Adding device B must not
        // disturb device A's selection on another step.
        var present = _router.Devices.ToDictionary(d => d.StableKey, ToTargetOption);
        for (var i = DeviceOptions.Count - 1; i >= 0; i--)
            if (!present.ContainsKey(DeviceOptions[i].StableKey))
                DeviceOptions.RemoveAt(i);
        foreach (var (key, option) in present)
        {
            var existing = DeviceOptions.FirstOrDefault(o => o.StableKey == key);
            if (existing is null)
                DeviceOptions.Add(option);
            // A device first seen without a manifest (HID race) gets its
            // capabilities on a later rescan — refresh so the layer-action picker
            // sees them. Matching is by key, so replacing keeps the selection.
            // Compare by content (the capability set is a HashSet → reference
            // equality under record ==, which would churn every rebuild).
            else if (existing.DisplayName != option.DisplayName
                     || existing.IsKeyboard != option.IsKeyboard
                     || !existing.HandledCapabilities.SetEquals(option.HandledCapabilities))
                DeviceOptions[DeviceOptions.IndexOf(existing)] = option;
        }
    }

    private static PipelineTargetOption ToTargetOption(RoutedDeviceInfo info) =>
        new(info.StableKey, info.DisplayName, info.IsKeyboard,
            RoutedDeviceRow.HandledCapabilityIds(info.Manifest));

    private void RebuildPipelineNames()
    {
        // In-place reconcile (same rationale as RebuildDeviceOptions): adding a
        // pipeline must not blank out a signal binding's already-selected name.
        var names = Pipelines.Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        for (var i = PipelineNames.Count - 1; i >= 0; i--)
            if (!names.Contains(PipelineNames[i]))
                PipelineNames.RemoveAt(i);
        foreach (var n in names)
            if (!PipelineNames.Contains(n))
                PipelineNames.Add(n);
    }

    // ── Commit ────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists the edited library + bindings. Called on window close. Mirrors the
    /// auto-switch rules' commit-on-close UX.
    /// </summary>
    public void Commit()
    {
        var pipelines = Pipelines.Select(p => p.ToPipeline()).ToList();
        var bindings = SignalBindings.Select(b => b.ToBinding()).ToList();
        var eventBindings = EventBindings.Select(b => b.ToBinding()).ToList();
        var appLayerBindings = AppLayerBindings.Select(b => b.ToBinding()).ToList();
        _settings.Update(s => s with
        {
            ActionPipelines = pipelines,
            SignalBindings = bindings,
            EventBindings = eventBindings,
            AppLayerBindings = appLayerBindings,
        }, "ActionPipelines");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.RoutesChanged -= OnRoutesChanged;
        foreach (var row in Pipelines)
            row.PropertyChanged -= OnPipelineRowPropertyChanged;
    }
}
