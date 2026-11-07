using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using LViz.App.Ring;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;
using Projektanker.Icons.Avalonia;

namespace LViz.App.Views;

/// <summary>
/// Paints one frame of the key ring from a <see cref="KeyRingViewModel"/>, the
/// <see cref="RingLayout"/> built from it and the control's view state (hover
/// amounts, band fade, pointed segment). Numbers follow the approved visual
/// reference (docs/ideas/lviz-key-ring-reference.html). It also supplies the
/// text measuring the layout needs, so layout and painting agree on widths.
/// </summary>
internal sealed class KeyRingPainter
{
    // ─── colours ─────────────────────────────────────────────────────────

    private static readonly SolidColorBrush BackdropBrush = Tint("#181825");
    private static readonly SolidColorBrush SegmentBrush = Tint("#313244");
    private static readonly SolidColorBrush SegmentHoverBrush = Tint("#45475A");
    private static readonly SolidColorBrush BandSegmentBrush = Tint("#2B2C3F");
    private static readonly SolidColorBrush ToggledOnBrush = Tint("#5A4A2E");
    private static readonly SolidColorBrush ToggledOnHoverBrush = Tint("#6A5734");
    private static readonly SolidColorBrush DisabledBrush = Tint("#282939");
    private static readonly SolidColorBrush CenterBrush = Tint("#1E1E2E");
    private static readonly SolidColorBrush CenterStrokeBrush = Tint("#45475A");
    private static readonly SolidColorBrush CenterChangedBrush = Tint("#2C2822");
    private static readonly SolidColorBrush GoldBrush = Tint(AppTheme.AccentGoldHex);
    private static readonly SolidColorBrush GoldSoftBrush = Tint(AppTheme.AccentGoldSoftHex);
    private static readonly SolidColorBrush TextBrush = Tint("#CDD6F4");
    private static readonly SolidColorBrush SubBrush = Tint("#A6ADC8");
    private static readonly SolidColorBrush MutedBrush = Tint("#6C7086");
    private static readonly SolidColorBrush HintBrush = Tint("#EB181825");

    // ─── typefaces ───────────────────────────────────────────────────────

    private static readonly FontFamily MonoFamily = new("Menlo, Consolas, monospace");

    /// <summary>The app's default family (Inter, see Program.cs). A bare
    /// <c>new Typeface()</c> has no family and FontManager throws on it.</summary>
    private static readonly Typeface InterRegular = New(FontFamily.Default, FontWeight.Normal);
    private static readonly Typeface InterMedium = New(FontFamily.Default, FontWeight.Medium);
    private static readonly Typeface InterSemiBold = New(FontFamily.Default, FontWeight.SemiBold);
    private static readonly Typeface MonoRegular = New(MonoFamily, FontWeight.Normal);
    private static readonly Typeface MonoMedium = New(MonoFamily, FontWeight.Medium);
    private static readonly Typeface MonoSemiBold = New(MonoFamily, FontWeight.SemiBold);
    private static readonly Typeface MonoBold = New(MonoFamily, FontWeight.Bold);

    private static readonly Dictionary<string, IconEntry?> IconCache = [];


    private readonly KeyRingViewModel _vm;
    private readonly RingLayout _layout;
    private readonly IReadOnlyDictionary<string, double> _amounts;
    private readonly KeyRingSegment? _pointed;
    private readonly double _bandOpacity;
    private readonly bool _hideHint;

    /// <param name="amounts">Hover amount (0 to 1) per segment key; absent is 0.</param>
    /// <param name="pointed">The segment the centre describes, or null.</param>
    /// <param name="bandOpacity">Fade-in of the open band, 0 to 1.</param>
    public KeyRingPainter(
        KeyRingViewModel vm, RingLayout layout, IReadOnlyDictionary<string, double> amounts,
        KeyRingSegment? pointed, double bandOpacity, bool hideHint)
    {
        _vm = vm;
        _layout = layout;
        _amounts = amounts;
        _pointed = pointed;
        _bandOpacity = bandOpacity;
        _hideHint = hideHint;
    }

