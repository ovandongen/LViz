using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

public class BoardSurfaceViewModelTests
{
    private static BoardSurfaceViewModel Create(
        IKeyboardProfile profile, Func<KeyPosition, KeyViewModel>? keyFactory = null)
    {
        var style = new BoardStyleViewModel("#FFD60A");
        return new BoardSurfaceViewModel(
            profile, new BoardLayoutViewModel(profile), style,
            keys => new ComboLegendViewModel(keys, style, overlayVisible: false, profileId: () => "",
                modifierStyle: () => ModifierStyle.Mac),
            keyFactory);
    }

    [Fact]
    public void Constructor_BuildsKeysAndSplitsThemByHand()
    {
        var profile = new CorneProfile();
        var board = Create(profile);

        Assert.Equal(profile.Keys.Count, board.Keys.Count);
        Assert.Equal(profile.Keys.Count(k => k.Hand == Hand.Left), board.LeftKeys.Count);
        Assert.Equal(profile.Keys.Count(k => k.Hand == Hand.Right), board.RightKeys.Count);
        Assert.All(board.LeftKeys, k => Assert.Equal(Hand.Left, k.Position.Hand));
        Assert.All(board.RightKeys, k => Assert.Equal(Hand.Right, k.Position.Hand));
        Assert.Equal(Enumerable.Range(0, board.Keys.Count), board.Keys.Select(k => k.Position.Index));
    }

    [Fact]
    public void SetProfile_RebuildsInPlaceAndMovesTheCanvas()
    {
        var board = Create(new CorneProfile());
        var keys = board.Keys;
        var legendKeys = board.ComboLegend;
        var old = board.Keys[0];
        var other = new Glove80Profile();

        board.SetProfile(other);

        Assert.Same(keys, board.Keys);
        Assert.Same(legendKeys, board.ComboLegend);
        Assert.Equal(other.Keys.Count, board.Keys.Count);
        Assert.Equal(other.Keys.Count, board.LeftKeys.Count + board.RightKeys.Count);
        Assert.NotSame(old, board.Keys[0]);
        Assert.Equal(other.CanvasWidth, board.BoardLayout.CanvasWidth);
    }

    [Fact]
    public void KeyFactory_StylesEveryKeyOnBuildAndRebuild()
    {
        var board = Create(new CorneProfile(), pos => new KeyViewModel(pos) { KeyFillColor = "#123456" });
        Assert.All(board.Keys, k => Assert.Equal("#123456", k.KeyFillColor));

        board.SetProfile(new Glove80Profile());
        Assert.All(board.Keys, k => Assert.Equal("#123456", k.KeyFillColor));
    }

    [Fact]
    public void Callbacks_StartNullAndAreSetByTheHost()
    {
        var board = Create(new CorneProfile());
        Assert.Null(board.OnKeyTapped);
        Assert.Null(board.OnKeyDoubleTapped);
        Assert.Null(board.OnKeyRightTapped);

        var tapped = -1;
        board.OnKeyTapped = i => tapped = i;
        board.OnKeyTapped(3);
        Assert.Equal(3, tapped);
    }

    // ---- Paint ------------------------------------------------------------------

    /// <summary>2 layers, 3 keys — the shapes that separate fall-through from
    /// literal rendering: Lower's key 0 <c>&amp;trans</c> (over Base's
    /// <c>&amp;kp Q</c>), key 1 <c>&amp;none</c>, key 2 <c>&amp;trans</c>
    /// (over Base's <c>&amp;kp A</c>).</summary>
    private const string KeymapText = """
        / {
            keymap {
                compatible = "zmk,keymap";
                base  { display-name = "Base";  bindings = <&kp Q &kp W &kp A>; };
                lower { display-name = "Lower"; bindings = <&trans &none &trans>; };
            };
        };
        """;

    private static LoadedKeymap SampleKeymap() =>
        new(ZmkKeymapLoader.LoadFromText(KeymapText, "test"));

