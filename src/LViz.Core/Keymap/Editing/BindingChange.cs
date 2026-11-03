using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// One cell of the keymap the editor wants to set to a new binding. The indices
/// are against the session's <c>Current</c> layer list at the moment the change
/// is submitted, not against the baseline.
/// </summary>
/// <param name="LayerIndex">Position of the layer in the current layer list.</param>
/// <param name="KeyIndex">Position of the key inside that layer's bindings.</param>
/// <param name="Binding">The binding the cell should hold afterwards.</param>
public readonly record struct BindingChange(int LayerIndex, int KeyIndex, KeyBinding Binding);
