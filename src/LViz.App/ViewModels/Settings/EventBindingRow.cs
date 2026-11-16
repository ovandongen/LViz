using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Mutable editor over one <see cref="EventBinding"/>: an inbound host event id
/// (string) and the pipeline name it runs. The string-keyed sibling of
/// <see cref="SignalBindingRow"/>. <see cref="PipelineNames"/> is the shared
/// (live-updating) name list the combo picks from.
/// </summary>
public sealed partial class EventBindingRow : ObservableObject
{
    public EventBindingRow(string eventId, string pipelineName, ObservableCollection<string> pipelineNames)
    {
        _eventId = eventId;
        _pipelineName = pipelineName;
        PipelineNames = pipelineNames;
    }

    /// <summary>Opaque string id a CLI/script fires (e.g. "ci:ok").</summary>
    [ObservableProperty] private string _eventId;

    /// <summary>References an <see cref="ActionPipeline.Name"/>; a dangling name
    /// resolves to nothing at run time and the event is rejected.</summary>
    [ObservableProperty] private string _pipelineName;

    public ObservableCollection<string> PipelineNames { get; }

    public EventBinding ToBinding() => new(EventId ?? "", PipelineName ?? "");
}
