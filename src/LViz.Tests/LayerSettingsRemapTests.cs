using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Rewriting the layer-number-keyed settings of one profile after layers
/// moved, were deleted, or were added at the end: colours/hidden flags/label
/// overrides/rules/mouse layer travel with their layer, deleted targets drop
/// silently or are reported, global pipelines are only checked, and the input
/// settings instance is never mutated.
/// </summary>
public class LayerSettingsRemapTests
{
    private const string Profile = "Corne";
    private const string Other = "Glove80";

    private static readonly KeyLabelOverride Label = new() { MainLabel = "X" };

    /// <summary>Both profiles hold the same kinds of entries on layers 0–2.</summary>
    private static UserSettings Input() => new()
    {
        LayerColors = new()
        {
            [Profile] = new() { [0] = "#000000", [1] = "#111111", [2] = "#222222" },
            [Other] = new() { [0] = "#AAAAAA", [1] = "#BBBBBB", [2] = "#CCCCCC" },
        },
        HiddenLayers = new()
        {
            [Profile] = new() { 1, 2 },
            [Other] = new() { 1, 2 },
        },
        KeyLabelOverrides = new()
        {
            [Profile] = new() { [1] = new() { [3] = Label } },
            [Other] = new() { [1] = new() { [3] = Label } },
        },
        AppLayerRules = new()
        {
            [Profile] = new() { new AppLayerRule("code", 2) },
            [Other] = new() { new AppLayerRule("code", 2) },
        },
        MouseLayer = new()
        {
            [Profile] = new MouseLayerSettings(Enabled: true, MouseLayerIndex: 2),
            [Other] = new MouseLayerSettings(Enabled: true, MouseLayerIndex: 2),
        },
    };

    private static UserSettings SettingsWithPipeline(params PipelineStep[] steps) => new()
    {
        ActionPipelines = new() { new ActionPipeline("focus", new List<PipelineStep>(steps)) },
    };

