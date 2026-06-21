using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>The three ways the pipeline editor dialog can close.</summary>
public enum PipelineEditorOutcome { Cancel, Save, Delete }

/// <summary>
/// View model for the single-screen pipeline editor dialog. Edits a working
/// <see cref="PipelineEditRow"/> deep-copied from the source (so Cancel discards),
/// owns the step add/reorder/remove commands, and validates the name for
/// non-emptiness + uniqueness. The owning <see cref="ActionPipelinesViewModel"/>
/// applies <see cref="Working"/> back into its library only on a Save outcome.
/// </summary>
public sealed partial class EditPipelineDialogViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<PipelineStepKindOption> _stepKinds;
    private readonly ObservableCollection<PipelineTargetOption> _deviceOptions;
    private readonly IReadOnlyList<PointingActionOption> _pointingActions;
    private readonly IReadOnlyList<ActionPipeline> _otherPipelines;
    private readonly IReadOnlyList<string> _pipelineRefOptions;
    private readonly HashSet<string> _takenNames;
    private readonly Action<ActionPipeline> _runTest;

    /// <param name="source">The pipeline to edit, or null to author a new one.</param>
    /// <param name="otherPipelines">The rest of the library (excludes <paramref name="source"/>):
    /// their names reject duplicates and feed the sub-pipeline picker, and their step
    /// graph drives the edit-time cycle check.</param>
    public EditPipelineDialogViewModel(
        ActionPipeline? source,
        IReadOnlyList<PipelineStepKindOption> stepKinds,
        ObservableCollection<PipelineTargetOption> deviceOptions,
        IReadOnlyList<PointingActionOption> pointingActions,
        IReadOnlyList<ActionPipeline> otherPipelines,
        Action<ActionPipeline> runTest)
    {
        _stepKinds = stepKinds;
        _deviceOptions = deviceOptions;
        _pointingActions = pointingActions;
        _otherPipelines = otherPipelines;
        _pipelineRefOptions = otherPipelines.Select(p => p.Name).ToList();
        _takenNames = new HashSet<string>(_pipelineRefOptions, StringComparer.Ordinal);
        _runTest = runTest;
        _newStepKind = stepKinds[0];
        IsExisting = source is not null;

        Working = new PipelineEditRow(source?.Name ?? "");
        // Subscribe before adding the source steps so each row gets validation wiring.
        Working.Steps.CollectionChanged += OnStepsChanged;
        if (source is not null)
            foreach (var step in source.Steps)
                Working.Steps.Add(NewStepRow(step));

        Working.PropertyChanged += OnWorkingPropertyChanged;
    }

    private void OnStepsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (StepEditRow r in e.OldItems) r.PropertyChanged -= OnStepRowChanged;
        if (e.NewItems is not null)
            foreach (StepEditRow r in e.NewItems) r.PropertyChanged += OnStepRowChanged;
        OnPropertyChanged(nameof(HasNoSteps));
        RaiseValidation();
    }

    // A step's kind or sub-pipeline ref change can make/break a cycle → re-validate.
    private void OnStepRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepEditRow.Kind) or nameof(StepEditRow.PipelineRef))
            RaiseValidation();
    }

    private void RaiseValidation()
    {
        OnPropertyChanged(nameof(CycleError));
        OnPropertyChanged(nameof(HasCycleError));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>Detaches from the working row's events. The working copy outlives
    /// the dialog on Save (it becomes a library row), so release it here.</summary>
    public void Dispose()
    {
        Working.PropertyChanged -= OnWorkingPropertyChanged;
        Working.Steps.CollectionChanged -= OnStepsChanged;
        foreach (var row in Working.Steps) row.PropertyChanged -= OnStepRowChanged;
    }

    /// <summary>The live working copy bound by the dialog and read back on Save.</summary>
    public PipelineEditRow Working { get; }

    /// <summary>True when editing an existing pipeline (enables the Delete button).</summary>
    public bool IsExisting { get; }

    public string DialogTitle => Loc.Instance[IsExisting ? "Settings_Pipeline_EditTitle" : "Settings_Pipeline_NewTitle"];

    public IReadOnlyList<PipelineStepKindOption> StepKinds => _stepKinds;

    public bool HasNoSteps => Working.Steps.Count == 0;

    /// <summary>Save is allowed once the name is non-blank, not a duplicate, and the
    /// pipeline doesn't call itself (directly or transitively).</summary>
    public bool CanSave
    {
        get
        {
            var name = Working.Name?.Trim();
            return !string.IsNullOrWhiteSpace(name) && !_takenNames.Contains(name) && !HasCycleError;
        }
    }

    /// <summary>The cycle chain message (e.g. "A → B → A") if this pipeline's
    /// sub-pipeline steps would form a loop, else null. Backed up by the runner's
    /// runtime guard, but surfaced here so the user sees it before saving.</summary>
    public string? CycleError
    {
        get
        {
            var working = Working.ToPipeline();
            if (string.IsNullOrWhiteSpace(working.Name)) return null; // name error covers it
            var library = new List<ActionPipeline>(_otherPipelines) { working };
            var cycle = PipelineGraph.FindCycle(library, working.Name);
            return cycle is null
                ? null
                : string.Format(Loc.Instance["Settings_Pipeline_CycleError"], string.Join(" → ", cycle));
        }
    }

    public bool HasCycleError => CycleError is not null;

    private void OnWorkingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineEditRow.Name))
            RaiseValidation();
    }

    private StepEditRow NewStepRow(PipelineStep step) =>
        new(step, _stepKinds, _deviceOptions, _pointingActions, _pipelineRefOptions);

    // ── Step kind picker + add ────────────────────────────────────────────

    [ObservableProperty] private PipelineStepKindOption _newStepKind;

    [RelayCommand]
    private void AddStep()
    {
        var kind = NewStepKind.Kind;
        // Device steps (RGB/pointing) each carry their own target. Default a new
        // one to the previous device-step's device so an on→off pair shares a
        // device without re-picking it; fall back to the first available device.
        var target = kind is PipelineStepKind.Rgb or PipelineStepKind.Pointing
            ? Working.Steps.LastOrDefault(s => s.TargetDeviceKey is not null)?.TargetDeviceKey
                ?? _deviceOptions.FirstOrDefault()?.StableKey
            : null;
        Working.Steps.Add(NewStepRow(new PipelineStep(kind, TargetDeviceKey: target)));
    }

    [RelayCommand]
    private void RemoveStep(StepEditRow? step)
    {
        if (step is not null) Working.Steps.Remove(step);
    }

    [RelayCommand]
    private void MoveStepUp(StepEditRow? step) => MoveStep(step, -1);

    [RelayCommand]
    private void MoveStepDown(StepEditRow? step) => MoveStep(step, +1);

    private void MoveStep(StepEditRow? step, int delta)
    {
        if (step is null) return;
        var i = Working.Steps.IndexOf(step);
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= Working.Steps.Count) return;
        Working.Steps.Move(i, j);
    }

    // ── Test ──────────────────────────────────────────────────────────────

    /// <summary>Runs the in-progress (unsaved) pipeline through the real runner.</summary>
    [RelayCommand]
    private void Test() => _runTest(Working.ToPipeline());
}
