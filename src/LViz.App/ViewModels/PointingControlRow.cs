using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.ViewModels;

/// <summary>One pointing action the target device handles (DPI / DPI index /
/// drag-scroll / snipe), plus a Send button. The action's wire byte, label, and
/// value kind are fixed at construction from the device's manifest. A
/// <see cref="ValueKind.Toggle"/> action (snipe,
/// drag-scroll) is an on/off flag rendered as a checkbox; a count/index action
/// (DPI, DPI index) is rendered as a numeric field.</summary>
public sealed partial class PointingControlRow : ObservableObject
{
    private readonly Func<PointingControlRow, Task> _send;

    public PointingControlRow(string capabilityId, byte actionByte, string label,
        ValueKind kind, int defaultValue, Func<PointingControlRow, Task> send)
    {
        CapabilityId = capabilityId;
        ActionByte = actionByte;
        Label = label;
        Kind = kind;
        _value = defaultValue;
        _send = send;
    }

    public string CapabilityId { get; }
    public byte ActionByte { get; }
    public string Label { get; }
    public ValueKind Kind { get; }

    /// <summary>On/off action → checkbox (<see cref="IsOn"/>); otherwise a
    /// numeric field (<see cref="Value"/>). Drives the XAML widget choice.</summary>
    public bool IsToggle => Kind == ValueKind.Toggle;
    public bool IsNumeric => !IsToggle;

    /// <summary>Checkbox state for a <see cref="IsToggle"/> action.</summary>
    [ObservableProperty]
    private bool _isOn;

    /// <summary>Numeric field value for a count/index action.</summary>
    [ObservableProperty]
    private int _value;

    /// <summary>The uint32 payload to send: 0/1 for a toggle, the clamped
    /// numeric value otherwise.</summary>
    public uint PayloadValue => IsToggle ? (IsOn ? 1u : 0u) : (uint)Math.Max(0, Value);

    [RelayCommand]
    private Task SendAsync() => _send(this);
}
