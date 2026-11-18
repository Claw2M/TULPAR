using System.Windows;
using System.Windows.Media.Animation;

namespace Tulpar.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void SetProgress(double percent, string statusText)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        BootProgress.Value = clamped;
        PercentText.Text = $"%{clamped:0}";
        StatusText.Text = statusText;
    }

    public async Task FadeOutAndCloseAsync()
    {
        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180))
        {
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(OpacityProperty, anim);
        await Task.Delay(185);
        Close();
    }
}