    /// <summary>The measuring the layout builds with.</summary>
    public static IRingTextMeasure Measure { get; } = new TextMeasure();

    private sealed class TextMeasure : IRingTextMeasure
    {
        public double BandItemTextWidth(KeyRingSegment segment)
        {
            var textWidth = MeasureTextWidth(
                segment.Label,
                segment.LabelIsCode ? MonoSemiBold : InterSemiBold,
                RingLayout.LabelSize(segment, layerStep: false));

            if (segment.Sub is { Length: > 0 } sub)
            {
                textWidth = Math.Max(textWidth,
                    MeasureTextWidth(sub, segment.SubIsCode ? MonoRegular : InterRegular, 11));
            }

            return textWidth;
        }

        public double CrumbTextWidth(KeyRingCrumb crumb) =>
            MeasureTextWidth(crumb.Text, crumb.IsCode ? MonoMedium : InterMedium, 12);
    }

    /// <summary>The hint pill's rectangle in control coordinates, or null
    /// while it is hidden or has no text.</summary>
    public static Rect? HintBounds(KeyRingViewModel vm, bool hideHint)
    {
        if (hideHint || vm.Hint.Length == 0) return null;

        var width = MeasureTextWidth(vm.Hint, InterRegular, 11) + 2 * 12;
        return new Rect(
            RingLayout.Centre.X - width / 2,
            RingLayout.Centre.Y + RingMetrics.ExtentRadius + RingMetrics.HintGap,
            width, RingMetrics.HintHeight);
    }

    private double AmountOf(string key) => _amounts.TryGetValue(key, out var value) ? value : 0;

    /// <summary>Paints the whole ring: backdrop, band, sectors with their labels
    /// and current markers, centre, crumbs and hint.</summary>
    public void Paint(DrawingContext context)
    {
        var vm = _vm;
        using (context.PushTransform(Matrix.CreateTranslation(RingLayout.Centre.X, RingLayout.Centre.Y)))
        {
            var bandOpen = _layout.BandSegments.Count > 0;

            // 1. backdrop disc
            context.DrawEllipse(BackdropBrush, null, default,
                RingMetrics.BackdropRadius, RingMetrics.BackdropRadius);

            // 2. band backdrop, then 3–5. segments, labels and markers. The
            // band draws at its fade-in opacity; nothing shares a radius with
            // it, so keeping its passes together inside the scope preserves
            // the painting order.
            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    DrawBandBackdrop(context);
                }
            }

            var layerStep = vm.StepKind == KeyRingStepKind.Layer;

