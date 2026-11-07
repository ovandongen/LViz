using LViz.App.ViewModels;
using LViz.Core.Layout;
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
            keys => new ComboLegendViewModel(keys, style, overlayVisible: false, profileId: () => ""),
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
}
