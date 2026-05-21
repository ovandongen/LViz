namespace LViz.Core.Models;

/// <summary>
/// A user-defined ZMK hold-tap behavior. The hold-side fires when the key is
/// held past the tapping term; the tap-side fires on a quick tap. The viewer
/// follows hold-taps that wrap a signal macro on their hold side — the hold
/// activates a layer + emits an OS-visible keycode just like a direct signal
/// macro would.
/// </summary>
/// <param name="Name">Behavior name including '&amp;' (e.g. "&amp;ht_symbol").</param>
/// <param name="HoldBinding">Behavior reference fired on hold (e.g. "&amp;mo_symbol_f16signal").</param>
/// <param name="TapBinding">Behavior reference fired on tap (e.g. "&amp;kp").</param>
/// <param name="HoldArity">Number of keymap params consumed by the hold side (0 for 0-param signal macros, 1 for &amp;kp / &amp;mo / &amp;to / etc).</param>
/// <param name="TapArity">Number of keymap params consumed by the tap side.</param>
public sealed record HoldTap(string Name, string HoldBinding, string TapBinding, int HoldArity, int TapArity);
