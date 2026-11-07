using Avalonia;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Ring;

/// <summary>
/// <see cref="RingLayout"/> without a window: slot building from the
/// view-model, the open band, the crumb row and the hit-testing over them.
/// Text widths come from a fixed fake, so only the geometry is under test.
/// </summary>
public class RingLayoutTests
{
    // Key 0 is &kp A (summary ring); key 1 is &bt BT_SEL 0 (a command with profiles).
    private const string Keymap = """
        / {
            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &kp A &bt BT_SEL 0 &trans &none >; };
                Nav { bindings = < &trans &trans &trans &trans >; };
            };
        };
        """;

    private sealed class FixedMeasure : IRingTextMeasure
    {
        public double BandItemTextWidth(KeyRingSegment segment) => 7 * segment.Label.Length;

        public double CrumbTextWidth(KeyRingCrumb crumb) => 7 * crumb.Text.Length;
    }

    private static readonly IRingTextMeasure Measure = new FixedMeasure();

    private static KeyRingViewModel BuildRing(int keyIndex)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }

    private static Point At(double radius, double angle)
    {
        var p = RingGeometry.Polar(radius, angle);
        return new Point(RingLayout.Centre.X + p.X, RingLayout.Centre.Y + p.Y);
    }

    [Fact]
    public void Build_MainRing_HasOneMainSlotPerSegmentAndNoBand()
    {
        var ring = BuildRing(0);

        var layout = RingLayout.Build(ring, bandOwnerKey: null, Measure);

        Assert.Equal(ring.Segments.Count, layout.Slots.Count(slot => slot.Layer == RingLayer.Main));
        Assert.DoesNotContain(layout.Slots, slot => slot.Layer == RingLayer.Band);
        Assert.Empty(layout.BandSegments);
        Assert.Null(layout.BandOwnerKey);
    }

    [Fact]
    public void HitTest_CentreMainRingAndOutside()
    {
        var layout = RingLayout.Build(BuildRing(0), null, Measure);

        Assert.IsType<RingHit.Center>(layout.HitTest(RingLayout.Centre));
        Assert.Equal("cat:Key", layout.HitTest(At(110, 0))?.Key);
        Assert.Null(layout.HitTest(new Point(5, 500)));
    }

    [Fact]
    public void Build_OwnerWithABand_OpensItAndHitsItsItems()
    {
        var ring = BuildRing(0);

        var layout = RingLayout.Build(ring, "cat:Layer", Measure);

        Assert.Equal("cat:Layer", layout.BandOwnerKey);
        Assert.NotEmpty(layout.BandSegments);
        Assert.Equal(layout.BandSegments.Count, layout.Slots.Count(slot => slot.Layer == RingLayer.Band));

        var item = layout.BandItemCentres()[0];
        Assert.Equal(item.Id, layout.HitTest(item.Centre)?.Key);
        Assert.IsType<RingHit.Segment>(layout.Find(item.Id));
    }

    [Fact]
    public void Build_OwnerWithoutABandOrNotOnTheRing_LeavesTheBandClosed()
    {
        var ring = BuildRing(0);

        // Bluetooth has the single behavior &bt: chosen on the category itself.
        Assert.Null(RingLayout.Build(ring, "cat:Bluetooth", Measure).BandOwnerKey);
        Assert.Null(RingLayout.Build(ring, "cat:Nope", Measure).BandOwnerKey);
    }

    [Fact]
    public void Build_ProfileBand_KeepsTheProfilesInOrder()
    {
        var ring = BuildRing(1);
        ring.GoToCrumb(2); // the command step of &bt BT_SEL 0

        var layout = RingLayout.Build(ring, "cmd:BT_SEL", Measure);

        var band = layout.Slots.Where(slot => slot.Layer == RingLayer.Band).ToArray();
        Assert.True(band.Length > 1);
        Assert.All(band, slot => Assert.IsType<KeyRingTarget.Profile>(slot.Segment.Target));
        for (var i = 1; i < band.Length; i++)
        {
            Assert.True(band[i].Start > band[i - 1].Start, "profile numbers run in order, clockwise");
        }
    }

    [Fact]
    public void Build_Crumbs_AreCentredOnTheRingAndInOrder()
    {
        var ring = BuildRing(0);

        var layout = RingLayout.Build(ring, null, Measure);

        Assert.Equal(ring.Crumbs.Count, layout.CrumbSlots.Count);
        var first = layout.CrumbSlots[0].Bounds;
        var last = layout.CrumbSlots[^1].Bounds;
        Assert.Equal(2 * RingLayout.Centre.X, first.Left + last.Right, precision: 6);
        for (var i = 1; i < layout.CrumbSlots.Count; i++)
        {
            Assert.True(layout.CrumbSlots[i].Bounds.Left > layout.CrumbSlots[i - 1].Bounds.Right);
        }

        Assert.Equal("crumb:1", layout.HitTest(layout.CrumbSlots[1].Bounds.Center)?.Key);
    }

    [Fact]
    public void IsOverRing_CoversTheExtentCrumbsAndTheHintOnly()
    {
        var layout = RingLayout.Build(BuildRing(0), null, Measure);
        var hint = new Rect(10, 590, 80, 22);

        Assert.True(layout.IsOverRing(RingLayout.Centre, hint: null));
        Assert.True(layout.IsOverRing(layout.CrumbSlots[0].Bounds.Center, hint: null));
        Assert.False(layout.IsOverRing(hint.Center, hint: null));
        Assert.True(layout.IsOverRing(hint.Center, hint));
        Assert.False(layout.IsOverRing(new Point(5, 700), hint));
    }

    [Fact]
    public void Find_NamesTheCentreCrumbsAndSlots_AndNothingElse()
    {
        var layout = RingLayout.Build(BuildRing(0), null, Measure);

        Assert.IsType<RingHit.Center>(layout.Find("center"));
        Assert.IsType<RingHit.Crumb>(layout.Find("crumb:0"));
        Assert.Null(layout.Find("crumb:99"));
        Assert.IsType<RingHit.Segment>(layout.Find("cat:Key"));
        Assert.Null(layout.Find("cat:Missing"));
        Assert.NotNull(layout.MainSlot("cat:Key"));
        Assert.Null(layout.MainSlot("center"));
    }

    [Fact]
    public void Empty_HitsAndHoldsNothing()
    {
        var layout = RingLayout.Empty;

        Assert.Null(layout.HitTest(RingLayout.Centre));
        Assert.False(layout.IsOverRing(RingLayout.Centre, hint: null));
        Assert.Null(layout.Find("center"));
        Assert.Empty(layout.Slots);
    }

    [Fact]
    public void LabelSize_CodeSmall_ProfileAndLayerStepBig()
    {
        var plain = new KeyRingSegment(new KeyRingTarget.Layer(0), "Base", null,
            LabelIsCode: false, SubIsCode: false, Icon: null, IsCurrent: false);
        var code = plain with { LabelIsCode = true };

        Assert.Equal(13, RingLayout.LabelSize(plain, layerStep: false));
        Assert.Equal(15, RingLayout.LabelSize(plain, layerStep: true));
        Assert.Equal(12, RingLayout.LabelSize(code, layerStep: true));
    }
}
