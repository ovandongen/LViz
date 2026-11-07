using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;

namespace LViz.Tests;

/// <summary>
/// Test lookup of ring targets by their stable <see cref="KeyRingTarget.Key"/>:
/// finds the sector the ring offers right now (main ring, inner ring, any
/// category's band, the list panel) and acts on its typed target. A key the
/// ring does not offer is simply not found, like a sector that is not drawn.
/// </summary>
internal static class KeyRingTestExtensions
{
    public static KeyRingTarget? FindTarget(this KeyRingViewModel ring, string key)
    {
        foreach (var segment in ring.Segments.Concat(ring.InnerSegments))
        {
            if (segment.Id == key) return segment.Target;
        }

        foreach (var row in ring.Panel?.Rows ?? [])
        {
            if (row.Id == key) return row.Target;
        }

        // Categories and the dialog's behaviors are addressable on any step
        // (the ring only draws them on some), as the tests need them.
        var categories = Enum.GetValues<ZmkBehaviorGroup>()
            .Select(group => (KeyRingTarget)new KeyRingTarget.Category(group))
            .ToArray();
        foreach (var category in categories)
        {
            if (category.Key == key) return category;
        }

        foreach (var owner in ring.Segments.Select(segment => segment.Target).Concat(categories))
        {
            foreach (var segment in ring.BandFor(owner))
            {
                if (segment.Id == key) return segment.Target;
            }
        }

        return ring.Dialog.AllOtherBehaviors
            .Select(choice => new KeyRingTarget.Behavior(choice))
            .FirstOrDefault(behavior => behavior.Key == key);
    }

    public static bool ChooseId(this KeyRingViewModel ring, string key) =>
        ring.FindTarget(key) is { } target && ring.Choose(target);

    public static IReadOnlyList<KeyRingSegment> BandForId(this KeyRingViewModel ring, string key) =>
        ring.FindTarget(key) is { } target ? ring.BandFor(target) : [];
}
