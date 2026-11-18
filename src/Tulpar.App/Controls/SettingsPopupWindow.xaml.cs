using System.Windows;
using System.Windows.Input;
using Tulpar.App.Services;
using Tulpar.App.ViewModels;

namespace Tulpar.App.Controls;

public partial class SettingsPopupWindow : Window
{
    private readonly Action _openOfflineMapAction;

    public SettingsPopupWindow(MainViewModel viewModel, Action openOfflineMapAction)
    {
        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _openOfflineMapAction = openOfflineMapAction ?? throw new ArgumentNullException(nameof(openOfflineMapAction));

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void OnOpenOfflineMapClick(object sender, RoutedEventArgs e)
    {
        _openOfflineMapAction();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