    [Fact]
    public void Apply_MovedLayers_SettingsTravelWithTheirLayer()
    {
        var input = Input();

        // Map under test: 0 -> 2, 1 -> 0, 2 -> 1 (layer 0 moved to the end).
        var result = LayerSettingsRemap.Apply(input, Profile, [2, 0, 1]);

        var colors = result.Settings.LayerColors[Profile];
        Assert.Equal(3, colors.Count);
        Assert.Equal("#111111", colors[0]);
        Assert.Equal("#222222", colors[1]);
        Assert.Equal("#000000", colors[2]);

        // Old 1 becomes new 0 and is dropped (base is never hidden); old 2 becomes 1.
        Assert.Equal(new List<int> { 1 }, result.Settings.HiddenLayers[Profile]);

        // Key labels move on the outer (layer) key only; the key map is as it was.
        Assert.Equal(Label, result.Settings.KeyLabelOverrides[Profile][0][3]);

        Assert.Equal([new AppLayerRule("code", 1)], result.Settings.AppLayerRules[Profile]);

        var mouse = result.Settings.MouseLayer[Profile];
        Assert.True(mouse.Enabled);
        Assert.Equal(1, mouse.MouseLayerIndex!.Value);

        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Apply_MovedLayers_OtherProfileIsUntouched()
    {
        var input = Input();

        var result = LayerSettingsRemap.Apply(input, Profile, [2, 0, 1]);

        Assert.Equal(input.LayerColors[Other], result.Settings.LayerColors[Other]);
        Assert.Equal(input.HiddenLayers[Other], result.Settings.HiddenLayers[Other]);
        Assert.Equal(input.KeyLabelOverrides[Other], result.Settings.KeyLabelOverrides[Other]);
        Assert.Equal(input.AppLayerRules[Other], result.Settings.AppLayerRules[Other]);
        Assert.Equal(input.MouseLayer[Other], result.Settings.MouseLayer[Other]);
    }

    [Fact]
    public void Apply_DeletedLayer_DropsSilentlyRemovesRuleAndClearsMouse_WithNotices()
    {
        var input = new UserSettings
        {
            LayerColors = new()
            {
                [Profile] = new() { [1] = "#111111", [2] = "#222222" },
                [Other] = new() { [1] = "#BBBBBB" },
            },
            HiddenLayers = new()
            {
                [Profile] = new() { 1 },
                [Other] = new() { 1 },
            },
            AppLayerRules = new()
            {
                [Profile] = new() { new AppLayerRule("code", 1), new AppLayerRule("slack", 2) },
                [Other] = new() { new AppLayerRule("code", 1) },
            },
            MouseLayer = new()
            {
                [Profile] = new MouseLayerSettings(Enabled: true, MouseLayerIndex: 1),
                [Other] = new MouseLayerSettings(Enabled: true, MouseLayerIndex: 1),
            },
        };

        // Map under test: 0 stays, 1 is deleted, 2 -> 1.
        var result = LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        // Colour of deleted layer 1 dropped silently; layer 2 moved to 1.
        Assert.Equal(new Dictionary<int, string> { [1] = "#222222" }, result.Settings.LayerColors[Profile]);

        // The only hidden layer was deleted → the profile entry is gone.
        Assert.False(result.Settings.HiddenLayers.ContainsKey(Profile));

        // ("code", 1) removed with a notice; ("slack", 2) moved to 1.
        Assert.Equal([new AppLayerRule("slack", 1)], result.Settings.AppLayerRules[Profile]);

        // Mouse layer cleared, engine settings otherwise kept.
        var mouse = result.Settings.MouseLayer[Profile];
        Assert.Null(mouse.MouseLayerIndex);
        Assert.True(mouse.Enabled);

        // Notices ordered: app rules first, then the mouse layer.
        Assert.Equal(2, result.Notices.Count);
        Assert.Equal(new LayerSettingsNotice(LayerSettingsNoticeKind.AppRuleRemoved, "code", 1), result.Notices[0]);
        Assert.Equal(new LayerSettingsNotice(LayerSettingsNoticeKind.MouseLayerCleared, "", 1), result.Notices[1]);

        // The other profile keeps its deleted-layer entries untouched.
        Assert.Equal(input.LayerColors[Other], result.Settings.LayerColors[Other]);
        Assert.Equal(input.HiddenLayers[Other], result.Settings.HiddenLayers[Other]);
        Assert.Equal(input.AppLayerRules[Other], result.Settings.AppLayerRules[Other]);
        Assert.Equal(input.MouseLayer[Other], result.Settings.MouseLayer[Other]);
    }

    [Fact]
    public void Apply_AddAtEnd_EverythingStaysPutAndNothingIsReported()
    {
        var input = Input();

        // 3-layer keymap that now has 4: the new layer takes index 3, nothing moved.
        var result = LayerSettingsRemap.Apply(input, Profile, [0, 1, 2]);

        Assert.Equal(input.LayerColors, result.Settings.LayerColors);
        Assert.Equal(input.HiddenLayers, result.Settings.HiddenLayers);
        Assert.Equal(input.KeyLabelOverrides, result.Settings.KeyLabelOverrides);
        Assert.Equal(input.AppLayerRules, result.Settings.AppLayerRules);
        Assert.Equal(input.MouseLayer, result.Settings.MouseLayer);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Apply_IndexOutsideMap_KeepsItsSetting()
    {
        var input = new UserSettings
        {
            LayerColors = new() { [Profile] = new() { [7] = "#777777" } },
        };

        var result = LayerSettingsRemap.Apply(input, Profile, [0, 1, 2]);

        Assert.Equal(new Dictionary<int, string> { [7] = "#777777" }, result.Settings.LayerColors[Profile]);
    }

    [Fact]
    public void Apply_MappedEntryWinsOverOutsideEntry()
    {
        var input = new UserSettings
        {
            LayerColors = new() { [Profile] = new() { [1] = "#111111", [2] = "#222222" } },
        };

        // Old layer 1 maps onto 2, which the map itself doesn't cover.
        var result = LayerSettingsRemap.Apply(input, Profile, [0, 2]);

        Assert.Equal(new Dictionary<int, string> { [2] = "#111111" }, result.Settings.LayerColors[Profile]);
    }

    [Fact]
    public void Apply_PipelineStepOnChangedLayer_ReportsCheckAndKeepsPipelines()
    {
        var input = SettingsWithPipeline(
            new PipelineStep(PipelineStepKind.KeyboardLayer, "", LayerIndex: 1));

        var result = LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        var notice = Assert.Single(result.Notices);
        Assert.Equal(LayerSettingsNoticeKind.PipelineStepCheck, notice.Kind);
        Assert.Equal("focus", notice.Name);
        Assert.Equal(1, notice.OldLayerIndex);
        Assert.Equal(input.ActionPipelines, result.Settings.ActionPipelines);
    }

    [Fact]
    public void Apply_PipelineStepOnUnchangedLayer_NoNotice()
    {
        var input = SettingsWithPipeline(
            new PipelineStep(PipelineStepKind.KeyboardLayer, "", LayerIndex: 0));

        var result = LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Apply_PipelineStepTargetingBusDevice_NoNotice()
    {
        var input = SettingsWithPipeline(
            new PipelineStep(PipelineStepKind.KeyboardLayer, "bean", LayerIndex: 1));

        var result = LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Apply_TwoPipelineStepsOnSameLayer_ReportsOnce()
    {
        var input = SettingsWithPipeline(
            new PipelineStep(PipelineStepKind.KeyboardLayer, "", LayerIndex: 1),
            new PipelineStep(PipelineStepKind.Delay, DelayMs: 10),
            new PipelineStep(PipelineStepKind.KeyboardLayer, "", LayerIndex: 1));

        var result = LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        var notice = Assert.Single(result.Notices);
        Assert.Equal(LayerSettingsNoticeKind.PipelineStepCheck, notice.Kind);
        Assert.Equal(1, notice.OldLayerIndex);
    }

    [Fact]
    public void Apply_LeavesInputSettingsUnchanged()
    {
        var input = new UserSettings
        {
            LayerColors = new()
            {
                [Profile] = new() { [1] = "#111111", [2] = "#222222" },
                [Other] = new() { [1] = "#BBBBBB" },
            },
            HiddenLayers = new()
            {
                [Profile] = new() { 1, 2 },
                [Other] = new() { 1 },
            },
            KeyLabelOverrides = new()
            {
                [Profile] = new() { [1] = new() { [3] = Label } },
                [Other] = new() { [1] = new() { [3] = Label } },
            },
            AppLayerRules = new()
            {
                [Profile] = new() { new AppLayerRule("code", 1), new AppLayerRule("slack", 2) },
                [Other] = new() { new AppLayerRule("code", 1) },
            },
            MouseLayer = new()
            {
                [Profile] = new MouseLayerSettings(Enabled: true, MouseLayerIndex: 1),
            },
            ActionPipelines = new()
            {
                new ActionPipeline("focus", new List<PipelineStep>
                {
                    new(PipelineStepKind.KeyboardLayer, "", LayerIndex: 1),
                }),
            },
        };

        // Deep-enough copies so a mutation of an inner dictionary or list shows up.
        var colors = input.LayerColors.ToDictionary(e => e.Key, e => new Dictionary<int, string>(e.Value));
        var hidden = input.HiddenLayers.ToDictionary(e => e.Key, e => new List<int>(e.Value));
        var labels = input.KeyLabelOverrides.ToDictionary(
            e => e.Key, e => e.Value.ToDictionary(e2 => e2.Key, e2 => new Dictionary<int, KeyLabelOverride>(e2.Value)));
        var rules = input.AppLayerRules.ToDictionary(e => e.Key, e => new List<AppLayerRule>(e.Value));
        var mouse = new Dictionary<string, MouseLayerSettings>(input.MouseLayer);
        var pipelines = new List<ActionPipeline>(input.ActionPipelines);

        LayerSettingsRemap.Apply(input, Profile, [0, null, 1]);

        Assert.Equal(colors, input.LayerColors);
        Assert.Equal(hidden, input.HiddenLayers);
        Assert.Equal(labels, input.KeyLabelOverrides);
        Assert.Equal(rules, input.AppLayerRules);
        Assert.Equal(mouse, input.MouseLayer);
        Assert.Equal(pipelines, input.ActionPipelines);
    }

    [Fact]
    public void Apply_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LayerSettingsRemap.Apply(null!, Profile, [0]));
    }

    [Fact]
    public void Apply_NullMap_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LayerSettingsRemap.Apply(new UserSettings(), Profile, null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankProfileId_Throws(string? profileId)
    {
        Assert.Throws<ArgumentException>(() => LayerSettingsRemap.Apply(new UserSettings(), profileId!, [0]));
    }
}
