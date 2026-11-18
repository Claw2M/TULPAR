using System.Windows;
using System.Windows.Input;
using Tulpar.App.Services;
using Tulpar.App.ViewModels;

namespace Tulpar.App.Controls;

/// <summary>
/// Spacious, high-contrast log inspection window for monitoring system events cleanly.
/// </summary>
public partial class LogPopupWindow : Window
{
    public LogPopupWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, vm.Logs.Select(l => l.DisplayLine));
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
