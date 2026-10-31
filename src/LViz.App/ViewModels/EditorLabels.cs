using LViz.Core.Keymap;
using LViz.Core.Layout;

namespace LViz.App.ViewModels;

/// <summary>
/// Per-key editor captions: the base-layer binding label where the keymap has
/// one for that binding index, the binding index otherwise.
/// </summary>
/// <remarks>
/// The label comes from the main board's paint path: a throwaway
/// <see cref="KeyViewModel"/> painted through <see cref="KeyViewModel.ApplyBinding"/>,
/// so editor captions and board captions can never drift apart. The throwaway
/// exists only to reach that formatter. Callers: the wizard's adjust step
/// (<c>LayoutWizardViewModel.EnsureEditor</c>) and the "Edit layout…" window.
/// </remarks>
public static class EditorLabels
{
    public static IReadOnlyList<string> For(LoadedKeymap? keymap, int keyCount, string profileId)
    {
        var baseLayer = keymap is not null && keymap.Layers.Count > 0 ? keymap.Layers[0] : null;
        var labels = new List<string>(keyCount);

        for (var i = 0; i < keyCount; i++)
        {
            var label = i.ToString();
            if (baseLayer is not null && i < baseLayer.Bindings.Count && keymap is not null)
            {
                var resolved = keymap.Resolve(0, i);
                var key = new KeyViewModel(new KeyPosition(i, 0, 0));
                key.ApplyBinding(
                    resolved.Binding,
                    activeLayerIndex: 0,
                    targetLayer: resolved.TargetLayer,
                    targetLayerName: resolved.TargetLayerName,
                    profileId: profileId,
                    holdTap: resolved.HoldTap,
                    macros: keymap.MacrosByName);

                if (!string.IsNullOrEmpty(key.Label))
                    label = key.Label;
            }

            labels.Add(label);
        }

        return labels;
    }
}
