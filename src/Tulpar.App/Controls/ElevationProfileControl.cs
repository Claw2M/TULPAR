using System.Collections;
using System.Globalization;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Media;
using Tulpar.App.Services;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;

namespace Tulpar.App.Controls;

/// <summary>
/// Pure-WPF GPU-rendered vertical flight elevation profile over cumulative mission distance.
/// Follows the SpaceX aerospace monochrome design system with semantic vertex markers.
/// </summary>
public sealed class ElevationProfileControl : FrameworkElement
{
    private const string EmptyPlaceholderText = "İRTİFA PROFİLİ (EN AZ 2 KONUMLU NOKTA GEREKİR)";

    public static readonly DependencyProperty ProfileLegsProperty = DependencyProperty.Register(
        nameof(ProfileLegs),
        typeof(IReadOnlyList<MissionLegMetric>),
        typeof(ElevationProfileControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedSeqProperty = DependencyProperty.Register(
        nameof(SelectedSeq),
        typeof(int),
        typeof(ElevationProfileControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MissionItemsProperty = DependencyProperty.Register(
        nameof(MissionItems),
        typeof(IEnumerable),
        typeof(ElevationProfileControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BackgroundBrush = CreateFrozenBrush(0x0A, 0x0A, 0x0D);
    private static readonly Brush BorderBrush = CreateFrozenBrush(0x3A, 0x3A, 0x3F);
    private static readonly Brush GridLineBrush = CreateFrozenBrush(0x24, 0x24, 0x2B);
    private static readonly Brush LabelBrush = CreateFrozenBrush(0x7A, 0x7A, 0x86);
    private static readonly Brush LineBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
    private static readonly Brush TakeoffBrush = CreateFrozenBrush(0x34, 0xC7, 0x59);
    private static readonly Brush LandRtlBrush = CreateFrozenBrush(0xFF, 0xCC, 0x00);
    private static readonly Brush SelectedBrush = CreateFrozenBrush(0x38, 0xBD, 0xF8);
    private static readonly Brush SelectedHaloBrush = CreateFrozenAlphaBrush(0x44, 0x38, 0xBD, 0xF8);
    private static readonly Brush TooltipBgBrush = CreateFrozenAlphaBrush(0xE6, 0x0A, 0x0A, 0x0D);
    private static readonly Brush AreaFillBrush = CreateFrozenAreaGradient();

    private static readonly Pen BorderPen = CreateFrozenPen(BorderBrush, 1.0);
    private static readonly Pen GridPen = CreateFrozenDashedPen(GridLineBrush, 1.0);
    private static readonly Pen ProfileLinePen = CreateFrozenLinePen(LineBrush, 1.75);
    private static readonly Pen VertexOutlinePen = CreateFrozenPen(BackgroundBrush, 1.25);
    private static readonly Pen SelectedHaloPen = CreateFrozenPen(SelectedBrush, 1.5);
    private static readonly Pen TooltipBorderPen = CreateFrozenPen(SelectedBrush, 1.0);

    private static readonly FontFamily InstrumentFontFamily = CreateInstrumentFontFamily();
    private static readonly Typeface RegularTypeface = new(InstrumentFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(InstrumentFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private readonly PolyLineSegment _lineSegment = new();
    private readonly PathFigure _lineFigure;
    private readonly PathGeometry _lineGeometry;

    private readonly PolyLineSegment _areaSegment = new();
    private readonly PathFigure _areaFigure;
    private readonly PathGeometry _areaGeometry;

    private readonly List<Point> _plotPoints = new(32);
    private readonly Dictionary<int, MavCmd> _commandBySeq = new(32);

    public ElevationProfileControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _lineFigure = new PathFigure(default, [_lineSegment], closed: false) { IsFilled = false };
        _lineGeometry = new PathGeometry([_lineFigure]);

        _areaFigure = new PathFigure(default, [_areaSegment], closed: true) { IsFilled = true };
        _areaGeometry = new PathGeometry([_areaFigure]);
    }

    public IReadOnlyList<MissionLegMetric>? ProfileLegs
    {
        get => (IReadOnlyList<MissionLegMetric>?)GetValue(ProfileLegsProperty);
        set => SetValue(ProfileLegsProperty, value);
    }

    public int SelectedSeq
    {
        get => (int)GetValue(SelectedSeqProperty);
        set => SetValue(SelectedSeqProperty, value);
    }

    public IEnumerable? MissionItems
    {
        get => (IEnumerable?)GetValue(MissionItemsProperty);
        set => SetValue(MissionItemsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 360.0 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 110.0 : availableSize.Height;
        return new Size(Math.Max(0.0, width), Math.Max(0.0, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w < 40.0 || h < 32.0)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRoundedRectangle(BackgroundBrush, BorderPen, new Rect(0.5, 0.5, w - 1.0, h - 1.0), 4.0, 4.0);

        IReadOnlyList<MissionLegMetric>? legs = ProfileLegs;
        if (legs is null || legs.Count < 2)
        {
            DrawPlaceholder(dc, w, h, dpi);
            return;
        }

        BuildCommandLookup();

        double minAlt = double.PositiveInfinity;
        double maxAlt = double.NegativeInfinity;
        double minDist = double.PositiveInfinity;
        double maxDist = double.NegativeInfinity;

        for (int i = 0; i < legs.Count; i++)
        {
            MissionLegMetric leg = legs[i];
            double alt = double.IsFinite(leg.AltitudeMeters) ? leg.AltitudeMeters : 0.0;
            double dist = double.IsFinite(leg.CumulativeDistanceMeters) ? leg.CumulativeDistanceMeters : 0.0;
            if (alt < minAlt) minAlt = alt;
            if (alt > maxAlt) maxAlt = alt;
            if (dist < minDist) minDist = dist;
            if (dist > maxDist) maxDist = dist;
        }

        if (!double.IsFinite(minAlt) || !double.IsFinite(maxAlt))
        {
            DrawPlaceholder(dc, w, h, dpi);
            return;
        }

        double plotMinAlt;
        double plotMaxAlt;
        if (Math.Abs(maxAlt - minAlt) < 1e-3)
        {
            plotMinAlt = minAlt - 5.0;
            plotMaxAlt = maxAlt + 5.0;
        }
        else
        {
            double span = maxAlt - minAlt;
            plotMinAlt = minAlt - span * 0.14;
            plotMaxAlt = maxAlt + span * 0.14;
        }

        bool showLeftAxisLabels = w >= 140.0;
        double leftPad = showLeftAxisLabels ? 44.0 : 10.0;
        const double rightPad = 12.0;
        const double topPad = 14.0;
        const double bottomPad = 16.0;

        Rect plot = new(
            leftPad,
            topPad,
            Math.Max(20.0, w - leftPad - rightPad),
            Math.Max(16.0, h - topPad - bottomPad));

        DrawGridAndAxisLabels(dc, plot, minAlt, maxAlt, plotMinAlt, plotMaxAlt, maxDist, showLeftAxisLabels, dpi);

        double altRange = Math.Max(1e-6, plotMaxAlt - plotMinAlt);
        double distRange = maxDist - minDist;

        _plotPoints.Clear();
        for (int i = 0; i < legs.Count; i++)
        {
            MissionLegMetric leg = legs[i];
            double alt = double.IsFinite(leg.AltitudeMeters) ? leg.AltitudeMeters : 0.0;
            double dist = double.IsFinite(leg.CumulativeDistanceMeters) ? leg.CumulativeDistanceMeters : 0.0;

            double xNorm = distRange > 1e-6
                ? (dist - minDist) / distRange
                : (double)i / (legs.Count - 1);
            double yNorm = (alt - plotMinAlt) / altRange;

            double x = plot.Left + Math.Clamp(xNorm, 0.0, 1.0) * plot.Width;
            double y = plot.Bottom - Math.Clamp(yNorm, 0.0, 1.0) * plot.Height;
            _plotPoints.Add(new Point(x, y));
        }

        DrawElevationAreaAndLine(dc, plot.Bottom);
        DrawVertexDotsAndSelection(dc, legs, w, h, dpi);
    }

    private void DrawPlaceholder(DrawingContext dc, double w, double h, double dpi)
    {
        double maxTextW = Math.Max(20.0, w - 24.0);
        FormattedText ft = CreateText(EmptyPlaceholderText, 10.0, LabelBrush, true, dpi);
        ft.MaxTextWidth = maxTextW;
        ft.TextAlignment = TextAlignment.Center;
        ft.Trimming = TextTrimming.CharacterEllipsis;

        double y = Math.Max(4.0, (h - ft.Height) / 2.0);
        dc.DrawText(ft, new Point(12.0, y));
    }

    private static void DrawGridAndAxisLabels(
        DrawingContext dc,
        Rect plot,
        double dataMinAlt,
        double dataMaxAlt,
        double plotMinAlt,
        double plotMaxAlt,
        double maxDist,
        bool showLeftAxisLabels,
        double dpi)
    {
        double altRange = Math.Max(1e-6, plotMaxAlt - plotMinAlt);
        double maxY = plot.Bottom - ((dataMaxAlt - plotMinAlt) / altRange) * plot.Height;
        double minY = plot.Bottom - ((dataMinAlt - plotMinAlt) / altRange) * plot.Height;
        double midY = (maxY + minY) * 0.5;

        dc.DrawLine(GridPen, new Point(plot.Left, maxY), new Point(plot.Right, maxY));
        if (Math.Abs(minY - maxY) >= 12.0)
        {
            dc.DrawLine(GridPen, new Point(plot.Left, midY), new Point(plot.Right, midY));
            dc.DrawLine(GridPen, new Point(plot.Left, minY), new Point(plot.Right, minY));
        }

        if (showLeftAxisLabels)
        {
            string maxLbl = $"{dataMaxAlt.ToString("0.#", CultureInfo.InvariantCulture)} m";
            FormattedText maxFt = CreateText(maxLbl, 8.5, LabelBrush, false, dpi);
            dc.DrawText(maxFt, new Point(Math.Max(4.0, plot.Left - maxFt.Width - 6.0), Math.Clamp(maxY - maxFt.Height * 0.5, 2.0, plot.Bottom - maxFt.Height)));

            if (Math.Abs(minY - maxY) >= 14.0)
            {
                string minLbl = $"{dataMinAlt.ToString("0.#", CultureInfo.InvariantCulture)} m";
                FormattedText minFt = CreateText(minLbl, 8.5, LabelBrush, false, dpi);
                dc.DrawText(minFt, new Point(Math.Max(4.0, plot.Left - minFt.Width - 6.0), Math.Clamp(minY - minFt.Height * 0.5, 2.0, plot.Bottom - minFt.Height + 4.0)));
            }
        }

        if (maxDist > 0.0)
        {
            string distLbl = maxDist >= 1000.0
                ? $"{(maxDist / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} km"
                : $"{maxDist.ToString("0", CultureInfo.InvariantCulture)} m";
            FormattedText distFt = CreateText(distLbl, 8.0, LabelBrush, false, dpi);
            dc.DrawText(distFt, new Point(plot.Right - distFt.Width, plot.Bottom + 2.0));
        }
    }

    private void DrawElevationAreaAndLine(DrawingContext dc, double baselineY)
    {
        if (_plotPoints.Count < 2)
        {
            return;
        }

        _areaFigure.StartPoint = new Point(_plotPoints[0].X, baselineY);
        _areaSegment.Points.Clear();
        for (int i = 0; i < _plotPoints.Count; i++)
        {
            _areaSegment.Points.Add(_plotPoints[i]);
        }
        _areaSegment.Points.Add(new Point(_plotPoints[^1].X, baselineY));

        _lineFigure.StartPoint = _plotPoints[0];
        _lineSegment.Points.Clear();
        for (int i = 1; i < _plotPoints.Count; i++)
        {
            _lineSegment.Points.Add(_plotPoints[i]);
        }

        dc.DrawGeometry(AreaFillBrush, null, _areaGeometry);
        dc.DrawGeometry(null, ProfileLinePen, _lineGeometry);
    }

    private void DrawVertexDotsAndSelection(
        DrawingContext dc,
        IReadOnlyList<MissionLegMetric> legs,
        double totalWidth,
        double totalHeight,
        double dpi)
    {
        int selectedSeq = SelectedSeq;
        int selectedIndex = -1;

        for (int i = 0; i < legs.Count; i++)
        {
            MissionLegMetric leg = legs[i];
            Point pt = _plotPoints[i];
            bool isSelected = selectedSeq >= 0 && leg.Seq == selectedSeq;
            if (isSelected)
            {
                selectedIndex = i;
                continue;
            }

            (Brush dotBrush, double radius) = ResolveVertexStyle(leg.Seq);
            dc.DrawEllipse(dotBrush, VertexOutlinePen, pt, radius, radius);
        }

        if (selectedIndex >= 0)
        {
            MissionLegMetric selectedLeg = legs[selectedIndex];
            Point pt = _plotPoints[selectedIndex];

            dc.DrawEllipse(SelectedHaloBrush, SelectedHaloPen, pt, 7.0, 7.0);
            dc.DrawEllipse(SelectedBrush, VertexOutlinePen, pt, 4.0, 4.0);

            double alt = double.IsFinite(selectedLeg.AltitudeMeters) ? selectedLeg.AltitudeMeters : 0.0;
            string callout = $"#{selectedLeg.Seq.ToString(CultureInfo.InvariantCulture)} · {alt.ToString("0.#", CultureInfo.InvariantCulture)} m";
            FormattedText ft = CreateText(callout, 8.5, SelectedBrush, true, dpi);

            double pillW = ft.Width + 10.0;
            double pillH = ft.Height + 4.0;
            double pillX = Math.Clamp(pt.X - pillW * 0.5, 6.0, Math.Max(6.0, totalWidth - pillW - 6.0));
            double pillY = pt.Y - pillH - 9.0;
            if (pillY < 4.0)
            {
                pillY = Math.Min(totalHeight - pillH - 4.0, pt.Y + 9.0);
            }

            Rect pillRect = new(pillX, pillY, pillW, pillH);
            dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, pillRect, 2.0, 2.0);
            dc.DrawText(ft, new Point(pillRect.Left + 5.0, pillRect.Top + 2.0));
        }
    }

    private (Brush DotBrush, double Radius) ResolveVertexStyle(int seq)
    {
        if (_commandBySeq.TryGetValue(seq, out MavCmd cmd))
        {
            switch (cmd)
            {
                case MavCmd.NavTakeoff:
                case MavCmd.NavTakeoffLocal:
                case MavCmd.NavVtolTakeoff:
                    return (TakeoffBrush, 3.8);

                case MavCmd.NavLand:
                case MavCmd.NavLandLocal:
                case MavCmd.NavVtolLand:
                case MavCmd.NavReturnToLaunch:
                    return (LandRtlBrush, 3.8);
            }
        }

        return (LineBrush, 3.2);
    }

    private void BuildCommandLookup()
    {
        _commandBySeq.Clear();
        IEnumerable? items = MissionItems;
        if (items is null)
        {
            return;
        }

        foreach (object? entry in items)
        {
            switch (entry)
            {
                case MissionItem coreItem:
                    _commandBySeq[coreItem.Seq] = coreItem.Command;
                    break;
                case EditableMissionItem editableItem:
                    _commandBySeq[editableItem.Seq] = editableItem.Command;
                    break;
            }
        }
    }

    private static FormattedText CreateText(string text, double fontSize, Brush brush, bool bold, double dpi) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            bold ? BoldTypeface : RegularTypeface,
            fontSize,
            brush,
            dpi);

    private static FontFamily CreateInstrumentFontFamily()
    {
        _ = PackUriHelper.UriSchemePack;
        return new FontFamily("pack://application:,,,/Tulpar.App;component/Assets/Fonts/#D-DIN, Arial Narrow, Arial, Verdana");
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenAlphaBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateFrozenAreaGradient()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF),
            new Point(0.0, 0.0),
            new Point(0.0, 1.0));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFrozenLinePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFrozenDashedPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = new DashStyle([3.0, 4.0], 0.0)
        };
        pen.Freeze();
        return pen;
    }
}
