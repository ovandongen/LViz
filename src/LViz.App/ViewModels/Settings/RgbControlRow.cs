using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;

namespace LViz.App.ViewModels;

/// <summary>
/// The editor for <c>core.rgb.set</c> on the selected target — its own edit
/// logic, distinct from the pointing rows. Hue + saturation are edited as one
/// <see cref="Color"/> via a <c>ColorView</c> picker (friendlier than numeric
/// boxes); brightness is a separate <see cref="Brightness"/> slider so it can be
/// dimmed independently of the chosen colour. The picker's own value channel is
/// ignored — brightness comes only from the slider.
/// <para>core.rgb.set is absolute and idempotent with every field optional (the
/// wire carries a presence mask), so each field group has an <c>Include*</c> flag:
/// only included fields are sent, the rest are left untouched on-device. Sending
/// with nothing included is a valid empty-mask state query. Derived values are
/// clamped to the protocol ranges (hue 0–359, sat/val 0–100) so the builder never
/// has to reject them.</para>
/// </summary>
public sealed partial class RgbControlRow : ObservableObject
{
    private readonly Func<RgbControlRow, Task> _send;

    public RgbControlRow(Func<RgbControlRow, Task> send) => _send = send;

    [ObservableProperty] private bool _includeOn = true;
    [ObservableProperty] private bool _on = true;

    /// <summary>The picked colour; only its hue + saturation reach the wire
    /// (brightness comes from <see cref="Brightness"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private Color _color = Colors.Red;

    [ObservableProperty] private bool _includeColor = true;

    /// <summary>Brightness percentage (0–100), independent of the picked colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private int _brightness = HidConstants.RgbAction.ValMax;

    [ObservableProperty] private bool _includeBrightness = true;

    [ObservableProperty] private bool _includeEffect;
    [ObservableProperty] private int _effect;

    /// <summary>Swatch preview (HexColorToBrushConverter): the picked hue/sat at
    /// the current brightness, so it shows what the strip will actually look
    /// like rather than the picker's full-bright colour.</summary>
    public string ColorHex
    {
        get
        {
            var hsv = Color.ToHsv();
            var preview = new HsvColor(1, hsv.H, hsv.S, Math.Clamp(Brightness, 0, 100) / 100.0).ToRgb();
            return $"#{preview.R:X2}{preview.G:X2}{preview.B:X2}";
        }
    }

    /// <summary>Builds the request from the included fields: hue/sat from the
    /// colour, brightness from the slider. Unincluded fields are null (left
    /// untouched on-device).</summary>
    public RgbSet ToRgbSet()
    {
        ushort? hue = null;
        byte? sat = null;
        if (IncludeColor)
        {
            var hsv = Color.ToHsv();
            hue = (ushort)Math.Clamp((int)Math.Round(hsv.H), 0, HidConstants.RgbAction.HueMax);
            sat = (byte)Math.Clamp((int)Math.Round(hsv.S * 100), 0, HidConstants.RgbAction.SatMax);
        }

        return new RgbSet(
            On: IncludeOn ? On : null,
            Hue: hue,
            Sat: sat,
            Val: IncludeBrightness ? (byte)Math.Clamp(Brightness, 0, HidConstants.RgbAction.ValMax) : null,
            Effect: IncludeEffect ? (byte)Math.Clamp(Effect, 0, byte.MaxValue) : null);
    }

    /// <summary>
    /// Seeds the editor from a stored <see cref="RgbSet"/> (reverse of
    /// <see cref="ToRgbSet"/>) so a saved RGB pipeline step re-opens with its
    /// fields populated. Each <c>Include*</c> flag follows whether the matching
    /// field is present; hue/sat are folded back into a full-bright
    /// <see cref="Color"/> for the picker (brightness comes from the slider).
    /// </summary>
    public void Seed(RgbSet set)
    {
        IncludeOn = set.On.HasValue;
        On = set.On ?? true;

        IncludeColor = set.Hue.HasValue || set.Sat.HasValue;
        if (IncludeColor)
        {
            var h = set.Hue ?? 0;
            var s = (set.Sat ?? HidConstants.RgbAction.SatMax) / 100.0;
            Color = new HsvColor(1, h, s, 1).ToRgb();
        }

        IncludeBrightness = set.Val.HasValue;
        Brightness = set.Val ?? HidConstants.RgbAction.ValMax;

        IncludeEffect = set.Effect.HasValue;
        Effect = set.Effect ?? 0;
    }

    [RelayCommand]
    private Task SendAsync() => _send(this);
}
