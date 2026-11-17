using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tulpar.App.Controls;
using Tulpar.Core.Mavlink;
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

            var button = new Button { Style = ghostBtnStyle, Content = "CONNECT" };
            var dangerButton = new Button { Style = dangerBtnStyle, Content = "RTL" };
            var textBox = new TextBox { Style = textBoxStyle, Text = "14550" };
            var comboBox = new ComboBox { Style = comboBoxStyle, ItemsSource = new[] { "GUIDED", "AUTO", "RTL" }, SelectedIndex = 0 };
            var microText = new TextBlock { Style = microCapStyle, Text = "TELEMETRİ" };
            var displayText = new TextBlock { Style = displayTextStyle, Text = "GUIDED" };
            var panel = new Border { Style = panelStyle };

            button.ApplyTemplate();
            dangerButton.ApplyTemplate();
            textBox.ApplyTemplate();
            comboBox.ApplyTemplate();
            comboBox.Measure(new Size(140, 32));
            comboBox.Arrange(new Rect(0, 0, 140, 32));

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