    /// <summary>A fresh edit over <see cref="KeymapText"/>; no test saves, so
    /// the backup root is never created (the store only writes on the first
    /// backup).</summary>
    private static KeymapEditViewModel NewEdit(IKeyboardProfile profile) =>
        new(
            new KeymapEditSource("x.keymap", "x.keymap", KeymapText, "sha", null),
            ZmkKeymapLoader.LoadFromText(KeymapText, "editor"),
            gitHub: null,
            new KeymapBackupStore(Path.Combine(Path.GetTempPath(), $"lviz-boardpaint-backups-{Guid.NewGuid():N}")),
            new FakeKeymapEditDialogs(),
            profile);

    [Fact]
    public void Paint_NoEdit_ResolvesTransparentFromTheLayerBelow()
    {
        var profile = new CorneProfile();
        var board = Create(profile);
        var paint = new KeyPaintContext(profile.Id, new UserSettings());

        board.Paint(SampleKeymap(), layerIndex: 1, edit: null, paint: paint);

        // Lower's &trans keys fall through to Base; &none paints as itself.
        Assert.Equal("Q", board.Keys[0].Label);
        Assert.Equal("A", board.Keys[2].Label);
        Assert.Equal("", board.Keys[1].Label);
    }

    [Fact]
    public void Paint_NoEdit_ClearsEditMarks()
    {
        var profile = new CorneProfile();
        var board = Create(profile);
        var paint = new KeyPaintContext(profile.Id, new UserSettings());
        board.Keys[3].IsEditChanged = true;
        board.Keys[4].IsEditSelected = true;

        board.Paint(SampleKeymap(), layerIndex: 0, edit: null, paint: paint);

        Assert.False(board.Keys[3].IsEditChanged);
        Assert.False(board.Keys[4].IsEditSelected);
    }

    [Fact]
    public void Paint_Edit_ShowsTheLayersOwnTransAndNone()
    {
        var profile = new CorneProfile();
        var board = Create(profile);
        var edit = NewEdit(profile);
        var paint = new KeyPaintContext(profile.Id, new UserSettings());

        board.Paint(edit.Keymap, layerIndex: 1, edit, paint);

        // The edit-mode literals, painted directly the way edit mode paints
        // them, are the reference — no hardcoded glyphs.
        var transRef = new KeyViewModel(board.Keys[0].Position);
        transRef.ApplyBinding(edit.Keymap.Layers[1].Bindings[0], activeLayerIndex: 1,
            targetLayer: null, targetLayerName: null, paint: paint, ignoreLabelOverride: true);
        var noneRef = new KeyViewModel(board.Keys[1].Position);
        noneRef.ApplyBinding(edit.Keymap.Layers[1].Bindings[1], activeLayerIndex: 1,
            targetLayer: null, targetLayerName: null, paint: paint, ignoreLabelOverride: true);

        Assert.Equal(transRef.Label, board.Keys[0].Label);
        Assert.Equal(transRef.Subscript, board.Keys[0].Subscript);
        Assert.Equal(noneRef.Label, board.Keys[1].Label);
        Assert.Equal(noneRef.Subscript, board.Keys[1].Subscript);
        // The layer's OWN binding, not the Base fall-through result.
        Assert.NotEqual("Q", board.Keys[0].Label);
        Assert.NotEqual("W", board.Keys[1].Label);
        // Keys past the binding list paint transparent.
        Assert.Equal(transRef.Label, board.Keys[3].Label);
    }

    [Fact]
    public void Paint_Edit_IgnoresLabelOverrides()
    {
        var profile = new CorneProfile();
        var settings = new UserSettings
        {
            KeyLabelOverrides = new()
            {
                [profile.Id] = new() { [1] = new() { [0] = new KeyLabelOverride { MainLabel = "OVERRIDE" } } },
            },
        };
        var paint = new KeyPaintContext(profile.Id, settings);
        var board = Create(profile);
        var edit = NewEdit(profile);

        board.Paint(edit.Keymap, layerIndex: 1, edit, paint);

        // The override sits exactly on the painted slot (layer 1, key 0) —
        // edit mode must skip it so the key shows what a save will write.
        Assert.NotEqual("OVERRIDE", board.Keys[0].Label);

        board.Paint(SampleKeymap(), layerIndex: 1, edit: null, paint: paint);

        Assert.Equal("OVERRIDE", board.Keys[0].Label);
    }
}
