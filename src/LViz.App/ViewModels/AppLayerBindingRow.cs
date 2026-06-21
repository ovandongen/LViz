using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// One selectable app-layer trigger: an AutoSwitch rule's process paired with a
/// lifecycle <see cref="AppLayerMoment"/>, plus the combined label shown in the
/// picker (e.g. "Notes — Enter"). The options come from the live AutoSwitch rules ×
/// the four moments, so a binding can only target a trigger a rule can raise.
/// </summary>
public sealed record AppLayerTriggerOption(string ProcessMatch, AppLayerMoment Moment, string Label);

/// <summary>
/// Mutable editor over one <see cref="AppLayerBinding"/>: a trigger (rule process ×
/// lifecycle moment) and the pipeline it runs. The local sibling of
/// <see cref="SignalBindingRow"/> / <see cref="EventBindingRow"/>. The process +
/// moment are stored as the binding's two fields but picked as one combined
/// <see cref="AppLayerTriggerOption"/>. <see cref="AvailableTriggers"/> is owned by
/// the parent VM and filtered to triggers no <em>other</em> row has bound (plus this
/// row's own pick), so each trigger maps to at most one binding.
/// </summary>
public sealed partial class AppLayerBindingRow : ObservableObject
{
    public AppLayerBindingRow(
        string processMatch, AppLayerMoment moment, string pipelineName,
        ObservableCollection<string> pipelineNames)
    {
        _processMatch = processMatch;
        _moment = moment;
        _pipelineName = pipelineName;
        PipelineNames = pipelineNames;

        // Keep the selection visible when the parent refilters the list — the option
        // instances are stable, but re-raise the computed selection to be safe.
        AvailableTriggers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedTrigger));
    }

    /// <summary>Case-insensitive match against the firing rule's
    /// <see cref="AppLayerRule.ProcessMatch"/>.</summary>
    [ObservableProperty] private string _processMatch;

    /// <summary>The lifecycle moment this binding fires on.</summary>
    [ObservableProperty] private AppLayerMoment _moment;

    /// <summary>References an <see cref="ActionPipeline.Name"/>; a dangling name
    /// resolves to nothing at run time.</summary>
    [ObservableProperty] private string _pipelineName;

    /// <summary>Triggers this row may pick — the master list minus triggers other
    /// rows already bound. Reconciled in place by the parent VM.</summary>
    public ObservableCollection<AppLayerTriggerOption> AvailableTriggers { get; } = new();

    public ObservableCollection<string> PipelineNames { get; }

    /// <summary>Trigger combo selection, mapped to/from <see cref="ProcessMatch"/> +
    /// <see cref="Moment"/>. The row's own trigger always stays in
    /// <see cref="AvailableTriggers"/>, so a populated row never resolves to null.</summary>
    public AppLayerTriggerOption? SelectedTrigger
    {
        get => AvailableTriggers.FirstOrDefault(o =>
            o.Moment == Moment &&
            string.Equals(o.ProcessMatch, ProcessMatch, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return;
            ProcessMatch = value.ProcessMatch;
            Moment = value.Moment;
            OnPropertyChanged();
        }
    }

    public AppLayerBinding ToBinding() => new(ProcessMatch ?? "", Moment, PipelineName ?? "");
}
