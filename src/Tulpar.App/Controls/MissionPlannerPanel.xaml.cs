using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tulpar.App.ViewModels;
using Tulpar.Core.Mavlink;

namespace Tulpar.App.Controls;

/// <summary>
/// Bottom mission and geofence editing drawer supporting waypoint/fence table editing,
/// adaptive command palettes, MAVLink protocol transfers, and WPL 110 / Tulpar JSON file I/O.
/// </summary>
public partial class MissionPlannerPanel : UserControl
{
    public MissionPlannerPanel()
    {
        InitializeComponent();
    }

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
