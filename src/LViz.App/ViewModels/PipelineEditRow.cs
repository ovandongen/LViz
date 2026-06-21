using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Mutable editor over one named <see cref="ActionPipeline"/>: an editable
/// <see cref="Name"/> plus its ordered <see cref="StepEditRow"/> list. Produces a
/// fresh immutable pipeline on commit via <see cref="ToPipeline"/>. <see cref="Summary"/>
/// is a compact one-line step digest for the tab's pipeline cards, refreshed via
/// <see cref="RecomputeSummary"/> after an edit lands.
/// </summary>
public sealed partial class PipelineEditRow : ObservableObject
{
    public PipelineEditRow(string name) => _name = name;

    [ObservableProperty] private string _name;

    public ObservableCollection<StepEditRow> Steps { get; } = new();

    /// <summary>Compact "step · step · step" digest shown on the pipeline card.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>Rebuilds <see cref="Summary"/> from the current steps. Called on
    /// seed and whenever the editor commits a change back to this row.</summary>
    public void RecomputeSummary() =>
        Summary = Steps.Count == 0
            ? Loc.Instance["Settings_Pipeline_NoSteps"]
            : string.Join("  ·  ", Steps.Select(s => s.Summarize()));

    public ActionPipeline ToPipeline() => new(Name, Steps.Select(s => s.ToStep()).ToList());
}
