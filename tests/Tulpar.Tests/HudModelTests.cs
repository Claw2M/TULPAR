using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tulpar.App.Controls;
using Tulpar.Core.Mavlink;
using Tulpar.Core.Missions;
using Tulpar.Core.Session;
using Tulpar.Core.Vehicles;

namespace Tulpar.Tests;

public class HudModelTests
{
    [Fact]
    public void HudProjection_FixedWingVsBoatProfile_TogglesAirspeedHorizonAndAltitudeTapes()
    {
        var snapshot = new TelemetrySnapshot(
            IsConnected: true,
            LinkLost: false,
            VehicleType: MavType.FixedWing,
            Autopilot: MavAutopilot.ArduPilotMega,
            VehicleDisplayLabel: "SİHA / SABİT KANAT",
            VehicleCategory: VehicleCategory.FixedWing,
            ModeName: "GUIDED",
            IsArmed: true,
            AltitudeRelativeMeters: 125.4,
            AirspeedMps: 24.8,
            GroundspeedMps: 21.3,
            VerticalSpeedMps: 1.9,
            HeadingDeg: 185.2,
            RollDeg: 14.5,
            PitchDeg: -4.2);

        HudRenderState fwState = HudProjection.FromSnapshot(snapshot, new FixedWingProfile());
        Assert.True(fwState.ShowHorizon);
        Assert.True(fwState.ShowAirspeed);
        Assert.True(fwState.ShowAltitudeTape);
        Assert.False(fwState.ShowVtolTransition);
        Assert.False(fwState.SurfaceOnlyMode);
        Assert.Equal(14.5, fwState.RollDeg, 3);
        Assert.Equal(-4.2, fwState.PitchDeg, 3);
        Assert.Equal(185.2, fwState.HeadingDeg, 3);
        Assert.Equal(24.8, fwState.AirspeedMps, 3);
        Assert.Equal(21.3, fwState.GroundspeedMps, 3);
        Assert.Equal(125.4, fwState.AltitudeRelMeters, 3);
        Assert.Equal(1.9, fwState.VerticalSpeedMps, 3);
        Assert.Equal("GUIDED", fwState.ModeName);
        Assert.True(fwState.IsArmed);
        Assert.False(fwState.LinkLost);
        Assert.Equal("SİHA / SABİT KANAT", fwState.VehicleLabel);

        HudRenderState multirotorState = HudProjection.FromSnapshot(snapshot, new MultirotorProfile());
        Assert.True(multirotorState.ShowHorizon);
        Assert.False(multirotorState.ShowAirspeed);
        Assert.True(multirotorState.ShowAltitudeTape);
        Assert.False(multirotorState.ShowVtolTransition);
        Assert.False(multirotorState.SurfaceOnlyMode);

        HudRenderState vtolState = HudProjection.FromSnapshot(snapshot, new VtolProfile());
        Assert.True(vtolState.ShowHorizon);
        Assert.True(vtolState.ShowAirspeed);
        Assert.True(vtolState.ShowAltitudeTape);
        Assert.True(vtolState.ShowVtolTransition);
        Assert.False(vtolState.SurfaceOnlyMode);

        var boatSnapshot = new TelemetrySnapshot(
            IsConnected: true,
            LinkLost: true,
            VehicleType: MavType.SurfaceBoat,
            Autopilot: MavAutopilot.ArduPilotMega,
            VehicleDisplayLabel: "İDA / YÜZEY",
            VehicleCategory: VehicleCategory.BoatIda,
            ModeName: "STEERING",
            IsArmed: false,
            GroundspeedMps: 6.7,
            HeadingDeg: 92.0,
            RollDeg: -3.5);

        HudRenderState boatState = HudProjection.FromSnapshot(boatSnapshot, new BoatIdaProfile());
        Assert.False(boatState.ShowHorizon);
        Assert.False(boatState.ShowAirspeed);
        Assert.False(boatState.ShowAltitudeTape);
        Assert.False(boatState.ShowVtolTransition);
        Assert.True(boatState.SurfaceOnlyMode);
        Assert.Equal("İDA / YÜZEY", boatState.VehicleLabel);
        Assert.Equal("STEERING", boatState.ModeName);
        Assert.True(boatState.LinkLost);
        Assert.False(boatState.IsArmed);

        HudRenderState roverState = HudProjection.FromSnapshot(TelemetrySnapshot.Empty, new RoverIkaProfile());
        Assert.False(roverState.ShowHorizon);
        Assert.False(roverState.ShowAirspeed);
        Assert.False(roverState.ShowAltitudeTape);
        Assert.False(roverState.ShowVtolTransition);
        Assert.True(roverState.SurfaceOnlyMode);
        Assert.Equal("İKA / ROVER", roverState.VehicleLabel);

        // Null profile falls back cleanly from snapshot category and TelemetrySnapshot.Empty
        HudRenderState inferredBoat = HudProjection.FromSnapshot(boatSnapshot, null);
        Assert.True(inferredBoat.SurfaceOnlyMode);
        Assert.False(inferredBoat.ShowHorizon);
        Assert.False(inferredBoat.ShowAltitudeTape);

        HudRenderState emptyState = HudProjection.FromSnapshot(TelemetrySnapshot.Empty, null);
        Assert.Equal("NO VEHICLE", emptyState.VehicleLabel);
        Assert.Equal("UNKNOWN", emptyState.ModeName);
        Assert.False(emptyState.SurfaceOnlyMode);
    }

