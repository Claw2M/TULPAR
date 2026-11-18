using System.Windows;

namespace Tulpar.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();
        splash.SetProgress(15, "ÇEKİRDEK MODÜLLER YÜKLENİYOR...");

        try
        {
            await Task.Delay(260);
            splash.SetProgress(45, "HARİTA VE TELEMETRİ MOTORU BAŞLATILIYOR...");
            await Task.Delay(220);

            var mainWindow = new MainWindow();
            splash.SetProgress(82, "ARAYÜZ BİLEŞENLERİ HAZIRLANIYOR...");
            await Task.Delay(260);

            splash.SetProgress(100, "HAZIR");
            await Task.Delay(180);

            MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();

            await splash.FadeOutAndCloseAsync();
        }
        catch (Exception ex)
        {
            splash.Close();
            MessageBox.Show(
                $"TULPAR başlatılırken beklenmeyen bir hata oluştu:\n{ex.Message}",
                "TULPAR Başlatma Hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