            foreach (var slot in _layout.Slots)
            {
                if (slot.Layer != RingLayer.Band) DrawSegment(context, slot);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _layout.Slots)
                    {
                        if (slot.Layer == RingLayer.Band) DrawSegment(context, slot);
                    }
                }
            }

            foreach (var slot in _layout.Slots)
            {
                if (slot.Layer != RingLayer.Band) DrawSegmentLabel(context, slot, layerStep);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _layout.Slots)
                    {
                        if (slot.Layer == RingLayer.Band) DrawSegmentLabel(context, slot, layerStep);
                    }
                }
            }

            foreach (var slot in _layout.Slots)
            {
                if (slot.Layer != RingLayer.Band) DrawCurrentMarker(context, slot);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _layout.Slots)
                    {
                        if (slot.Layer == RingLayer.Band) DrawCurrentMarker(context, slot);
                    }
                }
            }

            // 6. centre
            DrawCentre(context, vm);

            // 7. crumbs (the row runs across the top of the frame)
            DrawCrumbs(context);

            // 8. hint
            DrawHint(context, vm);
        }
    }

    private void DrawBandBackdrop(DrawingContext context)
    {
        var pad = RingMetrics.BandBackdropPad /
                  ((RingMetrics.BandBackdropInner + RingMetrics.BandBackdropOuter) / 2);

        var start = double.MaxValue;
        var end = double.MinValue;
        foreach (var slot in _layout.Slots)
        {
            if (slot.Layer != RingLayer.Band) continue;
            start = Math.Min(start, slot.Start);
            end = Math.Max(end, slot.End);
        }

        if (start > end) return;

        var geometry = SectorGeometry(RingMetrics.BandBackdropInner, RingMetrics.BandBackdropOuter,
            start - pad, end + pad, gap: 0, corner: RingMetrics.BackdropCorner);
        context.DrawGeometry(BackdropBrush, null, geometry);
    }

    private void DrawSegment(DrawingContext context, RingSlot slot)
    {
        var segment = slot.Segment;
        var h = AmountOf(segment.Id);

        var fill = !segment.IsEnabled ? DisabledBrush
            : segment.IsOn ? BlendBrush(ToggledOnBrush, ToggledOnHoverBrush, h)
            : slot.Layer == RingLayer.Band ? BlendBrush(BandSegmentBrush, SegmentHoverBrush, h)
            : BlendBrush(SegmentBrush, SegmentHoverBrush, h);

        IPen? pen = h > 0
            ? new Pen(new SolidColorBrush(WithAlpha(GoldBrush.Color, h)), 1.5)
            : null;

        context.DrawGeometry(fill, pen,
            SectorGeometry(slot.Inner, slot.Outer, slot.Start, slot.End, RingMetrics.Gap, RingMetrics.Corner));
    }

    private void DrawSegmentLabel(DrawingContext context, RingSlot slot, bool layerStep)
    {
        var segment = slot.Segment;
        var h = AmountOf(segment.Id);
        var midRadius = (slot.Inner + slot.Outer) / 2;
        var centre = RingGeometry.Polar(midRadius, (slot.Start + slot.End) / 2);
        var maxWidth = Math.Max(8, 2 * midRadius * Math.Sin((slot.End - slot.Start) / 2) - 8);

        var icon = segment.Icon is { Length: > 0 } name ? LoadIcon(name) : null;
        var hasSub = segment.Sub is { Length: > 0 };
        var iconBlock = icon is null ? 0 : 16 + 4;
        var blockHeight = iconBlock + 16 + (hasSub ? 2 + 14 : 0);
        var top = centre.Y - blockHeight / 2;

        if (icon is not null)
        {
            var iconColor = !segment.IsEnabled ? MutedBrush.Color
                : h > 0.5 ? GoldBrush.Color
                : SubBrush.Color;
            DrawIcon(context, icon.Value, centre.X, top + 8, new SolidColorBrush(iconColor));
        }

        var labelColor = !segment.IsEnabled ? MutedBrush.Color
            : h > 0.5 || segment.IsOn ? GoldBrush.Color
            : TextBrush.Color;
        var labelSize = RingLayout.LabelSize(segment, layerStep);
        DrawTextLine(context, segment.Label,
            segment.LabelIsCode ? MonoSemiBold : InterSemiBold, labelSize,
            new SolidColorBrush(labelColor),
            top + iconBlock, 16, centre.X, maxWidth);

        if (!hasSub) return;

        var subColor = !segment.IsEnabled ? MutedBrush.Color : SubBrush.Color;
        DrawTextLine(context, segment.Sub!,
            segment.SubIsCode ? MonoRegular : InterRegular, 11,
            new SolidColorBrush(subColor),
            top + iconBlock + 16 + 2, 14, centre.X, maxWidth);
    }

    private void DrawCurrentMarker(DrawingContext context, RingSlot slot)
    {
        if (!slot.Segment.IsCurrent) return;

        var radius = slot.Outer - 5;
        var mid = (slot.Start + slot.End) / 2;
        var half = Math.Min(0.2 * (slot.End - slot.Start), 14 / radius);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(RingGeometry.Polar(radius, mid - half), isFilled: true);
            ctx.ArcTo(RingGeometry.Polar(radius, mid + half), new Size(radius, radius),
                0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(GoldBrush, 2.5) { LineCap = PenLineCap.Round }, geometry);
    }

    private void DrawCentre(DrawingContext context, KeyRingViewModel vm)
    {
        var changed = vm.CenterState == KeyRingCenterState.Changed;
        var fill = changed ? CenterChangedBrush.Color : CenterBrush.Color;
        if (vm.CanClose && AmountOf(RingHit.Center.CenterKey) > 0)
        {
            fill = Blend(fill, SegmentHoverBrush.Color, 0.4);
        }

        var stroke = changed ? GoldBrush : CenterStrokeBrush;
        var strokeWidth = changed ? 2 : 1;
        context.DrawEllipse(new SolidColorBrush(fill), new Pen(stroke, strokeWidth), default,
            RingMetrics.CenterRadius, RingMetrics.CenterRadius);

        if (vm.StepKind == KeyRingStepKind.Text)
        {
            // The middle belongs to the text box the host puts there; the
            // caption and the line keep their 30 px offsets from the centre.
            var textCaption = LayoutText(vm.CenterCaption.ToUpperInvariant(),
                InterSemiBold, 10, MutedBrush, 104, letterSpacing: 0.6);
            DrawLineBox(context, textCaption, -30 - textCaption.Height / 2, 52);
            textCaption.Dispose();

            var textLine = LayoutText(vm.CenterLine, InterRegular, 11, SubBrush, 104);
            DrawLineBox(context, textLine, 30 - textLine.Height / 2, 52);
            textLine.Dispose();
            return;
        }

        // Pointing at a segment shows its full name and description here:
        // labels in narrow segments are trimmed, the centre has the room.
        var lines = _pointed is { } pointed
            ? PointedLines(pointed)
            : BindingLines(vm, changed);

        var total = 0.0;
        foreach (var (_, height) in lines) total += height;

        var y = -total / 2;
        foreach (var (layout, height) in lines)
        {
            DrawLineBox(context, layout, y + (height - layout.Height) / 2, 52);
            layout.Dispose();
            y += height;
        }
    }

    /// <summary>The centre's text while a segment is pointed at: its label
    /// (gold, like the hovered segment) and its sub, each up to two lines.</summary>
    private static List<(TextLayout Layout, double Height)> PointedLines(KeyRingSegment pointed)
    {
        var label = LayoutText(pointed.Label,
            pointed.LabelIsCode ? MonoSemiBold : InterSemiBold, 13,
            pointed.IsEnabled ? GoldBrush : MutedBrush, 104, maxLines: 2);
        var lines = new List<(TextLayout Layout, double Height)> { (label, label.Height + 2) };

        if (pointed.Sub is { Length: > 0 } sub)
        {
            var subText = LayoutText(sub, pointed.SubIsCode ? MonoRegular : InterRegular, 11,
                SubBrush, 104, maxLines: 2);
            lines.Add((subText, subText.Height));
        }

        return lines;
    }

    /// <summary>The centre's text otherwise: caption, the binding before the
    /// change, the binding, and the line (save / pick / description).</summary>
    private static List<(TextLayout Layout, double Height)> BindingLines(KeyRingViewModel vm, bool changed)
    {
        var lines = new List<(TextLayout Layout, double Height)>();

        if (vm.CenterState is KeyRingCenterState.Choosing or KeyRingCenterState.Unchanged
            && vm.CenterCaption.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterCaption.ToUpperInvariant(), InterSemiBold, 10,
                MutedBrush, 104, letterSpacing: 0.6), 15));
        }

        if (vm.CenterBefore.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterBefore + "  \u2192", MonoRegular, 11,
                MutedBrush, 104), 16));
        }

        lines.Add((LayoutText(vm.CenterBinding, MonoBold, 14,
            changed ? GoldBrush : TextBrush, 104), 20));

        if (vm.CenterLine.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterLine,
                changed ? InterSemiBold : InterRegular, 11,
                changed ? GoldBrush : SubBrush, 104), 16));
        }

        return lines;
    }

    private void DrawCrumbs(DrawingContext context)
    {
        if (_layout.CrumbSlots.Count == 0) return;

        var left = -RingLayout.Centre.X;
        var top = -RingLayout.Centre.Y;

        foreach (var slot in _layout.CrumbSlots)
        {
            var bounds = new Rect(
                slot.Bounds.X + left, slot.Bounds.Y + top,
                slot.Bounds.Width, slot.Bounds.Height);
            var h = AmountOf(new RingHit.Crumb(slot.Index).Key);
            var crumb = slot.Crumb;

            IBrush? fill;
            IPen? pen;
            Color textColor;
            switch (crumb.State)
            {
                case KeyRingCrumbState.Current:
                    fill = GoldSoftBrush;
                    pen = new Pen(GoldBrush, 1);
                    textColor = GoldBrush.Color;
                    break;
                case KeyRingCrumbState.Open:
                    fill = null;
                    pen = new Pen(GoldBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
                    textColor = GoldBrush.Color;
                    break;
                default:
                    fill = BlendBrush(SegmentBrush, SegmentHoverBrush, h);
                    pen = null;
                    textColor = TextBrush.Color;
                    break;
            }

            var radius = RingMetrics.CrumbHeight / 2;
            context.DrawRectangle(fill, pen, bounds, radius, radius);

            var typeface = crumb.IsCode ? MonoMedium : InterMedium;
            using (var text = LayoutText(crumb.Text, typeface, 12, new SolidColorBrush(textColor), 0))
            {
                text.Draw(context, new Point(
                    bounds.X + 10, bounds.Y + (RingMetrics.CrumbHeight - text.Height) / 2));
            }

            if (crumb.IsChanged)
            {
                context.DrawEllipse(GoldBrush, null,
                    new Point(bounds.Right - 9, bounds.Y + RingMetrics.CrumbHeight / 2), 3, 3);
            }

            if (slot.Index < _layout.CrumbSlots.Count - 1)
            {
                using (var chevron = LayoutText("\u203A", InterRegular, 12, MutedBrush, 0))
                {
                    var gapCentreX = bounds.Right + 16 / 2;
                    chevron.Draw(context, new Point(
                        gapCentreX - chevron.Width / 2,
                        bounds.Y + (RingMetrics.CrumbHeight - chevron.Height) / 2));
                }
            }
        }
    }

    private void DrawHint(DrawingContext context, KeyRingViewModel vm)
    {
        if (_hideHint || vm.Hint.Length == 0) return;

        using var text = LayoutText(vm.Hint, InterRegular, 11, SubBrush, 0);
        var width = text.Width + 2 * 12;
        var top = RingMetrics.ExtentRadius + RingMetrics.HintGap;

        context.DrawRectangle(HintBrush, null,
            new Rect(-width / 2, top, width, RingMetrics.HintHeight), 11, 11);
        text.Draw(context, new Point(
            -width / 2 + 12, top + (RingMetrics.HintHeight - text.Height) / 2));
    }

    // ─── paths and text helpers ──────────────────────────────────────────

    /// <summary>The segment outline as a fillable geometry, following the
    /// order documented on <see cref="SectorOutline"/>: corner arcs clockwise,
    /// the outer arc clockwise, the inner arc counter-clockwise.</summary>
    private static StreamGeometry SectorGeometry(
        double inner, double outer, double start, double end, double gap, double corner)
    {
        var outline = RingGeometry.Sector(inner, outer, start, end, gap, corner);
        var cornerSize = new Size(corner, corner);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(outline.OuterEdge0, isFilled: true);
            ctx.ArcTo(outline.OuterArc0, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.ArcTo(outline.OuterArc1, new Size(outer, outer), 0, outline.IsLargeArc, SweepDirection.Clockwise);
            ctx.ArcTo(outline.OuterEdge1, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.LineTo(outline.InnerEdge1);
            ctx.ArcTo(outline.InnerArc1, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.ArcTo(outline.InnerArc0, new Size(inner, inner), 0, outline.IsLargeArc, SweepDirection.CounterClockwise);
            ctx.ArcTo(outline.InnerEdge0, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>Draws <paramref name="text"/> centred on <paramref name="centreX"/>
    /// (control-local, ring-centre-relative), top-aligned inside the line box
    /// starting at <paramref name="y"/>.</summary>
    private static void DrawLineBox(DrawingContext context, TextLayout text, double y, double halfWidth)
    {
        text.Draw(context, new Point(-halfWidth, y));
    }

    private void DrawTextLine(
        DrawingContext context, string text, Typeface typeface, double size,
        IBrush brush, double y, double lineHeight, double centreX, double maxWidth)
    {
        using var layout = LayoutText(text, typeface, size, brush, maxWidth);
        layout.Draw(context, new Point(centreX - maxWidth / 2, y + (lineHeight - layout.Height) / 2));
    }

    private static TextLayout LayoutText(
        string text, Typeface typeface, double size, IBrush brush, double maxWidth,
        double letterSpacing = 0, int maxLines = 1) =>
        new(
            text, typeface, size, brush,
            textAlignment: TextAlignment.Center,
            textWrapping: maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            textTrimming: TextTrimming.CharacterEllipsis,
            // 0 means unlimited: TextLayout clamps a maxWidth of 0 to an empty
            // line box.
            maxWidth: maxWidth > 0 ? maxWidth : double.PositiveInfinity,
            maxLines: maxLines,
            letterSpacing: letterSpacing);

    private static double MeasureTextWidth(string text, Typeface typeface, double size)
    {
        if (text.Length == 0) return 0;

        using var layout = new TextLayout(
            text, typeface, size, TextBrush,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap,
            textTrimming: TextTrimming.None);
        return layout.Width;
    }

    // ─── icons ───────────────────────────────────────────────────────────

    private readonly record struct IconEntry(Geometry Geometry, int X, int Y, int Width, int Height);

    /// <summary>The cached path geometry of one Font Awesome icon, or null
    /// when it was unknown or unreadable (then it is skipped).</summary>
    private static IconEntry? LoadIcon(string name)
    {
        if (IconCache.TryGetValue(name, out var cached)) return cached;

        IconEntry? entry = null;
        try
        {
            var model = IconProvider.Current.GetIcon(name);
            string data = model.Path;
            if (data.Length > 0 && model.ViewBox.Width > 0 && model.ViewBox.Height > 0)
            {
                entry = new IconEntry(
                    Geometry.Parse(data),
                    model.ViewBox.X, model.ViewBox.Y, model.ViewBox.Width, model.ViewBox.Height);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("KeyRing", $"Icon '{name}' unavailable: {ex.Message}");
            entry = null;
        }

        IconCache[name] = entry;
        return entry;
    }

    private static void DrawIcon(DrawingContext context, IconEntry icon, double centreX, double centreY, IBrush brush)
    {
        const double size = 16;
        var scale = size / Math.Max(icon.Width, icon.Height);
        var offsetX = centreX - scale * (icon.X + icon.Width / 2.0);
        var offsetY = centreY - scale * (icon.Y + icon.Height / 2.0);

        using (context.PushTransform(new Matrix(scale, 0, 0, scale, offsetX, offsetY)))
        {
            context.DrawGeometry(brush, null, icon.Geometry);
        }
    }

    // ─── colour helpers ──────────────────────────────────────────────────

    private static SolidColorBrush Tint(string hex) => new(Color.Parse(hex));

    private static Typeface New(FontFamily family, FontWeight weight) =>
        new(family, FontStyle.Normal, weight);

    /// <summary>Linear RGB blend from idle to hover.</summary>
    private static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static SolidColorBrush BlendBrush(SolidColorBrush from, SolidColorBrush to, double t) =>
        t <= 0 ? from : t >= 1 ? to : new SolidColorBrush(Blend(from.Color, to.Color, t));

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
}
