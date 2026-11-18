using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tulpar.App.Services;

namespace Tulpar.App.Controls;

public partial class SafetyConfirmPopupWindow : Window
{
    public SafetyConfirmPopupWindow(
        SafetyActionRequest request,
        string currentMode,
        bool isArmed)
    {
        ArgumentNullException.ThrowIfNull(request);

        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);

        TitleText.Text = string.IsNullOrWhiteSpace(request.Title)
            ? "GÜVENLİK ONAYI GEREKLİ"
            : request.Title;
        DescriptionText.Text = request.Description;
        ModeBadgeText.Text = string.IsNullOrWhiteSpace(currentMode) ? "BİLİNMİYOR" : currentMode;

        ArmBadgeText.Text = isArmed ? "ARMED" : "DISARMED";
        ArmBadgeText.Foreground = new SolidColorBrush(
            isArmed
                ? Color.FromRgb(0x34, 0xC7, 0x59)
                : Color.FromRgb(0xFF, 0x3B, 0x30));

        if (request.AltitudeMeters > 0f)
        {
            AltitudePanel.Visibility = Visibility.Visible;
            AltitudeBadgeText.Text = string.Create(CultureInfo.InvariantCulture, $"{request.AltitudeMeters:0.0} M");
        }

        // Default focus stays on Cancel so accidental Space/Enter never sends a flight command.
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
