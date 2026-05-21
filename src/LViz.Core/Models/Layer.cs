namespace LViz.Core.Models;

/// <summary>
/// A single layer of the keymap. Length of <see cref="Bindings"/> matches the
/// keyboard profile's key count (60 for GO60, 80 for Glove80).
/// </summary>
public sealed record Layer(int Index, string Name, IReadOnlyList<KeyBinding> Bindings);
