namespace LViz.Core.Settings;

/// <summary>
/// What a <see cref="LayerSettingsRemap"/> had to drop or cannot fix.
/// </summary>
public enum LayerSettingsNoticeKind
{
    /// <summary>An app→layer rule pointed at a deleted layer and was removed.</summary>
    AppRuleRemoved,

    /// <summary>The mouse layer was set to a deleted layer and has been cleared.</summary>
    MouseLayerCleared,

    /// <summary>An action-pipeline keyboard-layer step points at a layer that moved
    /// or was deleted; pipelines are global so nothing was changed — check it.</summary>
    PipelineStepCheck,
}

/// <summary>
/// One thing the remap dropped or could not fix.
/// Name: the rule's ProcessMatch, "" for the mouse layer, the pipeline's Name.
/// OldLayerIndex: the layer number the setting pointed at before the edit.
/// </summary>
public sealed record LayerSettingsNotice(LayerSettingsNoticeKind Kind, string Name, int OldLayerIndex);

/// <summary>
/// The rewritten settings plus what the user should be told about
/// (ordered: app rules, then mouse layer, then pipelines).
/// </summary>
public sealed record LayerSettingsRemapResult(UserSettings Settings, IReadOnlyList<LayerSettingsNotice> Notices);

/// <summary>
/// Rewrites the layer-number-keyed settings of one keyboard profile after its
/// layers were added, deleted or moved, so colours, hidden flags, key labels,
/// app→layer rules and the mouse layer travel with their layer.
///
/// <para><paramref name="map"/> has one entry per old layer: <c>map[i]</c> is
/// layer i's new index, <c>null</c> when it was deleted (see
/// <c>KeymapEditSession.BaselineLayerMap()</c>). An index outside the map's
/// range keeps its setting where it is — a layer the map doesn't mention is
/// treated as untouched, not as deleted.</para>
///
/// <para>Dropping a deleted layer is silent for colours, hidden flags and key
/// labels; app-rule removals and mouse-layer clears are reported, and
/// keyboard-layer pipeline steps pointing at a moved or deleted layer are
/// reported without being changed (pipelines are global). Input is never
/// mutated: changed parts get fresh dictionaries and lists, everything else
/// is carried over by reference for the caller's <c>with</c> expression.</para>
/// </summary>
public static class LayerSettingsRemap
{
    /// <summary>
    /// Applies <paramref name="map"/> to <paramref name="profileId"/>'s
    /// layer-scoped entries in <paramref name="settings"/>. Other profiles and
    /// every other setting are carried over untouched.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> or
    /// <paramref name="map"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="profileId"/> is blank.</exception>
    public static LayerSettingsRemapResult Apply(UserSettings settings, string profileId, IReadOnlyList<int?> map)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(map);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Profile id must not be blank.", nameof(profileId));

        var notices = new List<LayerSettingsNotice>();

        var layerColors = RemapLayerKeyed(settings.LayerColors, profileId, map, static hex => hex);
        var keyLabels = RemapLayerKeyed(settings.KeyLabelOverrides, profileId, map,
            static keys => new Dictionary<int, KeyLabelOverride>(keys));
        var hidden = RemapHiddenLayers(settings.HiddenLayers, profileId, map);
        var rules = RemapAppLayerRules(settings.AppLayerRules, profileId, map, notices);
        var mouse = RemapMouseLayer(settings.MouseLayer, profileId, map, notices);
        CollectPipelineNotices(settings.ActionPipelines, map, notices);

