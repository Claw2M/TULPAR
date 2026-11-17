using System.Globalization;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Media;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.App.Controls;

/// <summary>
/// Adaptive 60-FPS vector aerospace HUD rendered via WPF <see cref="DrawingContext"/>.
/// Switches automatically between aerial artificial-horizon instruments and surface-only
/// 360-degree compass rose + inclinometer layout for IKA/IDA platforms.
/// </summary>
public sealed class HudControl : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(TelemetrySnapshot),
        typeof(HudControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VehicleProfileProperty = DependencyProperty.Register(
        nameof(VehicleProfile),
        typeof(IVehicleProfile),
        typeof(HudControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // SpaceX Design System palette (pure black/white/hairline + safety alerts)
    private static readonly Brush CanvasNightBrush = CreateFrozenBrush(0x00, 0x00, 0x00);
    private static readonly Brush CanvasNightSoftBrush = CreateFrozenBrush(0x0A, 0x0A, 0x0A);
    private static readonly Brush OnPrimaryBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
    private static readonly Brush OnPrimaryMuteBrush = CreateFrozenBrush(0xF0, 0xF0, 0xFA);
    private static readonly Brush HairlineDarkBrush = CreateFrozenBrush(0x3A, 0x3A, 0x3F);
    private static readonly Brush InkMuteBrush = CreateFrozenBrush(0x5A, 0x5A, 0x5F);
    private static readonly Brush HazardRedBrush = CreateFrozenBrush(0xFF, 0x3B, 0x30);
    private static readonly Brush WarningAmberBrush = CreateFrozenBrush(0xFF, 0xCC, 0x00);

    private static readonly Pen HairlinePen = CreateFrozenPen(HairlineDarkBrush, 1.0);
    private static readonly Pen PrimaryThinPen = CreateFrozenPen(OnPrimaryBrush, 1.0);
    private static readonly Pen PrimaryVectorPen = CreateFrozenPen(OnPrimaryBrush, 1.5);
    private static readonly Pen MuteThinPen = CreateFrozenPen(OnPrimaryMuteBrush, 1.0);
    private static readonly Pen MuteVectorPen = CreateFrozenPen(OnPrimaryMuteBrush, 1.5);
    private static readonly Pen MuteDashedPen = CreateFrozenDashedPen(OnPrimaryMuteBrush, 1.5);
    private static readonly Pen HazardPen = CreateFrozenPen(HazardRedBrush, 1.5);
    private static readonly Pen WarningPen = CreateFrozenPen(WarningAmberBrush, 1.0);

    private static readonly FontFamily InstrumentFontFamily = CreateInstrumentFontFamily();
    private static readonly Typeface RegularTypeface = new(InstrumentFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(InstrumentFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public HudControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public TelemetrySnapshot? Snapshot
    {
        get => (TelemetrySnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public IVehicleProfile? VehicleProfile
    {
        get => (IVehicleProfile?)GetValue(VehicleProfileProperty);
        set => SetValue(VehicleProfileProperty, value);
    }

    public HudRenderState CurrentRenderState => HudProjection.FromSnapshot(Snapshot, VehicleProfile);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 340.0 : Math.Min(340.0, availableSize.Width);
        double height = double.IsInfinity(availableSize.Height) ? 250.0 : Math.Min(250.0, availableSize.Height);
        return new Size(Math.Max(0.0, width), Math.Max(0.0, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w < 80.0 || h < 80.0)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        HudRenderState state = CurrentRenderState;

        // Pure black canvas with 1px #3a3a3f hairline frame (zero drop shadows/gradients)
        dc.DrawRoundedRectangle(CanvasNightBrush, HairlinePen, new Rect(0.5, 0.5, w - 1.0, h - 1.0), 4.0, 4.0);

        const double pad = 8.0;
        const double headerH = 22.0;
        const double footerH = 22.0;
        const double compassH = 24.0;

        Rect compassRect = new(pad + 54.0, pad + headerH, Math.Max(60.0, w - (pad + 54.0) * 2.0), compassH);
        DrawTopCompassTape(dc, compassRect, state.HeadingDeg, dpi);

        double instrumentTop = compassRect.Bottom + 6.0;
        double instrumentBottom = h - pad - footerH - 4.0;
        double instrumentH = Math.Max(40.0, instrumentBottom - instrumentTop);

        if (state.SurfaceOnlyMode)
        {
            DrawSurfaceInstrumentLayout(dc, new Rect(pad, instrumentTop, w - pad * 2.0, instrumentH), state, dpi);
        }
        else
        {
            DrawAerialInstrumentLayout(dc, w, instrumentTop, instrumentH, state, dpi);
        }

        DrawCornerBadges(dc, w, h, pad, state, dpi);

        if (state.LinkLost)
        {
            DrawLinkLostBanner(dc, w, h, dpi);
        }
    }

    private static void DrawAerialInstrumentLayout(
        DrawingContext dc,
        double totalWidth,
        double instrumentTop,
        double instrumentH,
        HudRenderState state,
        double dpi)
    {
        const double pad = 8.0;
        double tapeW = Math.Clamp(totalWidth * 0.16, 48.0, 64.0);

        Rect leftTapeRect = new(pad, instrumentTop, tapeW, instrumentH);
        Rect rightTapeRect = new(totalWidth - pad - tapeW, instrumentTop, tapeW, instrumentH);
        Rect centerRect = new(
            leftTapeRect.Right + 6.0,
            instrumentTop,
            Math.Max(40.0, rightTapeRect.Left - leftTapeRect.Right - 12.0),
            instrumentH);

        DrawSpeedTape(dc, leftTapeRect, state, dpi);

        if (state.ShowAltitudeTape)
        {
            DrawAltitudeAndVsiTape(dc, rightTapeRect, state, dpi);
        }

        if (state.ShowHorizon)
        {
            DrawArtificialHorizon(dc, centerRect, state, dpi);
        }

        if (state.ShowVtolTransition)
        {
            DrawBadgeBox(
                dc,
                new Point(centerRect.Left + 6.0, centerRect.Top + 6.0),
                42.0,
                16.0,
                "VTOL",
                WarningAmberBrush,
                WarningPen,
                dpi);
        }
    }

    private static void DrawArtificialHorizon(DrawingContext dc, Rect rect, HudRenderState state, double dpi)
    {
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, rect, 4.0, 4.0);

        double cx = rect.Left + rect.Width / 2.0;
        double cy = rect.Top + rect.Height / 2.0;
        double pxPerDeg = Math.Max(1.6, rect.Height / 64.0);
        double pitchOffsetPx = HudProjection.ClampPitch(state.PitchDeg) * pxPerDeg;
        double ladderScale = Math.Min(1.0, rect.Width / 170.0);

        // Top roll bank arc (-45..+45)
        double rollRadius = Math.Min(rect.Width, rect.Height) * 0.42;
        DrawRollArc(dc, cx, cy, rollRadius, state.RollDeg);

        dc.PushClip(new RectangleGeometry(rect));
        dc.PushTransform(new RotateTransform(-state.RollDeg, cx, cy));

        double horizonY = cy + pitchOffsetPx;
        double halfSpan = Math.Max(rect.Width, rect.Height);
        double centerGap = 18.0 * ladderScale;

        // Horizon line split around fixed aircraft watermark
        dc.DrawLine(PrimaryVectorPen, new Point(cx - halfSpan, horizonY), new Point(cx - centerGap, horizonY));
        dc.DrawLine(PrimaryVectorPen, new Point(cx + centerGap, horizonY), new Point(cx + halfSpan, horizonY));

        // Pitch ladder rungs (-20, -10, +10, +20)
        int[] rungs = [-20, -10, 10, 20];
        foreach (int deg in rungs)
        {
            double rungY = horizonY - deg * pxPerDeg;
            double inner = 16.0 * ladderScale;
            double outer = (Math.Abs(deg) == 20 ? 44.0 : 34.0) * ladderScale;
            double tipDir = deg > 0 ? 5.0 : -5.0;
            Pen pen = deg > 0 ? MuteVectorPen : MuteDashedPen;

            dc.DrawLine(pen, new Point(cx - outer, rungY), new Point(cx - inner, rungY));
            dc.DrawLine(pen, new Point(cx - outer, rungY), new Point(cx - outer, rungY + tipDir));

            dc.DrawLine(pen, new Point(cx + inner, rungY), new Point(cx + outer, rungY));
            dc.DrawLine(pen, new Point(cx + outer, rungY), new Point(cx + outer, rungY + tipDir));

            string label = deg > 0 ? $"+{deg}" : deg.ToString(CultureInfo.InvariantCulture);
            FormattedText ft = CreateText(label, 9.0, OnPrimaryMuteBrush, false, dpi);
            dc.DrawText(ft, new Point(cx - outer - ft.Width - 3.0, rungY - ft.Height / 2.0));
            dc.DrawText(ft, new Point(cx + outer + 3.0, rungY - ft.Height / 2.0));
        }

        dc.Pop();
        dc.Pop();

        // Fixed aircraft waterline crosshair
        dc.DrawLine(PrimaryVectorPen, new Point(cx - 22.0, cy), new Point(cx - 8.0, cy));
        dc.DrawLine(PrimaryVectorPen, new Point(cx - 8.0, cy), new Point(cx, cy + 6.0));
        dc.DrawLine(PrimaryVectorPen, new Point(cx, cy + 6.0), new Point(cx + 8.0, cy));
        dc.DrawLine(PrimaryVectorPen, new Point(cx + 8.0, cy), new Point(cx + 22.0, cy));
        dc.DrawEllipse(OnPrimaryBrush, null, new Point(cx, cy), 1.8, 1.8);
    }

    private static void DrawRollArc(DrawingContext dc, double cx, double cy, double radius, double rollDeg)
    {
        if (radius < 28.0)
        {
            return;
        }

        double[] bankAngles = [-45.0, -30.0, -15.0, 0.0, 15.0, 30.0, 45.0];
        Point? prev = null;

        for (int deg = -45; deg <= 45; deg += 5)
        {
            double rad = (deg - 90.0) * (Math.PI / 180.0);
            Point pt = new(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
            if (prev is Point start)
            {
                dc.DrawLine(HairlinePen, start, pt);
            }
            prev = pt;
        }

        foreach (double angle in bankAngles)
        {
            double rad = (angle - 90.0) * (Math.PI / 180.0);
            double tickLen = angle == 0.0 ? 7.0 : 5.0;
            Point inner = new(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
            Point outer = new(cx + (radius + tickLen) * Math.Cos(rad), cy + (radius + tickLen) * Math.Sin(rad));
            dc.DrawLine(angle == 0.0 ? PrimaryThinPen : MuteThinPen, inner, outer);
        }

        double clampedRoll = Math.Clamp(rollDeg, -45.0, 45.0);
        double pointerRad = (-clampedRoll - 90.0) * (Math.PI / 180.0);
        double tipR = radius - 2.0;
        double baseR = radius - 8.0;
        Point tip = new(cx + tipR * Math.Cos(pointerRad), cy + tipR * Math.Sin(pointerRad));
        Point left = new(cx + baseR * Math.Cos(pointerRad - 0.08), cy + baseR * Math.Sin(pointerRad - 0.08));
        Point right = new(cx + baseR * Math.Cos(pointerRad + 0.08), cy + baseR * Math.Sin(pointerRad + 0.08));

        var tri = new StreamGeometry();
        using (StreamGeometryContext ctx = tri.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(left, true, false);
            ctx.LineTo(right, true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(OnPrimaryBrush, null, tri);
    }

    private static void DrawTopCompassTape(DrawingContext dc, Rect rect, double rawHeadingDeg, double dpi)
    {
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, rect, 4.0, 4.0);
        dc.PushClip(new RectangleGeometry(rect));

        double heading = HudProjection.NormalizeHeading(rawHeadingDeg);
        double cx = rect.Left + rect.Width / 2.0;
        const double visibleHalfSpanDeg = 45.0;
        double pxPerDeg = rect.Width / (visibleHalfSpanDeg * 2.0);

        int baseTick = (int)Math.Floor(heading / 5.0) * 5;
        for (int offset = -50; offset <= 50; offset += 5)
        {
            double tickDeg = HudProjection.NormalizeHeading(baseTick + offset);
            double diff = HudProjection.ShortestAngleDiffDeg(tickDeg, heading);
            if (Math.Abs(diff) > visibleHalfSpanDeg + 4.0)
            {
                continue;
            }

            double x = cx + diff * pxPerDeg;
            int degInt = (int)Math.Round(tickDeg) % 360;
            bool isMajor = degInt % 15 == 0;
            double tickTop = rect.Bottom - (isMajor ? 9.0 : 5.0);
            dc.DrawLine(isMajor ? PrimaryThinPen : HairlinePen, new Point(x, tickTop), new Point(x, rect.Bottom));

            if (isMajor)
            {
                string label = FormatHeadingTickLabel(degInt);
                FormattedText ft = CreateText(label, 8.5, OnPrimaryMuteBrush, degInt % 90 == 0, dpi);
                dc.DrawText(ft, new Point(x - ft.Width / 2.0, rect.Top + 2.0));
            }
        }

        dc.Pop();

        // Digital heading readout box centered on compass tape
        string hdgText = $"{Math.Round(heading) % 360:000}°";
        FormattedText hdgFt = CreateText(hdgText, 10.5, OnPrimaryBrush, true, dpi);
        double boxW = Math.Max(34.0, hdgFt.Width + 10.0);
        Rect hdgBox = new(cx - boxW / 2.0, rect.Top + 1.0, boxW, rect.Height - 2.0);
        dc.DrawRoundedRectangle(CanvasNightBrush, PrimaryThinPen, hdgBox, 2.0, 2.0);
        dc.DrawText(hdgFt, new Point(cx - hdgFt.Width / 2.0, hdgBox.Top + (hdgBox.Height - hdgFt.Height) / 2.0));
    }

    private static void DrawSpeedTape(DrawingContext dc, Rect rect, HudRenderState state, double dpi)
    {
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, rect, 4.0, 4.0);
        dc.PushClip(new RectangleGeometry(rect));

        double primarySpeed = state.ShowAirspeed ? state.AirspeedMps : state.GroundspeedMps;
        string primaryTag = state.ShowAirspeed ? "IAS" : "GS";

        double cy = rect.Top + rect.Height / 2.0;
        const double pxPerMps = 4.5;
        int minTick = (int)Math.Floor((primarySpeed - 25.0) / 5.0) * 5;
        int maxTick = (int)Math.Ceiling((primarySpeed + 25.0) / 5.0) * 5;

        for (int spd = minTick; spd <= maxTick; spd += 5)
        {
            if (spd < 0)
            {
                continue;
            }

            double y = cy - (spd - primarySpeed) * pxPerMps;
            if (y < rect.Top + 14.0 || y > rect.Bottom - 20.0)
            {
                continue;
            }

            bool isMajor = spd % 10 == 0;
            dc.DrawLine(MuteThinPen, new Point(rect.Right - (isMajor ? 10.0 : 6.0), y), new Point(rect.Right, y));
            if (isMajor)
            {
                FormattedText ft = CreateText(spd.ToString(CultureInfo.InvariantCulture), 8.5, OnPrimaryMuteBrush, false, dpi);
                dc.DrawText(ft, new Point(rect.Left + 4.0, y - ft.Height / 2.0));
            }
        }

        dc.Pop();

        // Tag + primary speed box
        FormattedText tagFt = CreateText(primaryTag, 8.5, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(tagFt, new Point(rect.Left + 4.0, rect.Top + 2.0));

        Rect readoutBox = new(rect.Left + 2.0, cy - 11.0, rect.Width - 4.0, 22.0);
        dc.DrawRoundedRectangle(CanvasNightBrush, PrimaryThinPen, readoutBox, 2.0, 2.0);
        FormattedText valFt = CreateText(primarySpeed.ToString("0.0", CultureInfo.InvariantCulture), 10.5, OnPrimaryBrush, true, dpi);
        dc.DrawText(valFt, new Point(readoutBox.Left + (readoutBox.Width - valFt.Width) / 2.0, readoutBox.Top + (readoutBox.Height - valFt.Height) / 2.0));

        if (state.ShowAirspeed)
        {
            string gsText = $"GS {state.GroundspeedMps.ToString("0.0", CultureInfo.InvariantCulture)}";
            FormattedText gsFt = CreateText(gsText, 8.5, OnPrimaryMuteBrush, false, dpi);
            dc.DrawText(gsFt, new Point(rect.Left + 4.0, rect.Bottom - gsFt.Height - 2.0));
        }
    }

    private static void DrawAltitudeAndVsiTape(DrawingContext dc, Rect rect, HudRenderState state, double dpi)
    {
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, rect, 4.0, 4.0);
        dc.PushClip(new RectangleGeometry(rect));

        double alt = state.AltitudeRelMeters;
        double cy = rect.Top + rect.Height / 2.0;
        const double pxPerMeter = 2.0;

        int minTick = (int)Math.Floor((alt - 60.0) / 10.0) * 10;
        int maxTick = (int)Math.Ceiling((alt + 60.0) / 10.0) * 10;

        for (int a = minTick; a <= maxTick; a += 10)
        {
            double y = cy - (a - alt) * pxPerMeter;
            if (y < rect.Top + 14.0 || y > rect.Bottom - 20.0)
            {
                continue;
            }

            bool isMajor = a % 20 == 0;
            dc.DrawLine(MuteThinPen, new Point(rect.Left, y), new Point(rect.Left + (isMajor ? 10.0 : 6.0), y));
            if (isMajor)
            {
                FormattedText ft = CreateText(a.ToString(CultureInfo.InvariantCulture), 8.5, OnPrimaryMuteBrush, false, dpi);
                dc.DrawText(ft, new Point(rect.Right - ft.Width - 4.0, y - ft.Height / 2.0));
            }
        }

        dc.Pop();

        FormattedText tagFt = CreateText("ALT", 8.5, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(tagFt, new Point(rect.Right - tagFt.Width - 4.0, rect.Top + 2.0));

        Rect readoutBox = new(rect.Left + 2.0, cy - 11.0, rect.Width - 4.0, 22.0);
        dc.DrawRoundedRectangle(CanvasNightBrush, PrimaryThinPen, readoutBox, 2.0, 2.0);
        FormattedText valFt = CreateText($"{alt.ToString("0.0", CultureInfo.InvariantCulture)}m", 10.0, OnPrimaryBrush, true, dpi);
        dc.DrawText(valFt, new Point(readoutBox.Left + (readoutBox.Width - valFt.Width) / 2.0, readoutBox.Top + (readoutBox.Height - valFt.Height) / 2.0));

        string vsiSign = state.VerticalSpeedMps >= 0 ? "+" : string.Empty;
        string vsiText = $"VS {vsiSign}{state.VerticalSpeedMps.ToString("0.0", CultureInfo.InvariantCulture)}";
        FormattedText vsiFt = CreateText(vsiText, 8.5, OnPrimaryMuteBrush, false, dpi);
        dc.DrawText(vsiFt, new Point(rect.Right - vsiFt.Width - 4.0, rect.Bottom - vsiFt.Height - 2.0));
    }

    private static void DrawSurfaceInstrumentLayout(DrawingContext dc, Rect area, HudRenderState state, double dpi)
    {
        double tiltBarH = 30.0;
        double speedPanelW = Math.Clamp(area.Width * 0.28, 76.0, 108.0);
        Rect speedRect = new(area.Left, area.Top, speedPanelW, Math.Max(40.0, area.Height - tiltBarH - 6.0));
        Rect roseArea = new(
            speedRect.Right + 8.0,
            area.Top,
            Math.Max(60.0, area.Width - speedPanelW - 8.0),
            Math.Max(40.0, area.Height - tiltBarH - 6.0));
        Rect tiltRect = new(area.Left, area.Bottom - tiltBarH, area.Width, tiltBarH);

        // Ground / water speed readout panel (left)
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, speedRect, 4.0, 4.0);
        FormattedText gsTitle = CreateText("SOG / GS", 9.0, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(gsTitle, new Point(speedRect.Left + 8.0, speedRect.Top + 6.0));

        FormattedText gsValue = CreateText(state.GroundspeedMps.ToString("0.0", CultureInfo.InvariantCulture), 20.0, OnPrimaryBrush, true, dpi);
        double valY = speedRect.Top + (speedRect.Height - gsValue.Height) / 2.0;
        dc.DrawText(gsValue, new Point(speedRect.Left + (speedRect.Width - gsValue.Width) / 2.0, valY));

        FormattedText unitFt = CreateText("M/S", 9.0, OnPrimaryMuteBrush, false, dpi);
        dc.DrawText(unitFt, new Point(speedRect.Left + (speedRect.Width - unitFt.Width) / 2.0, valY + gsValue.Height));

        double kmh = state.GroundspeedMps * 3.6;
        FormattedText kmhFt = CreateText($"{kmh.ToString("0.0", CultureInfo.InvariantCulture)} KM/H", 8.5, InkMuteBrush, false, dpi);
        dc.DrawText(kmhFt, new Point(speedRect.Left + 8.0, speedRect.Bottom - kmhFt.Height - 6.0));

        // 360-degree compass rose & heading vector (right/center)
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, roseArea, 4.0, 4.0);
        FormattedText roseLabel = CreateText("HDG ROSE", 8.5, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(roseLabel, new Point(roseArea.Left + 8.0, roseArea.Top + 6.0));
        double cx = roseArea.Left + roseArea.Width / 2.0;
        double cy = roseArea.Top + roseArea.Height / 2.0;
        double radius = Math.Max(18.0, Math.Min(roseArea.Width, roseArea.Height) * 0.33);

        dc.DrawEllipse(null, HairlinePen, new Point(cx, cy), radius, radius);
        for (int deg = 0; deg < 360; deg += 15)
        {
            double rad = (deg - 90.0) * (Math.PI / 180.0);
            bool cardinal = deg % 90 == 0;
            double tickLen = cardinal ? 7.0 : (deg % 45 == 0 ? 5.0 : 3.0);
            Point outer = new(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
            Point inner = new(cx + (radius - tickLen) * Math.Cos(rad), cy + (radius - tickLen) * Math.Sin(rad));
            dc.DrawLine(cardinal ? PrimaryThinPen : HairlinePen, inner, outer);

            if (cardinal)
            {
                string cardLabel = FormatHeadingTickLabel(deg);
                FormattedText cardFt = CreateText(cardLabel, 9.0, OnPrimaryMuteBrush, true, dpi);
                Point lblPos = new(
                    cx + (radius + 9.0) * Math.Cos(rad) - cardFt.Width / 2.0,
                    cy + (radius + 9.0) * Math.Sin(rad) - cardFt.Height / 2.0);
                dc.DrawText(cardFt, lblPos);
            }
        }

        // Heading vector arrow pointing toward NormalizeHeading(state.HeadingDeg)
        double hdgRad = (state.HeadingDeg - 90.0) * (Math.PI / 180.0);
        double tipLen = radius - 8.0;
        Point arrowTip = new(cx + tipLen * Math.Cos(hdgRad), cy + tipLen * Math.Sin(hdgRad));
        Point arrowTail = new(cx - (tipLen * 0.45) * Math.Cos(hdgRad), cy - (tipLen * 0.45) * Math.Sin(hdgRad));
        dc.DrawLine(PrimaryVectorPen, arrowTail, arrowTip);

        Point headLeft = new(
            arrowTip.X - 7.0 * Math.Cos(hdgRad - 0.45),
            arrowTip.Y - 7.0 * Math.Sin(hdgRad - 0.45));
        Point headRight = new(
            arrowTip.X - 7.0 * Math.Cos(hdgRad + 0.45),
            arrowTip.Y - 7.0 * Math.Sin(hdgRad + 0.45));
        dc.DrawLine(PrimaryVectorPen, arrowTip, headLeft);
        dc.DrawLine(PrimaryVectorPen, arrowTip, headRight);
        dc.DrawEllipse(OnPrimaryBrush, null, new Point(cx, cy), 2.5, 2.5);

        // Side-tilt / roll bar (bottom inclinometer)
        dc.DrawRoundedRectangle(CanvasNightSoftBrush, HairlinePen, tiltRect, 4.0, 4.0);
        FormattedText tiltLabel = CreateText("SIDE TILT", 8.5, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(tiltLabel, new Point(tiltRect.Left + 8.0, tiltRect.Top + (tiltRect.Height - tiltLabel.Height) / 2.0));

        string rollSign = state.RollDeg >= 0 ? "+" : string.Empty;
        FormattedText tiltVal = CreateText($"{rollSign}{state.RollDeg.ToString("0.0", CultureInfo.InvariantCulture)}°", 9.5, OnPrimaryBrush, true, dpi);
        dc.DrawText(tiltVal, new Point(tiltRect.Right - tiltVal.Width - 8.0, tiltRect.Top + (tiltRect.Height - tiltVal.Height) / 2.0));

        double trackLeft = tiltRect.Left + tiltLabel.Width + 18.0;
        double trackRight = tiltRect.Right - tiltVal.Width - 18.0;
        if (trackRight - trackLeft > 30.0)
        {
            double trackMidX = (trackLeft + trackRight) / 2.0;
            double trackY = tiltRect.Top + tiltRect.Height / 2.0;
            dc.DrawLine(HairlinePen, new Point(trackLeft, trackY), new Point(trackRight, trackY));
            dc.DrawLine(PrimaryThinPen, new Point(trackMidX, trackY - 5.0), new Point(trackMidX, trackY + 5.0));

            double normalizedTilt = Math.Clamp(state.RollDeg, -45.0, 45.0) / 45.0;
            double markerX = trackMidX + normalizedTilt * ((trackRight - trackLeft) * 0.48);
            bool excessiveTilt = Math.Abs(state.RollDeg) >= 25.0;
            dc.DrawRectangle(
                excessiveTilt ? WarningAmberBrush : OnPrimaryBrush,
                null,
                new Rect(markerX - 3.0, trackY - 6.0, 6.0, 12.0));
        }
    }

    private static void DrawCornerBadges(DrawingContext dc, double w, double h, double pad, HudRenderState state, double dpi)
    {
        // Top-left: Vehicle label
        FormattedText vehicleFt = CreateText(state.VehicleLabel, 9.5, OnPrimaryMuteBrush, true, dpi);
        dc.DrawText(vehicleFt, new Point(pad + 2.0, pad + 2.0));

        // Top-right: Flight mode badge
        FormattedText modeFt = CreateText(state.ModeName, 9.5, OnPrimaryBrush, true, dpi);
        double modeW = Math.Max(46.0, modeFt.Width + 12.0);
        DrawBadgeBox(dc, new Point(w - pad - modeW, pad), modeW, 18.0, state.ModeName, OnPrimaryBrush, HairlinePen, dpi);

        // Bottom-left: ARMED (HazardRed) vs DISARMED (InkMute)
        string armText = state.IsArmed ? "ARMED" : "DISARMED";
        Brush armBrush = state.IsArmed ? HazardRedBrush : InkMuteBrush;
        Pen armPen = state.IsArmed ? HazardPen : HairlinePen;
        DrawBadgeBox(dc, new Point(pad, h - pad - 18.0), 66.0, 18.0, armText, armBrush, armPen, dpi);

        // Bottom-right: Attitude or Surface status
        string rightStatus = state.SurfaceOnlyMode
            ? "SURFACE"
            : $"P {(state.PitchDeg >= 0 ? "+" : string.Empty)}{state.PitchDeg.ToString("0", CultureInfo.InvariantCulture)}°  R {(state.RollDeg >= 0 ? "+" : string.Empty)}{state.RollDeg.ToString("0", CultureInfo.InvariantCulture)}°";
        FormattedText statusFt = CreateText(rightStatus, 9.0, OnPrimaryMuteBrush, false, dpi);
        dc.DrawText(statusFt, new Point(w - pad - statusFt.Width - 2.0, h - pad - statusFt.Height - 2.0));
    }

    private static void DrawLinkLostBanner(DrawingContext dc, double w, double h, double dpi)
    {
        double bannerW = Math.Min(180.0, w - 32.0);
        const double bannerH = 28.0;
        Rect banner = new((w - bannerW) / 2.0, (h - bannerH) / 2.0, bannerW, bannerH);

        dc.DrawRoundedRectangle(CanvasNightBrush, HazardPen, banner, 4.0, 4.0);
        FormattedText ft = CreateText("LINK LOST", 13.0, HazardRedBrush, true, dpi);
        dc.DrawText(ft, new Point(banner.Left + (banner.Width - ft.Width) / 2.0, banner.Top + (banner.Height - ft.Height) / 2.0));
    }

    private static void DrawBadgeBox(
        DrawingContext dc,
        Point topLeft,
        double width,
        double height,
        string text,
        Brush foreground,
        Pen borderPen,
        double dpi)
    {
        Rect box = new(topLeft.X, topLeft.Y, width, height);
        dc.DrawRoundedRectangle(CanvasNightBrush, borderPen, box, 4.0, 4.0);
        FormattedText ft = CreateText(text, 9.5, foreground, true, dpi);
        dc.DrawText(ft, new Point(box.Left + (box.Width - ft.Width) / 2.0, box.Top + (box.Height - ft.Height) / 2.0));
    }

    private static string FormatHeadingTickLabel(int degInt) =>
        degInt switch
        {
            0 => "N",
            45 => "NE",
            90 => "E",
            135 => "SE",
            180 => "S",
            225 => "SW",
            270 => "W",
            315 => "NW",
            _ => degInt.ToString("000", CultureInfo.InvariantCulture)
        };

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

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFrozenDashedPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = new DashStyle([4.0, 3.0], 0.0)
        };
        pen.Freeze();
        return pen;
    }
}
