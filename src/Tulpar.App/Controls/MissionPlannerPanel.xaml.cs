using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Tulpar.App.Services;
using Tulpar.App.ViewModels;
using Tulpar.Core.Mavlink;

namespace Tulpar.App.Controls;

/// <summary>
/// Bottom mission and geofence editing drawer supporting visual waypoint node flow
/// with floating parameter popups, ready flight pattern schematics, loop-closing,
/// optional tabular DataGrid editing, MAVLink transfers, and WPL 110 / Tulpar JSON I/O.
/// </summary>
public partial class MissionPlannerPanel : UserControl
{
    private MainViewModel? _viewModel;

    public MissionPlannerPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.MissionWaypointInspectRequested -= OnMissionWaypointInspectRequested;
        }

        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.MissionWaypointInspectRequested += OnMissionWaypointInspectRequested;
        }
    }

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (VisualRouteScroll is null || WaypointDataGrid is null)
        {
            return;
        }

        bool visualMode = VisualModeToggle?.IsChecked == true;
        VisualRouteScroll.Visibility = visualMode ? Visibility.Visible : Visibility.Collapsed;
        WaypointDataGrid.Visibility = visualMode ? Visibility.Collapsed : Visibility.Visible;
        if (!visualMode && WaypointSettingsPopup is not null)
        {
            WaypointSettingsPopup.IsOpen = false;
        }
    }

    private void OnWaypointCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement card || card.DataContext is not EditableMissionItem item)
        {
            return;
        }

        _viewModel.SelectedMissionItem = item;
        OpenWaypointPopup(item, card);
    }

    private void OnCardDeleteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || sender is not FrameworkElement btn || btn.DataContext is not EditableMissionItem item)
        {
            return;
        }

        _viewModel.SelectedMissionItem = item;
        WaypointSettingsPopup.IsOpen = false;
        _viewModel.RemoveSelectedMissionWaypoint();
    }

    private void OnMissionWaypointInspectRequested(EditableMissionItem item)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!_viewModel.IsMissionDrawerExpanded)
        {
            _viewModel.IsMissionDrawerExpanded = true;
        }

        _viewModel.SelectedMissionItem = item;

        Dispatcher.BeginInvoke(() =>
        {
            FrameworkElement? container = VisualRouteItems.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container is not null)
            {
                container.BringIntoView();
                OpenWaypointPopup(item, container);
            }
            else
            {
                OpenWaypointPopup(item, RouteHostGrid);
            }
        });
    }

    private void OpenWaypointPopup(EditableMissionItem item, FrameworkElement anchor)
    {
        MissionToolsPopup.IsOpen = false;
        LoopMissionPopup.IsOpen = false;
        PatternTemplatePopup.IsOpen = false;
        WaypointSettingsPopup.PlacementTarget = anchor;
        WaypointSettingsPopup.IsOpen = false;
        WaypointSettingsPopup.IsOpen = true;
    }

    private void OnElevationToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ElevationProfileHost is null)
        {
            return;
        }

        ElevationProfileHost.Visibility = ElevationToggleBtn?.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPopupDeleteClick(object sender, RoutedEventArgs e)
    {
        WaypointSettingsPopup.IsOpen = false;
        _viewModel?.RemoveSelectedMissionWaypoint();
    }

    private void OnClosePopupClick(object sender, RoutedEventArgs e)
    {
        // Force focused TextBox inside popup to commit any edited value before closing.
        if (Keyboard.FocusedElement is UIElement focused)
        {
            focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        WaypointSettingsPopup.IsOpen = false;
    }

    private void OnOpenMissionToolsPopupClick(object sender, RoutedEventArgs e)
    {
        WaypointSettingsPopup.IsOpen = false;
        LoopMissionPopup.IsOpen = false;
        PatternTemplatePopup.IsOpen = false;
        MissionToolsPopup.IsOpen = !MissionToolsPopup.IsOpen;
    }

    private void OnOpenLoopPopupClick(object sender, RoutedEventArgs e)
    {
        MissionToolsPopup.IsOpen = false;
        WaypointSettingsPopup.IsOpen = false;
        PatternTemplatePopup.IsOpen = false;
        LoopMissionPopup.IsOpen = !LoopMissionPopup.IsOpen;
    }

    private void OnOpenTemplatesPopupClick(object sender, RoutedEventArgs e)
    {
        MissionToolsPopup.IsOpen = false;
        WaypointSettingsPopup.IsOpen = false;
        LoopMissionPopup.IsOpen = false;

        if (_viewModel is not null)
        {
            PatternAltitudeBox.Text = _viewModel.DefaultWaypointAltitudeMeters.ToString("0.#", CultureInfo.InvariantCulture);
            PatternUseFenceCheck.IsChecked = false;
        }

        PatternTemplatePopup.IsOpen = !PatternTemplatePopup.IsOpen;
    }

    private void OnCloseAuxiliaryPopupsClick(object sender, RoutedEventArgs e)
    {
        MissionToolsPopup.IsOpen = false;
        LoopMissionPopup.IsOpen = false;
        PatternTemplatePopup.IsOpen = false;
    }

    private void OnQuickLoopZeroClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ConnectLastWaypointToTarget(targetSeq: 0, repeatCount: -1, useDoJump: true);
        LoopMissionPopup.IsOpen = false;
    }

    private void OnApplyLoopClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        int targetSeq = ParseIntOrDefault(LoopTargetSeqBox.Text, 0);
        int repeatCount = ParseIntOrDefault(LoopRepeatCountBox.Text, -1);
        bool useDoJump = LoopUseDoJumpRadio.IsChecked == true;

        if (_viewModel.ConnectLastWaypointToTarget(targetSeq, repeatCount, useDoJump))
        {
            LoopMissionPopup.IsOpen = false;
        }
    }

    private void OnPatternChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (PatternParamALabel is null || PatternParamBLabel is null || PatternParamCLabel is null || PatternParamDLabel is null)
        {
            return;
        }

        if (PatternAreaScanRadio?.IsChecked == true)
        {
            PatternParamALabel.Text = "ALAN GENİŞLİĞİ (M)";
            PatternParamBLabel.Text = "ALAN YÜKSEKLİĞİ (M)";
            PatternParamCLabel.Text = "HAT ARALIĞI (M)";
            PatternParamDLabel.Text = "TARAMA NOKTASI (OTO)";
            PatternParamABox.Text = "200";
            PatternParamBBox.Text = "140";
            PatternParamCBox.Text = "35";
        }
        else if (PatternOrbitRadio?.IsChecked == true)
        {
            PatternParamALabel.Text = "YÖRÜNGE YARIÇAPI (M)";
            PatternParamBLabel.Text = "BİTİŞ YARIÇAPI (SABİT)";
            PatternParamCLabel.Text = "TUR SAYISI";
            PatternParamDLabel.Text = "ÇEMBER NOKTA SAYISI";
            PatternParamABox.Text = "60";
            PatternParamBBox.Text = "60";
            PatternParamCBox.Text = "1";
        }
        else if (PatternShrinkingRadio?.IsChecked == true)
        {
            PatternParamALabel.Text = "DIŞ YARIÇAP (M)";
            PatternParamBLabel.Text = "İÇ YARIÇAP (M)";
            PatternParamCLabel.Text = "TUR SAYISI";
            PatternParamDLabel.Text = "TUR NOKTA SAYISI";
            PatternParamABox.Text = "120";
            PatternParamBBox.Text = "25";
            PatternParamCBox.Text = "3";
        }
        else
        {
            PatternParamALabel.Text = "BAŞL. YARIÇAP (M)";
            PatternParamBLabel.Text = "BİTİŞ YARIÇAP (M)";
            PatternParamCLabel.Text = "TUR SAYISI";
            PatternParamDLabel.Text = "TUR NOKTA SAYISI";
            PatternParamABox.Text = "25";
            PatternParamBBox.Text = "120";
            PatternParamCBox.Text = "3";
        }
    }

    private void OnGeneratePatternClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MissionPatternKind kind = MissionPatternKind.ExpandingSpiral;
        if (PatternShrinkingRadio.IsChecked == true)
        {
            kind = MissionPatternKind.ShrinkingSpiral;
        }
        else if (PatternOrbitRadio.IsChecked == true)
        {
            kind = MissionPatternKind.CircularOrbit;
        }
        else if (PatternAreaScanRadio.IsChecked == true)
        {
            kind = MissionPatternKind.PolygonGridScan;
        }

        double paramA = ParseDoubleOrDefault(PatternParamABox.Text, 25.0);
        double paramB = ParseDoubleOrDefault(PatternParamBBox.Text, 120.0);
        double paramC = ParseDoubleOrDefault(PatternParamCBox.Text, kind == MissionPatternKind.PolygonGridScan ? 35.0 : 3.0);
        int paramD = Math.Max(6, ParseIntOrDefault(PatternParamDBox.Text, 12));
        float altitude = (float)Math.Clamp(ParseDoubleOrDefault(PatternAltitudeBox.Text, _viewModel.DefaultWaypointAltitudeMeters), 1.0, 5000.0);
        double angleDeg = ParseDoubleOrDefault(PatternAngleBox.Text, 0.0);
        (double centerLat, double centerLon) = _viewModel.ResolveDefaultPatternCenter();

        IReadOnlyList<(double Lat, double Lon)>? polyVerts = null;
        if (kind == MissionPatternKind.PolygonGridScan && PatternUseFenceCheck.IsChecked == true)
        {
            if (_viewModel.FencePoints.Count >= 3)
            {
                polyVerts = _viewModel.FencePoints.Select(f => (f.Latitude, f.Longitude)).ToList();
            }
            else
            {
                _viewModel.AppendSystemLog(MavSeverity.Warning, "ROTA", "Geofence alanı taranamadı: en az 3 Geofence köşesi gerekir; dikdörtgen alan kullanılıyor.");
            }
        }

        MavFrame frame = string.Equals(_viewModel.DefaultMissionFrameLabel, "AMSL", StringComparison.OrdinalIgnoreCase)
            ? MavFrame.GlobalInt
            : MavFrame.GlobalRelativeAltInt;

        var request = new MissionPatternRequest(
            Kind: kind,
            CenterLat: centerLat,
            CenterLon: centerLon,
            AltitudeMeters: altitude,
            Frame: frame,
            StartRadiusMeters: paramA,
            EndRadiusMeters: paramB,
            Turns: paramC,
            PointsPerTurn: paramD,
            AreaWidthMeters: paramA,
            AreaHeightMeters: paramB,
            GridSpacingMeters: paramC,
            GridAngleDeg: angleDeg,
            Clockwise: true,
            IncludeTakeoff: PatternIncludeTakeoffCheck.IsChecked == true,
            CloseLoopWithJump: PatternCloseLoopCheck.IsChecked == true,
            PolygonVertices: polyVerts);

        int count = _viewModel.ApplyMissionPattern(request, replaceExisting: PatternReplaceCheck.IsChecked == true);
        if (count > 0)
        {
            PatternTemplatePopup.IsOpen = false;
        }
    }

    private static double ParseDoubleOrDefault(string? raw, double fallback)
    {
        string normalized = (raw ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }

    private static int ParseIntOrDefault(string? raw, int fallback) =>
        int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private void OnImportMissionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Görev Dosyası Aç",
            Filter = "Görev Dosyaları (*.waypoints;*.tulpar.json;*.json)|*.waypoints;*.tulpar.json;*.json|QGC WPL 110 (*.waypoints)|*.waypoints|Tulpar JSON (*.json)|*.json|Tüm Dosyalar (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                vm.ImportMissionJson(content);
            }
            else
            {
                vm.ImportWpl110(content);
            }
        }
        catch (Exception ex)
        {
            vm.AppendSystemLog(MavSeverity.Error, "DOSYA", $"Görev dosyası açılamadı: {ex.Message}");
        }
    }

    private void OnExportMissionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Görev Dosyası Kaydet",
            Filter = "QGC WPL 110 (*.waypoints)|*.waypoints|Tulpar JSON (*.tulpar.json)|*.tulpar.json",
            DefaultExt = ".waypoints",
            FileName = "tulpar_mission.waypoints"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            string content = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? vm.ExportMissionJson()
                : vm.ExportWpl110();
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            vm.AppendSystemLog(MavSeverity.Info, "DOSYA", $"Görev kaydedildi: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            vm.AppendSystemLog(MavSeverity.Error, "DOSYA", $"Görev kaydedilemedi: {ex.Message}");
        }
    }
}