        var result = settings with
        {
            LayerColors = layerColors,
            HiddenLayers = hidden,
            KeyLabelOverrides = keyLabels,
            AppLayerRules = rules,
            MouseLayer = mouse,
        };
        return new LayerSettingsRemapResult(result, notices);
    }

    /// <summary>
    /// True when <paramref name="layerIndex"/> is not covered by the map —
    /// such a layer is treated as untouched rather than deleted.
    /// </summary>
    private static bool IsOutsideMap(int layerIndex, IReadOnlyList<int?> map) =>
        layerIndex < 0 || layerIndex >= map.Count;

    /// <summary>
    /// Remaps the layer keys of <paramref name="profileId"/>'s inner dictionary
    /// (layer → value). Mapped keys move; deleted keys are dropped; keys
    /// outside the map stay. Unmapped survivors are inserted first so a mapped
    /// entry landing on the same index overwrites them. An empty result
    /// removes the profile's entry. Values go through
    /// <paramref name="copyValue"/> (<c>v =&gt; v</c> for immutable values).
    /// </summary>
    private static Dictionary<string, Dictionary<int, TValue>> RemapLayerKeyed<TValue>(
        Dictionary<string, Dictionary<int, TValue>> source,
        string profileId,
        IReadOnlyList<int?> map,
        Func<TValue, TValue> copyValue)
    {
        if (!source.TryGetValue(profileId, out var inner)) return source;

        var remapped = new Dictionary<int, TValue>();
        foreach (var (oldIndex, value) in inner)
            if (IsOutsideMap(oldIndex, map))
                remapped[oldIndex] = copyValue(value);
        foreach (var (oldIndex, value) in inner)
        {
            if (IsOutsideMap(oldIndex, map)) continue;
            if (map[oldIndex] is int target)
                remapped[target] = copyValue(value);
        }

        return PerProfile.SetOrRemove(source, profileId, remapped.Count > 0, remapped);
    }

    /// <summary>
    /// Same move/drop/stay rules as <see cref="RemapLayerKeyed{TValue}"/>,
    /// then the <see cref="HiddenLayers"/> invariants: new index 0 (base) is
    /// dropped, the list stays sorted ascending and duplicate-free, and an
    /// empty list removes the profile's entry.
    /// </summary>
    private static Dictionary<string, List<int>> RemapHiddenLayers(
        Dictionary<string, List<int>> source, string profileId, IReadOnlyList<int?> map)
    {
        if (!source.TryGetValue(profileId, out var hidden)) return source;

        var moved = new List<int>(hidden.Count);
        foreach (var oldIndex in hidden)
        {
            if (IsOutsideMap(oldIndex, map)) moved.Add(oldIndex);
            else if (map[oldIndex] is int target) moved.Add(target);
        }

        var kept = moved.Where(index => index != 0).Distinct().Order().ToList();
        return PerProfile.SetOrRemove(source, profileId, kept.Count > 0, kept);
    }

    /// <summary>
    /// Rewrites <paramref name="profileId"/>'s rules in list order: mapped →
    /// new index, deleted → removed with an <see cref="LayerSettingsNoticeKind.AppRuleRemoved"/>
    /// notice, outside the map → unchanged. An empty list removes the
    /// profile's entry.
    /// </summary>
    private static Dictionary<string, List<AppLayerRule>> RemapAppLayerRules(
        Dictionary<string, List<AppLayerRule>> source, string profileId,
        IReadOnlyList<int?> map, List<LayerSettingsNotice> notices)
    {
        if (!source.TryGetValue(profileId, out var rules)) return source;

        var kept = new List<AppLayerRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (IsOutsideMap(rule.LayerIndex, map))
            {
                kept.Add(rule);
                continue;
            }

            if (map[rule.LayerIndex] is int target) kept.Add(rule with { LayerIndex = target });
            else notices.Add(new LayerSettingsNotice(
                LayerSettingsNoticeKind.AppRuleRemoved, rule.ProcessMatch, rule.LayerIndex));
        }

        return PerProfile.SetOrRemove(source, profileId, kept.Count > 0, kept);
    }

    /// <summary>
    /// Moves <paramref name="profileId"/>'s mouse layer to its new index; when
    /// the layer was deleted the index is cleared (engine settings otherwise
    /// kept) with a <see cref="LayerSettingsNoticeKind.MouseLayerCleared"/>
    /// notice. A missing entry, a null index, or an index outside the map is
    /// left alone.
    /// </summary>
    private static Dictionary<string, MouseLayerSettings> RemapMouseLayer(
        Dictionary<string, MouseLayerSettings> source, string profileId,
        IReadOnlyList<int?> map, List<LayerSettingsNotice> notices)
    {
        if (!source.TryGetValue(profileId, out var settings)) return source;
        if (settings.MouseLayerIndex is not int oldIndex || IsOutsideMap(oldIndex, map)) return source;

        if (map[oldIndex] is int target)
            return PerProfile.Set(source, profileId, settings with { MouseLayerIndex = target });

        notices.Add(new LayerSettingsNotice(LayerSettingsNoticeKind.MouseLayerCleared, "", oldIndex));
        return PerProfile.Set(source, profileId, settings with { MouseLayerIndex = null });
    }

    /// <summary>
    /// Pipelines are global and never rewritten, so a keyboard-layer step for
    /// the visualized keyboard (<see cref="PipelineStep.TargetDeviceKey"/>
    /// empty) whose layer moved or vanished only earns a
    /// <see cref="LayerSettingsNoticeKind.PipelineStepCheck"/> notice — at
    /// most once per (pipeline name, old index), in pipeline then step order.
    /// </summary>
    private static void CollectPipelineNotices(
        List<ActionPipeline> pipelines, IReadOnlyList<int?> map, List<LayerSettingsNotice> notices)
    {
        var reported = new HashSet<(string PipelineName, int OldIndex)>();
        foreach (var pipeline in pipelines)
            foreach (var step in pipeline.Steps)
            {
                if (step.Kind != PipelineStepKind.KeyboardLayer) continue;
                if (!string.IsNullOrEmpty(step.TargetDeviceKey)) continue;
                if (step.LayerIndex is not int oldIndex || IsOutsideMap(oldIndex, map)) continue;
                if (map[oldIndex] == oldIndex) continue;

                if (reported.Add((pipeline.Name, oldIndex)))
                    notices.Add(new LayerSettingsNotice(
                        LayerSettingsNoticeKind.PipelineStepCheck, pipeline.Name, oldIndex));
            }
    }
}
