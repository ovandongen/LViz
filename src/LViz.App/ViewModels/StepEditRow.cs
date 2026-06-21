using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.ViewModels;

/// <summary>
/// Mutable editor over one <see cref="PipelineStep"/>. The persisted model is an
/// immutable record; this row holds the live edit state for the UI and produces a
/// fresh record on commit via <see cref="ToStep"/>. The visible editor swaps with
/// <see cref="Kind"/> (one set of fields per step kind); only the fields relevant
/// to the current kind are read back.
/// </summary>
public sealed partial class StepEditRow : ObservableObject
{
    private readonly IReadOnlyList<PipelineStepKindOption> _kindOptions;

    /// <summary>Live device list (shared with the parent VM, rebuilt on hot-plug)
    /// for the RGB/pointing target picker.</summary>
    public ObservableCollection<PipelineTargetOption> DeviceOptions { get; }

    /// <summary>The four routable pointing actions, for the pointing-step picker.</summary>
    public IReadOnlyList<PointingActionOption> PointingOptions { get; }

    /// <summary>The RGB editor (reused from the device tester) for an Rgb step —
    /// owns the colour-picker + HSB encoding. Its Send button isn't rendered here;
    /// only its field state is captured into the step.</summary>
    public RgbControlRow Rgb { get; } = new(_ => Task.CompletedTask);

