using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Mutable editor over one <see cref="SignalBinding"/>: a source device, an inbound
/// signal id, and the pipeline name it runs. Signal ids are per-device, so the
/// source device is part of the binding (the Bean's signal 0 ≠ the keyboard's).
/// <see cref="DeviceOptions"/> is the shared (live-updating) device list and
/// <see cref="PipelineNames"/> the shared name list the combos pick from.
/// </summary>
public sealed partial class SignalBindingRow : ObservableObject
{
    public SignalBindingRow(
        int signalId, string sourceDeviceKey, string pipelineName,
        ObservableCollection<string> pipelineNames,
        ObservableCollection<PipelineTargetOption> deviceOptions)
    {
        _signalId = signalId;
        _sourceDeviceKey = sourceDeviceKey;
        _pipelineName = pipelineName;
        PipelineNames = pipelineNames;
        DeviceOptions = deviceOptions;

        // Keep the source-device selection visible when the device list rebuilds
        // (hot-plug / rescan) — the option instances change, so re-raise the
        // computed selection off the stored key. Mirrors StepEditRow.
        DeviceOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedSourceDevice));
    }

    /// <summary>Opaque uint8 id (0–255) the device fires.</summary>
    [ObservableProperty] private int _signalId;

    /// <summary>The router stable key of the device whose signal this binds. An
    /// empty key (or an absent device) matches nothing at run time.</summary>
    [ObservableProperty] private string _sourceDeviceKey;

    /// <summary>References an <see cref="ActionPipeline.Name"/>; a dangling name
    /// resolves to nothing at run time and the signal is ignored.</summary>
    [ObservableProperty] private string _pipelineName;

    public ObservableCollection<string> PipelineNames { get; }

    /// <summary>Live device inventory (shared with the parent VM, rebuilt on hot-plug).</summary>
    public ObservableCollection<PipelineTargetOption> DeviceOptions { get; }

    /// <summary>Source-device combo selection, mapped to/from
    /// <see cref="SourceDeviceKey"/>. Null when the stored key isn't in the current
    /// device list (device absent) — the key is preserved on commit so it reapplies
    /// when the device returns.</summary>
    public PipelineTargetOption? SelectedSourceDevice
    {
        get => DeviceOptions.FirstOrDefault(o => o.StableKey == SourceDeviceKey);
        set
        {
            SourceDeviceKey = value?.StableKey ?? "";
            OnPropertyChanged();
        }
    }

    public SignalBinding ToBinding() => new(SignalId, SourceDeviceKey ?? "", PipelineName ?? "");
}
