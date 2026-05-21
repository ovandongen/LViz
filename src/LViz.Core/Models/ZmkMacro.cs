namespace LViz.Core.Models;

/// <summary>
/// A ZMK macro definition as exported by the Moergo layout editor.
/// </summary>
/// <param name="Name">Behavior name including the leading '&amp;' (e.g. "&amp;test").</param>
/// <param name="Params">Declared parameter names in order (e.g. ["layer", "code"]).</param>
/// <param name="Bindings">Flat sequence of inner bindings that compose the macro body.</param>
public sealed record ZmkMacro(
    string Name,
    IReadOnlyList<string> Params,
    IReadOnlyList<KeyBinding> Bindings);