    public StepEditRow(
        PipelineStep step,
        IReadOnlyList<PipelineStepKindOption> kindOptions,
        ObservableCollection<PipelineTargetOption> deviceOptions,
        IReadOnlyList<PointingActionOption> pointingOptions,
        IReadOnlyList<string> pipelineRefOptions)
    {
        _kindOptions = kindOptions;
        DeviceOptions = deviceOptions;
        PointingOptions = pointingOptions;
        PipelineRefOptions = pipelineRefOptions;

        _kind = step.Kind;
        _pipelineRef = step.PipelineRef;
        _targetDeviceKey = step.TargetDeviceKey;
        _layerIndex = step.LayerIndex ?? 0;
        _layerAction = step.LayerAction ?? PipelineLayerAction.SetBase;
        if (step.Rgb is { } rgb) Rgb.Seed(rgb);
        _pointingValue = (int)(step.PointingValue ?? 0u);
        _selectedPointing = pointingOptions.FirstOrDefault(o => o.ActionByte == step.PointingActionByte)
            ?? pointingOptions.FirstOrDefault();
        _launchTarget = step.LaunchTarget ?? "";
        _shellCommand = step.ShellCommand ?? "";
        _delayMs = step.DelayMs ?? 100;

        // Keep the target-device selection visible when the device list rebuilds
        // (hot-plug / rescan): the option instances change, so re-raise the
        // computed selection off the stored key.
        DeviceOptions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedTargetDevice));
            RebuildLayerTargets();
        };
        RebuildLayerTargets();
        RebuildLayerActions();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeyboardLayer))]
    [NotifyPropertyChangedFor(nameof(IsRgb))]
    [NotifyPropertyChangedFor(nameof(IsPointing))]
    [NotifyPropertyChangedFor(nameof(IsLaunch))]
    [NotifyPropertyChangedFor(nameof(IsShell))]
    [NotifyPropertyChangedFor(nameof(IsDelay))]
    [NotifyPropertyChangedFor(nameof(IsPipeline))]
    [NotifyPropertyChangedFor(nameof(IsDeviceStep))]
    [NotifyPropertyChangedFor(nameof(IsDeviceLayer))]
    [NotifyPropertyChangedFor(nameof(SelectedKindOption))]
    private PipelineStepKind _kind;

    public bool IsKeyboardLayer => Kind == PipelineStepKind.KeyboardLayer;
    public bool IsRgb => Kind == PipelineStepKind.Rgb;
    public bool IsPointing => Kind == PipelineStepKind.Pointing;
    public bool IsLaunch => Kind == PipelineStepKind.Launch;
    public bool IsShell => Kind == PipelineStepKind.Shell;
    public bool IsDelay => Kind == PipelineStepKind.Delay;
    public bool IsPipeline => Kind == PipelineStepKind.Pipeline;

    /// <summary>RGB and pointing steps address a target device; the others don't.</summary>
    public bool IsDeviceStep => IsRgb || IsPointing;

    public IReadOnlyList<PipelineStepKindOption> KindOptions => _kindOptions;

    /// <summary>The kind picker's selected item, mapped to/from <see cref="Kind"/>.</summary>
    public PipelineStepKindOption? SelectedKindOption
    {
        get => _kindOptions.FirstOrDefault(o => o.Kind == Kind);
        set { if (value is not null) Kind = value.Kind; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceLayer))]
    private string? _targetDeviceKey;

    /// <summary>Target-device combo selection, mapped to/from
    /// <see cref="TargetDeviceKey"/>. Null when the stored key isn't in the
    /// current device list (device absent) — the key is still preserved on commit
    /// so it reapplies when the device returns.</summary>
    public PipelineTargetOption? SelectedTargetDevice
    {
        get => DeviceOptions.FirstOrDefault(o => o.StableKey == TargetDeviceKey);
        set
        {
            TargetDeviceKey = value?.StableKey;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private int _layerIndex;

    // ── Device-targeted layer step ────────────────────────────────────────
    // The capability ids that map to a layer action, in offer order.
    private static readonly (string CapabilityId, PipelineLayerAction Action, string LabelKey)[] _layerActionMap =
    {
        (CapabilityIds.LayerSetBase, PipelineLayerAction.SetBase, "Settings_Pipeline_LayerAction_SetBase"),
        (CapabilityIds.LayerActivate, PipelineLayerAction.Activate, "Settings_Pipeline_LayerAction_Activate"),
        (CapabilityIds.LayerDeactivate, PipelineLayerAction.Deactivate, "Settings_Pipeline_LayerAction_Deactivate"),
    };

    /// <summary>True for a layer step that targets a bus device (non-empty key) —
    /// gates the layer-action picker. An empty target is the visualized keyboard.</summary>
    public bool IsDeviceLayer => IsKeyboardLayer && !string.IsNullOrEmpty(TargetDeviceKey);

    /// <summary>Layer-step target options: the "visualized keyboard" sentinel
    /// (empty key) plus every bus device that advertises a <c>core.layer.*</c>
    /// handle. Rebuilt with the device list.</summary>
    public ObservableCollection<PipelineTargetOption> LayerTargetOptions { get; } = new();

    /// <summary>The layer actions the selected target device advertises. Empty for
    /// the visualized keyboard.</summary>
    public ObservableCollection<PipelineLayerActionOption> LayerActions { get; } = new();

    // The "visualized keyboard" option (empty key). A single stable instance so an
    // in-place reconcile of LayerTargetOptions never disturbs a selection that
    // points at it (a Clear()+rebuild would transiently null the TwoWay combo and
    // write that null back — wiping TargetDeviceKey).
    private readonly PipelineTargetOption _keyboardSentinel =
        new("", Loc.Instance["Settings_Pipeline_VisualizedKeyboard"], IsKeyboard: true,
            new HashSet<string>(StringComparer.Ordinal));

    private void RebuildLayerTargets()
    {
        if (LayerTargetOptions.Count == 0)
            LayerTargetOptions.Add(_keyboardSentinel);

        // In-place reconcile the device rows after the sentinel at [0]: keep only
        // bus devices that advertise a core.layer.* handle.
        var desired = DeviceOptions
            .Where(o => !o.IsKeyboard && _layerActionMap.Any(m => o.HandledCapabilities.Contains(m.CapabilityId)))
            .ToList();
        for (var i = LayerTargetOptions.Count - 1; i >= 1; i--)
            if (desired.All(d => d.StableKey != LayerTargetOptions[i].StableKey))
                LayerTargetOptions.RemoveAt(i);
        foreach (var d in desired)
            if (LayerTargetOptions.All(o => o.StableKey != d.StableKey))
                LayerTargetOptions.Add(d);

        OnPropertyChanged(nameof(SelectedLayerTarget));
    }

    private void RebuildLayerActions()
    {
        LayerActions.Clear();
        var device = DeviceOptions.FirstOrDefault(o => o.StableKey == TargetDeviceKey);
        if (device is not null)
            foreach (var (capId, action, labelKey) in _layerActionMap)
                if (device.HandledCapabilities.Contains(capId))
                    LayerActions.Add(new PipelineLayerActionOption(action, Loc.Instance[labelKey]));

        // Keep the stored action selectable; fall back to the first the device offers.
        if (LayerActions.All(a => a.Action != LayerAction) && LayerActions.Count > 0)
            LayerAction = LayerActions[0].Action;
        OnPropertyChanged(nameof(SelectedLayerAction));
    }

    partial void OnTargetDeviceKeyChanged(string? value) => RebuildLayerActions();

    /// <summary>Layer-step target combo selection (sentinel for the visualized
    /// keyboard), mapped to/from <see cref="TargetDeviceKey"/>.</summary>
    public PipelineTargetOption? SelectedLayerTarget
    {
        get => LayerTargetOptions.FirstOrDefault(o => o.StableKey == (TargetDeviceKey ?? ""));
        set
        {
            TargetDeviceKey = string.IsNullOrEmpty(value?.StableKey) ? null : value!.StableKey;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private PipelineLayerAction _layerAction;

    /// <summary>Layer-action combo selection, mapped to/from <see cref="LayerAction"/>.</summary>
    public PipelineLayerActionOption? SelectedLayerAction
    {
        get => LayerActions.FirstOrDefault(o => o.Action == LayerAction);
        set { if (value is not null) LayerAction = value.Action; }
    }

    [ObservableProperty] private PointingActionOption? _selectedPointing;
    [ObservableProperty] private int _pointingValue;

    [ObservableProperty] private string _launchTarget;
    [ObservableProperty] private string _shellCommand;
    [ObservableProperty] private int _delayMs;

    /// <summary>Names of the other pipelines this step may run (excludes the pipeline
    /// being edited, so a direct self-call can't be picked). Snapshot at dialog open.</summary>
    public IReadOnlyList<string> PipelineRefOptions { get; }

    /// <summary>The pipeline a <see cref="PipelineStepKind.Pipeline"/> step runs.
    /// References an <see cref="ActionPipeline.Name"/>; a dangling name skips at run time.</summary>
    [ObservableProperty] private string? _pipelineRef;

    /// <summary>A compact one-line token for the pipeline card summary, e.g.
    /// "Layer: 2", "RGB → Bean", "Delay: 200ms". Uses the localized kind label and
    /// the resolved device display name; falls back to the stored key when the
    /// device is absent.</summary>
    public string Summarize()
    {
        var kindLabel = SelectedKindOption?.Label ?? Kind.ToString();
        var device = SelectedTargetDevice?.DisplayName ?? TargetDeviceKey ?? "?";
        return Kind switch
        {
            PipelineStepKind.KeyboardLayer => IsDeviceLayer
                ? $"{kindLabel} {LayerIndex} → {device}"
                : $"{kindLabel}: {LayerIndex}",
            PipelineStepKind.Rgb => $"{kindLabel} → {device}",
            PipelineStepKind.Pointing => $"{SelectedPointing?.Label ?? kindLabel} → {device}",
            PipelineStepKind.Launch => $"{kindLabel}: {Shorten(LaunchTarget)}",
            PipelineStepKind.Shell => $"{kindLabel}: {Shorten(ShellCommand)}",
            PipelineStepKind.Delay => $"{kindLabel}: {DelayMs}ms",
            PipelineStepKind.Pipeline => $"{kindLabel}: {PipelineRef ?? "?"}",
            _ => kindLabel,
        };
    }

    private static string Shorten(string? s)
    {
        s = s?.Trim() ?? "";
        return s.Length <= 24 ? s : s[..23] + "…";
    }

    /// <summary>Builds the immutable step from the current edit state, reading
    /// only the fields relevant to <see cref="Kind"/>.</summary>
    public PipelineStep ToStep() => Kind switch
    {
        PipelineStepKind.KeyboardLayer => string.IsNullOrEmpty(TargetDeviceKey)
            ? new PipelineStep(Kind, LayerIndex: LayerIndex)
            : new PipelineStep(Kind, TargetDeviceKey: TargetDeviceKey, LayerIndex: LayerIndex, LayerAction: LayerAction),
        PipelineStepKind.Rgb => new PipelineStep(Kind, TargetDeviceKey: TargetDeviceKey, Rgb: Rgb.ToRgbSet()),
        PipelineStepKind.Pointing => new PipelineStep(Kind,
            TargetDeviceKey: TargetDeviceKey,
            PointingActionByte: SelectedPointing?.ActionByte,
            PointingValue: (uint)Math.Max(0, PointingValue)),
        PipelineStepKind.Launch => new PipelineStep(Kind, LaunchTarget: LaunchTarget),
        PipelineStepKind.Shell => new PipelineStep(Kind, ShellCommand: ShellCommand),
        PipelineStepKind.Delay => new PipelineStep(Kind, DelayMs: Math.Max(0, DelayMs)),
        PipelineStepKind.Pipeline => new PipelineStep(Kind, PipelineRef: PipelineRef),
        _ => new PipelineStep(Kind),
    };
}