    [Fact]
    public void HudProjection_ClampsPitchAndNormalizesHeadingWrapping359To0()
    {
        // Pitch ladder clamping [-45, 45]
        Assert.Equal(45.0, HudProjection.ClampPitch(75.0));
        Assert.Equal(-45.0, HudProjection.ClampPitch(-89.5));
        Assert.Equal(45.0, HudProjection.ClampPitch(45.0));
        Assert.Equal(-45.0, HudProjection.ClampPitch(-45.0));
        Assert.Equal(12.25, HudProjection.ClampPitch(12.25));
        Assert.Equal(0.0, HudProjection.ClampPitch(double.NaN));
        Assert.Equal(45.0, HudProjection.ClampPitch(double.PositiveInfinity));
        Assert.Equal(-45.0, HudProjection.ClampPitch(double.NegativeInfinity));

        // Heading normalization into [0, 360)
        Assert.Equal(0.0, HudProjection.NormalizeHeading(0.0));
        Assert.Equal(0.0, HudProjection.NormalizeHeading(360.0));
        Assert.Equal(5.5, HudProjection.NormalizeHeading(725.5), 6);
        Assert.Equal(359.0, HudProjection.NormalizeHeading(-1.0), 6);
        Assert.Equal(0.0, HudProjection.NormalizeHeading(-360.0));
        Assert.Equal(0.0, HudProjection.NormalizeHeading(double.NaN));

        double tinyNegative = HudProjection.NormalizeHeading(-1e-17);
        Assert.InRange(tinyNegative, 0.0, 360.0);
        Assert.NotEqual(360.0, tinyNegative);

        // Shortest signed angle difference in [-180, 180)
        Assert.Equal(1.0, HudProjection.ShortestAngleDiffDeg(0.0, 359.0), 6);
        Assert.Equal(-1.0, HudProjection.ShortestAngleDiffDeg(359.0, 0.0), 6);
        Assert.Equal(20.0, HudProjection.ShortestAngleDiffDeg(10.0, 350.0), 6);
        Assert.Equal(-20.0, HudProjection.ShortestAngleDiffDeg(350.0, 10.0), 6);
        Assert.Equal(-180.0, HudProjection.ShortestAngleDiffDeg(180.0, 0.0), 6);
        Assert.Equal(-180.0, HudProjection.ShortestAngleDiffDeg(0.0, 180.0), 6);

        // FromSnapshot applies pitch clamp, heading wrap, and finite sanitization
        var extremeSnapshot = new TelemetrySnapshot(
            PitchDeg: 68.0,
            HeadingDeg: 360.0,
            RollDeg: double.NaN,
            AirspeedMps: double.PositiveInfinity,
            GroundspeedMps: 12.0);
        HudRenderState projected = HudProjection.FromSnapshot(extremeSnapshot, new FixedWingProfile());
        Assert.Equal(45.0, projected.PitchDeg);
        Assert.Equal(0.0, projected.HeadingDeg);
        Assert.Equal(0.0, projected.RollDeg);
        Assert.Equal(0.0, projected.AirspeedMps);
        Assert.Equal(12.0, projected.GroundspeedMps);
    }

    [Fact]
    public void SpaceXTheme_LoadsAllDesignTokensAndStylesWithoutDropShadows()
    {
        RunOnStaThread(() =>
        {
            _ = PackUriHelper.UriSchemePack;

            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Tulpar.App;component/Themes/SpaceXTheme.xaml", UriKind.Absolute)
            };

            AssertBrushColor(theme, "CanvasNightBrush", Color.FromRgb(0x00, 0x00, 0x00));
            AssertBrushColor(theme, "CanvasNightSoftBrush", Color.FromRgb(0x0A, 0x0A, 0x0A));
            AssertBrushColor(theme, "HairlineDarkBrush", Color.FromRgb(0x3A, 0x3A, 0x3F));
            AssertBrushColor(theme, "OnPrimaryBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            AssertBrushColor(theme, "OnPrimaryMuteBrush", Color.FromRgb(0xF0, 0xF0, 0xFA));
            AssertBrushColor(theme, "InkMuteBrush", Color.FromRgb(0x5A, 0x5A, 0x5F));
            AssertBrushColor(theme, "HazardRedBrush", Color.FromRgb(0xFF, 0x3B, 0x30));
            AssertBrushColor(theme, "WarningAmberBrush", Color.FromRgb(0xFF, 0xCC, 0x00));

            var displayFont = Assert.IsType<FontFamily>(theme["DisplayFontFamily"]);
            var uiFont = Assert.IsType<FontFamily>(theme["UiFontFamily"]);
            Assert.Contains("Arial Narrow", displayFont.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Arial", uiFont.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Verdana", uiFont.Source, StringComparison.OrdinalIgnoreCase);

            ICollection<FontFamily> embeddedFamilies = Fonts.GetFontFamilies(
                new Uri("pack://application:,,,/Tulpar.App;component/Assets/Fonts/", UriKind.Absolute));
            Assert.Contains(embeddedFamilies, f => f.Source.Contains("D-DIN", StringComparison.OrdinalIgnoreCase));

            var ghostBtnStyle = Assert.IsType<Style>(theme["GhostPillButtonStyle"]);
            var dangerBtnStyle = Assert.IsType<Style>(theme["DangerGhostPillButtonStyle"]);
            var textBoxStyle = Assert.IsType<Style>(theme["SpaceXTextBoxStyle"]);
            var comboBoxStyle = Assert.IsType<Style>(theme["SpaceXComboBoxStyle"]);
            var microCapStyle = Assert.IsType<Style>(theme["MicroCapTextStyle"]);
            var displayTextStyle = Assert.IsType<Style>(theme["DisplayTelemetryTextStyle"]);
            var panelStyle = Assert.IsType<Style>(theme["HairlinePanelStyle"]);

            var button = new Button { Style = ghostBtnStyle, Content = "connect" };
            var dangerButton = new Button { Style = dangerBtnStyle, Content = "rtl" };
            var textBox = new TextBox { Style = textBoxStyle, Text = "14550" };
            var comboBox = new ComboBox { Style = comboBoxStyle, ItemsSource = new[] { "GUIDED", "AUTO", "RTL" }, SelectedIndex = 0 };
            var microText = new TextBlock { Style = microCapStyle, Text = "TELEMETRİ" };
            var displayText = new TextBlock { Style = displayTextStyle, Text = "GUIDED" };
            var panel = new Border { Style = panelStyle };

            button.ApplyTemplate();
            dangerButton.ApplyTemplate();
            textBox.ApplyTemplate();
            comboBox.ApplyTemplate();
            button.Measure(new Size(140, 36));
            button.Arrange(new Rect(0, 0, 140, 36));
            dangerButton.Measure(new Size(140, 36));
            dangerButton.Arrange(new Rect(0, 0, 140, 36));
            comboBox.Measure(new Size(140, 32));
            comboBox.Arrange(new Rect(0, 0, 140, 32));

            ContentPresenter? buttonPresenter = FindVisualChild<ContentPresenter>(button);
            ContentPresenter? dangerPresenter = FindVisualChild<ContentPresenter>(dangerButton);
            Assert.NotNull(buttonPresenter);
            Assert.NotNull(dangerPresenter);
            Assert.Equal("CONNECT", buttonPresenter!.Content);
            Assert.Equal("RTL", dangerPresenter!.Content);

            var customContent = new StackPanel();
            var customButton = new Button { Style = ghostBtnStyle, Content = customContent };
            customButton.ApplyTemplate();
            ContentPresenter? customPresenter = FindVisualChild<ContentPresenter>(customButton);
            Assert.NotNull(customPresenter);
            Assert.Same(customContent, customPresenter!.Content);

            Assert.Null(button.Effect);
            Assert.Null(dangerButton.Effect);
            Assert.Null(textBox.Effect);
            Assert.Null(comboBox.Effect);
            Assert.Null(microText.Effect);
            Assert.Null(displayText.Effect);
            Assert.Null(panel.Effect);

            Assert.Equal(Color.FromRgb(0x00, 0x00, 0x00), ((SolidColorBrush)button.Background).Color);
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), ((SolidColorBrush)button.BorderBrush).Color);
            Assert.Equal(Color.FromRgb(0xFF, 0x3B, 0x30), ((SolidColorBrush)dangerButton.BorderBrush).Color);
            Assert.Equal(Color.FromRgb(0x0A, 0x0A, 0x0A), ((SolidColorBrush)textBox.Background).Color);
            Assert.Equal(Color.FromRgb(0x3A, 0x3A, 0x3F), ((SolidColorBrush)textBox.BorderBrush).Color);
            Assert.Equal(12.0, microText.FontSize);
            Assert.Equal(Color.FromRgb(0xF0, 0xF0, 0xFA), ((SolidColorBrush)microText.Foreground).Color);
            Assert.Equal(FontWeights.Bold, displayText.FontWeight);
            Assert.Equal(new CornerRadius(4), panel.CornerRadius);
            Assert.Equal(new Thickness(1), panel.BorderThickness);
            Assert.Equal(Color.FromRgb(0x0A, 0x0A, 0x0A), ((SolidColorBrush)panel.Background).Color);
            Assert.Equal(Color.FromRgb(0x3A, 0x3A, 0x3F), ((SolidColorBrush)panel.BorderBrush).Color);
        });
    }

    [Fact]
    public void HudControl_RendersAllVehicleCategoriesAndAlertStatesCleanly()
    {
        RunOnStaThread(() =>
        {
            var hud = new HudControl { Width = 360, Height = 260 };

            var scenarios = new (TelemetrySnapshot? Snapshot, IVehicleProfile? Profile)[]
            {
                (null, null),
                (
                    new TelemetrySnapshot(
                        IsConnected: true,
                        ModeName: "GUIDED",
                        IsArmed: true,
                        AirspeedMps: 26.4,
                        GroundspeedMps: 22.1,
                        AltitudeRelativeMeters: 145.0,
                        VerticalSpeedMps: 2.3,
                        HeadingDeg: 359.5,
                        RollDeg: -25.0,
                        PitchDeg: 60.0),
                    new FixedWingProfile()
                ),
                (
                    new TelemetrySnapshot(
                        IsConnected: true,
                        ModeName: "QHOVER",
                        IsArmed: true,
                        AirspeedMps: 12.0,
                        GroundspeedMps: 4.0,
                        AltitudeRelativeMeters: 35.0,
                        VerticalSpeedMps: 0.4,
                        HeadingDeg: 0.5,
                        RollDeg: 3.0,
                        PitchDeg: 2.0),
                    new VtolProfile()
                ),
                (
                    new TelemetrySnapshot(
                        IsConnected: true,
                        LinkLost: true,
                        ModeName: "LOITER",
                        IsArmed: false,
                        GroundspeedMps: 0.5,
                        AltitudeRelativeMeters: 42.0,
                        VerticalSpeedMps: -0.3,
                        HeadingDeg: 180.0,
                        RollDeg: 5.0,
                        PitchDeg: -15.0),
                    new MultirotorProfile(MavType.Quadrotor)
                ),
                (
                    new TelemetrySnapshot(
                        IsConnected: true,
                        ModeName: "STEERING",
                        IsArmed: true,
                        GroundspeedMps: 4.5,
                        HeadingDeg: 270.0,
                        RollDeg: 12.5),
                    new RoverIkaProfile()
                ),
                (
                    new TelemetrySnapshot(
                        IsConnected: true,
                        LinkLost: true,
                        ModeName: "AUTO",
                        IsArmed: true,
                        GroundspeedMps: 7.2,
                        HeadingDeg: 5.0,
                        RollDeg: -8.0),
                    new BoatIdaProfile()
                )
            };

            foreach (var (snapshot, profile) in scenarios)
            {
                hud.Snapshot = snapshot;
                hud.VehicleProfile = profile;
                hud.Measure(new Size(360, 260));
                hud.Arrange(new Rect(0, 0, 360, 260));

                var bitmap = new RenderTargetBitmap(360, 260, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(hud);

                byte[] pixels = new byte[360 * 260 * 4];
                bitmap.CopyPixels(pixels, 360 * 4, 0);

                // Verify instrument actually drew non-zero bright pixels (white/grey vectors on black)
                bool hasDrawnPixel = false;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 0 || pixels[i + 1] > 0 || pixels[i + 2] > 0)
                    {
                        hasDrawnPixel = true;
                        break;
                    }
                }

                Assert.True(hasDrawnPixel);
            }

            // Responsive sizing check: unconstrained Measure returns default aerospace aspect and renders cleanly at compact size
            var autoSizedHud = new HudControl();
            autoSizedHud.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(new Size(340, 250), autoSizedHud.DesiredSize);

            hud.Width = 220;
            hud.Height = 180;
            hud.Measure(new Size(220, 180));
            hud.Arrange(new Rect(0, 0, 220, 180));
            var compactBitmap = new RenderTargetBitmap(220, 180, 96, 96, PixelFormats.Pbgra32);
            compactBitmap.Render(hud);
            Assert.Equal(220, compactBitmap.PixelWidth);
        });
    }

    [Fact]
    public void HudControl_Compact220x180AndVtolBadge_UsesMonochromeOnlyAndAvoidsClipOverflows()
    {
        RunOnStaThread(() =>
        {
            var vtolHud = new HudControl { Width = 220, Height = 180 };
            vtolHud.Snapshot = new TelemetrySnapshot(
                IsConnected: true,
                LinkLost: false,
                VehicleDisplayLabel: "TULPAR HYBRID VTOL AIRFRAME",
                VehicleCategory: VehicleCategory.Vtol,
                ModeName: "AUTO_MISSION",
                IsArmed: false,
                AirspeedMps: 18.5,
                GroundspeedMps: 16.0,
                AltitudeRelativeMeters: 85.0,
                VerticalSpeedMps: 0.8,
                HeadingDeg: 358.0,
                RollDeg: 3.0,
                PitchDeg: 2.0);
            vtolHud.VehicleProfile = new VtolProfile();
            vtolHud.Measure(new Size(220, 180));
            vtolHud.Arrange(new Rect(0, 0, 220, 180));

            var vtolBitmap = new RenderTargetBitmap(220, 180, 96, 96, PixelFormats.Pbgra32);
            vtolBitmap.Render(vtolHud);

            byte[] pixels = new byte[220 * 180 * 4];
            vtolBitmap.CopyPixels(pixels, 220 * 4, 0);

            bool hasAmberOrRed = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                // Detect any amber (#FFCC00) or hazard red (#FF3B30) pixel when no alert is active
                if (r > 180 && (g < 80 || b < 40))
                {
                    hasAmberOrRed = true;
                    break;
                }
            }

            Assert.False(hasAmberOrRed);

            // Top header text boxes (VehicleLabel vs ModeName) must never horizontally overlap at 220px width
            var vtolGlyphBounds = CollectGlyphBounds(VisualTreeHelper.GetDrawing(vtolHud))
                .Where(b => b.Top < 28.0)
                .OrderBy(b => b.Left)
                .ToList();
            Assert.True(vtolGlyphBounds.Count >= 2);
            for (int i = 1; i < vtolGlyphBounds.Count; i++)
            {
                Assert.True(
                    vtolGlyphBounds[i - 1].Right <= vtolGlyphBounds[i].Left,
                    $"Top header text overlap at 220x180: {vtolGlyphBounds[i - 1]} vs {vtolGlyphBounds[i]}");
            }

            // Compact surface-only layout (220x180 and 180x160) must render cleanly without overlap or overflow
            var surfaceHud = new HudControl { Width = 220, Height = 180 };
            surfaceHud.Snapshot = new TelemetrySnapshot(
                IsConnected: true,
                LinkLost: false,
                VehicleDisplayLabel: "İDA / YÜZEY PLATFORMU",
                VehicleCategory: VehicleCategory.BoatIda,
                ModeName: "STEERING",
                IsArmed: false,
                GroundspeedMps: 7.2,
                HeadingDeg: 45.0,
                RollDeg: -8.0);
            surfaceHud.VehicleProfile = new BoatIdaProfile();
            surfaceHud.Measure(new Size(220, 180));
            surfaceHud.Arrange(new Rect(0, 0, 220, 180));

            var surfaceBitmap = new RenderTargetBitmap(220, 180, 96, 96, PixelFormats.Pbgra32);
            surfaceBitmap.Render(surfaceHud);
            Assert.Equal(220, surfaceBitmap.PixelWidth);

            List<Rect> surfaceGlyphs = CollectGlyphBounds(VisualTreeHelper.GetDrawing(surfaceHud));

            // Speed card labels at 220x180 (x < 70, y in instrument panel [58..118]) must not overlap vertically
            var speedCardGlyphs = surfaceGlyphs
                .Where(b => b.Left >= 8.0 && b.Right <= 72.0 && b.Top >= 58.0 && b.Bottom <= 120.0)
                .OrderBy(b => b.Top)
                .ToList();
            Assert.True(speedCardGlyphs.Count >= 2);
            for (int i = 1; i < speedCardGlyphs.Count; i++)
            {
                Assert.True(
                    speedCardGlyphs[i - 1].Bottom <= speedCardGlyphs[i].Top + 0.5,
                    $"Compact speed panel text overlap: {speedCardGlyphs[i - 1]} vs {speedCardGlyphs[i]}");
            }

            // Compass rose labels (N/E/S/W + HDG) at 220x180 must remain strictly inside roseArea [60..116]
            var roseGlyphs = surfaceGlyphs
                .Where(b => b.Left >= 74.0 && b.Top >= 56.0 && b.Bottom <= 122.0)
                .ToList();
            Assert.True(roseGlyphs.Count >= 4);
            foreach (Rect g in roseGlyphs)
            {
                Assert.True(g.Top >= 60.0 && g.Bottom <= 116.0, $"Rose label overflowed roseArea: {g}");
            }

            surfaceHud.Width = 180;
            surfaceHud.Height = 160;
            surfaceHud.Measure(new Size(180, 160));
            surfaceHud.Arrange(new Rect(0, 0, 180, 160));
            var ultraCompact = new RenderTargetBitmap(180, 160, 96, 96, PixelFormats.Pbgra32);
            ultraCompact.Render(surfaceHud);
            Assert.Equal(180, ultraCompact.PixelWidth);
        });
    }

    [Fact]
    public void HudProjection_ProjectsAndFormatsBatteryGpsThrottleAndWaypointOverlays()
    {
        var snapshot = new TelemetrySnapshot(
            IsConnected: true,
            VehicleCategory: VehicleCategory.FixedWing,
            ModeName: "AUTO",
            IsArmed: true,
            BatteryVoltageVolts: 22.8,
            BatteryRemainingPercent: 74,
            GpsFixType: GpsFixType.Fix3D,
            SatellitesVisible: 16,
            ThrottlePercent: 62,
            ActiveWaypointSeq: 4);

        HudRenderState state = HudProjection.FromSnapshot(snapshot, new FixedWingProfile());
        Assert.Equal(22.8, state.BatteryVoltageVolts, 3);
        Assert.Equal(74, state.BatteryRemainingPercent);
        Assert.Equal(GpsFixType.Fix3D, state.GpsFixType);
        Assert.Equal(16, state.SatellitesVisible);
        Assert.Equal(62, state.ThrottlePercent);
        Assert.Equal(4, state.ActiveWaypointSeq);

        Assert.Equal("22.8V / 74%", HudProjection.FormatBatteryReadout(state.BatteryVoltageVolts, state.BatteryRemainingPercent));
        Assert.Equal("3D / 16", HudProjection.FormatGpsReadout(state.GpsFixType, state.SatellitesVisible));
        Assert.Equal("THR %62", HudProjection.FormatThrottleReadout(state.ThrottlePercent));
        Assert.Equal("WP #4", HudProjection.FormatActiveWaypointReadout(state.ActiveWaypointSeq));

        // Edge sanitization: NaN voltage, out-of-range throttle/percent, negative WP
        var edgeSnapshot = new TelemetrySnapshot(
            BatteryVoltageVolts: double.NaN,
            BatteryRemainingPercent: 140,
            GpsFixType: GpsFixType.RtkFixed,
            SatellitesVisible: -3,
            ThrottlePercent: 150,
            ActiveWaypointSeq: -1);

        HudRenderState edgeState = HudProjection.FromSnapshot(edgeSnapshot, null);
        Assert.Equal(0.0, edgeState.BatteryVoltageVolts);
        Assert.Equal(100, edgeState.BatteryRemainingPercent);
        Assert.Equal(GpsFixType.RtkFixed, edgeState.GpsFixType);
        Assert.Equal(0, edgeState.SatellitesVisible);
        Assert.Equal(100, edgeState.ThrottlePercent);
        Assert.Equal(-1, edgeState.ActiveWaypointSeq);
        Assert.Equal("--V / 100%", HudProjection.FormatBatteryReadout(edgeState.BatteryVoltageVolts, edgeState.BatteryRemainingPercent));
        Assert.Equal("RTK FIX / 0", HudProjection.FormatGpsReadout(edgeState.GpsFixType, edgeState.SatellitesVisible));
        Assert.Equal("THR %100", HudProjection.FormatThrottleReadout(edgeState.ThrottlePercent));
        Assert.Equal(string.Empty, HudProjection.FormatActiveWaypointReadout(edgeState.ActiveWaypointSeq));
        Assert.Equal("--V / --%", HudProjection.FormatBatteryReadout(0.0, -1));

        // Multi-frame HudControl render pass with cached geometries/transforms
        RunOnStaThread(() =>
        {
            var hud = new HudControl { Width = 360, Height = 260, Snapshot = snapshot, VehicleProfile = new FixedWingProfile() };
            hud.Measure(new Size(360, 260));
            hud.Arrange(new Rect(0, 0, 360, 260));

            var bmp1 = new RenderTargetBitmap(360, 260, 96, 96, PixelFormats.Pbgra32);
            bmp1.Render(hud);

            hud.Snapshot = snapshot with { RollDeg = 18.0, PitchDeg = 6.0, HeadingDeg = 90.0, ActiveWaypointSeq = -1 };
            hud.Measure(new Size(360, 260));
            hud.Arrange(new Rect(0, 0, 360, 260));
            var bmp2 = new RenderTargetBitmap(360, 260, 96, 96, PixelFormats.Pbgra32);
            bmp2.Render(hud);

            List<Rect> glyphs = CollectGlyphBounds(VisualTreeHelper.GetDrawing(hud));
            Assert.True(glyphs.Count > 0);
        });
    }

    [Fact]
    public void ElevationProfileControl_RendersEmptyAndPopulatedFlightMetricsCleanlyOnSta()
    {
        RunOnStaThread(() =>
        {
            var control = new ElevationProfileControl();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(new Size(360, 110), control.DesiredSize);

            // Empty / null state renders centered placeholder cleanly
            control.Width = 360;
            control.Height = 110;
            control.Measure(new Size(360, 110));
            control.Arrange(new Rect(0, 0, 360, 110));

            var emptyBmp = new RenderTargetBitmap(360, 110, 96, 96, PixelFormats.Pbgra32);
            emptyBmp.Render(control);
            List<Rect> emptyGlyphs = CollectGlyphBounds(VisualTreeHelper.GetDrawing(control));
            Assert.NotEmpty(emptyGlyphs);

            // Single-point state also shows placeholder
            var singleItem = new MissionItem[]
            {
                new(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavTakeoff, 0f, 0f, 0f, 0f, 39.9200, 32.8540, 25f)
            };
            FlightMetricsSummary singleSummary = FlightMetricsEstimator.Estimate(singleItem, cruiseSpeedMps: 18.0);
            control.ProfileLegs = singleSummary.Legs;
            control.MissionItems = singleItem;
            control.Measure(new Size(360, 110));
            control.Arrange(new Rect(0, 0, 360, 110));
            var singleBmp = new RenderTargetBitmap(360, 110, 96, 96, PixelFormats.Pbgra32);
            singleBmp.Render(control);
            Assert.NotEmpty(CollectGlyphBounds(VisualTreeHelper.GetDrawing(control)));

            // Full mission from FlightMetricsEstimator with Takeoff, Waypoints, RTL, and SelectedSeq
            var missionItems = new MissionItem[]
            {
                new(0, MavFrame.GlobalRelativeAltInt, MavCmd.NavTakeoff, 15f, 0f, 0f, 0f, 39.9200, 32.8540, 25f),
                new(1, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, 39.9240, 32.8580, 95f),
                new(2, MavFrame.GlobalRelativeAltInt, MavCmd.NavWaypoint, 0f, 0f, 0f, 0f, 39.9280, 32.8620, 140f),
                new(3, MavFrame.GlobalRelativeAltInt, MavCmd.NavReturnToLaunch, 0f, 0f, 0f, 0f, 39.9200, 32.8540, 40f)
            };
            FlightMetricsSummary summary = FlightMetricsEstimator.Estimate(missionItems, cruiseSpeedMps: 18.0);

            control.ProfileLegs = summary.Legs;
            control.MissionItems = missionItems;
            control.SelectedSeq = 2;
            control.Measure(new Size(360, 110));
            control.Arrange(new Rect(0, 0, 360, 110));

            var fullBmp = new RenderTargetBitmap(360, 110, 96, 96, PixelFormats.Pbgra32);
            fullBmp.Render(control);

            byte[] pixels = new byte[360 * 110 * 4];
            fullBmp.CopyPixels(pixels, 360 * 4, 0);

            bool hasSelectedSkyBlue = false;
            bool hasTakeoffGreen = false;
            bool hasRtlAmber = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                // #38BDF8 (R=56, G=189, B=248)
                if (b > 200 && g > 140 && r < 100)
                {
                    hasSelectedSkyBlue = true;
                }

                // #34C759 (R=52, G=199, B=89)
                if (g > 160 && r < 90 && b < 120)
                {
                    hasTakeoffGreen = true;
                }

                // #FFCC00 (R=255, G=204, B=0)
                if (r > 220 && g > 170 && b < 30)
                {
                    hasRtlAmber = true;
                }
            }

            Assert.True(hasSelectedSkyBlue, "Expected #38BDF8 selected waypoint halo/label pixels.");
            Assert.True(hasTakeoffGreen, "Expected #34C759 takeoff vertex pixels.");
            Assert.True(hasRtlAmber, "Expected #FFCC00 RTL/Land vertex pixels.");

            List<Rect> populatedGlyphs = CollectGlyphBounds(VisualTreeHelper.GetDrawing(control));
            Assert.True(populatedGlyphs.Count >= 4);
        });
    }

    private static List<Rect> CollectGlyphBounds(Drawing? drawing)
    {
        var results = new List<Rect>();
        CollectGlyphBoundsRecursive(drawing, results);
        return results;
    }

    private static void CollectGlyphBoundsRecursive(Drawing? drawing, List<Rect> results)
    {
        if (drawing is GlyphRunDrawing glyph)
        {
            results.Add(glyph.Bounds);
        }
        else if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
            {
                CollectGlyphBoundsRecursive(child, results);
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void AssertBrushColor(ResourceDictionary dict, string key, Color expected)
    {
        var brush = Assert.IsType<SolidColorBrush>(dict[key]);
        Assert.Equal(expected, brush.Color);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
