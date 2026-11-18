using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Tulpar.App.Services;
using Tulpar.MapHost;

namespace Tulpar.App.Controls;

/// <summary>
/// Modal dialog for pre-flight offline region caching, external raster .mbtiles archive import,
/// and custom XYZ tile template registration.
/// </summary>
public partial class OfflineMapDialog : Window
{
    private readonly MapHttpServer _mapServer;
    private readonly OfflineRegionDownloader _downloader;
    private readonly Action<string?> _onLayersUpdated;
    private readonly Action? _onRequestBoundsPickFromMap;
    private CancellationTokenSource? _downloadCts;
    private bool _isReady;
    private bool _isClosed;

    public OfflineMapDialog(
        MapHttpServer mapServer,
        OfflineRegionDownloader downloader,
        MapBounds? initialBounds,
        Action<string?> onLayersUpdated,
        Action? onRequestBoundsPickFromMap = null)
    {
        _mapServer = mapServer ?? throw new ArgumentNullException(nameof(mapServer));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _onLayersUpdated = onLayersUpdated ?? throw new ArgumentNullException(nameof(onLayersUpdated));
        _onRequestBoundsPickFromMap = onRequestBoundsPickFromMap;

        InitializeComponent();
        WindowChromeHelper.EnableDarkTitleBar(this);

        if (initialBounds is { } bounds)
        {
            MinLatBox.Text = bounds.MinLat.ToString("0.000000", CultureInfo.InvariantCulture);
            MaxLatBox.Text = bounds.MaxLat.ToString("0.000000", CultureInfo.InvariantCulture);
            MinLonBox.Text = bounds.MinLon.ToString("0.000000", CultureInfo.InvariantCulture);
            MaxLonBox.Text = bounds.MaxLon.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        ReloadProviders();
        _isReady = true;
        UpdateTileEstimate();
        Closed += OnDialogClosed;
    }

    public bool RequestedBoundsPick { get; private set; }

    private void ReloadProviders(string? selectId = null)
    {
        IReadOnlyList<TileProviderConfig> providers = _mapServer.GetProviders();
        ProviderCombo.ItemsSource = providers;
        if (providers.Count > 0)
        {
            TileProviderConfig? match = !string.IsNullOrWhiteSpace(selectId)
                ? providers.FirstOrDefault(p => string.Equals(p.Id, selectId, StringComparison.OrdinalIgnoreCase))
                : null;
            ProviderCombo.SelectedItem = match ?? providers[0];
        }
    }

    private void OnEstimateInputChanged(object sender, EventArgs e)
    {
        if (_isReady)
        {
            UpdateTileEstimate();
        }
    }

    private void UpdateTileEstimate()
    {
        if (!TryReadDownloadInputs(
                out TileProviderConfig? provider,
                out double minLat,
                out double maxLat,
                out double minLon,
                out double maxLon,
                out int minZoom,
                out int maxZoom,
                out int maxTiles,
                out string? error))
        {
            TileEstimateText.Text = error ?? "GEÇERSİZ GİRDİ";
            TileEstimateText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xFA));
            return;
        }

        try
        {
            int effMin = Math.Max(minZoom, provider!.MinZoom);
            int effMax = Math.Min(maxZoom, provider.MaxZoom);
            if (effMax < effMin)
            {
                TileEstimateText.Text = $"TAHMİNİ KARO: 0 (KATMAN ZOOM ARALIĞI {provider.MinZoom}..{provider.MaxZoom})";
                TileEstimateText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xFA));
                return;
            }

            IReadOnlyList<(int Z, int X, int Y)> tiles = OfflineRegionDownloader.EnumerateTiles(
                minLat, minLon, maxLat, maxLon, effMin, effMax, maxTiles + 1);

            if (tiles.Count > maxTiles)
            {
                TileEstimateText.Text = $"TAHMİNİ KARO: > {maxTiles} (MAKS SINIR NEDENİYLE KIRPILACAK)";
                TileEstimateText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xFA));
            }
            else
            {
                TileEstimateText.Text = $"TAHMİNİ KARO: {tiles.Count} / {maxTiles}";
                TileEstimateText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xFA));
            }
        }
        catch (Exception ex)
        {
            TileEstimateText.Text = ex.Message.ToUpperInvariant();
            TileEstimateText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        }
    }

    private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (!TryReadDownloadInputs(
                out TileProviderConfig? provider,
                out double minLat,
                out double maxLat,
                out double minLon,
                out double maxLon,
                out int minZoom,
                out int maxZoom,
                out int maxTiles,
                out string? error) || provider is null)
        {
            DownloadStatusText.Text = error ?? "GEÇERSİZ PARAMETRE";
            return;
        }

        CancelAndDisposeDownloadCts();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        StartDownloadBtn.IsEnabled = false;
        CancelDownloadBtn.IsEnabled = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = "KARO LİSTESİ HAZIRLANIYOR...";

        var progress = new Progress<OfflineDownloadProgress>(p =>
        {
            if (_isClosed)
            {
                return;
            }

            int pct = p.TotalTiles > 0
                ? Math.Clamp((int)Math.Round((p.CompletedTiles + p.FailedTiles) * 100.0 / p.TotalTiles), 0, 100)
                : 0;
            DownloadProgressBar.Value = pct;
            DownloadStatusText.Text = $"İNDİRİLİYOR: {p.CompletedTiles}/{p.TotalTiles} (HATA: {p.FailedTiles})";
        });

        try
        {
            OfflineDownloadResult result = await _downloader.DownloadBoundsAsync(
                provider,
                minLat,
                minLon,
                maxLat,
                maxLon,
                minZoom,
                maxZoom,
                maxTiles,
                progress,
                cts.Token);

            if (_isClosed)
            {
                return;
            }

            DownloadProgressBar.Value = 100;
            string capNote = result.Capped ? " [KIRPILDI]" : string.Empty;
            DownloadStatusText.Text =
                $"TAMAMLANDI: {result.DownloadedTiles} İNDİRİLDİ · {result.SkippedCachedTiles} ÖNBELLEKTEN · {result.FailedTiles} HATA{capNote}";
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                DownloadStatusText.Text = "İNDİRME İPTAL EDİLDİ.";
            }
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                DownloadStatusText.Text = $"İNDİRME HATASI: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
            }
            cts.Dispose();

            if (!_isClosed)
            {
                StartDownloadBtn.IsEnabled = true;
                CancelDownloadBtn.IsEnabled = false;
            }
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        CancelAndDisposeDownloadCts();
    }

    private void CancelAndDisposeDownloadCts()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _downloadCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void OnPickBoundsFromMapClick(object sender, RoutedEventArgs e)
    {
        RequestedBoundsPick = true;
        _onRequestBoundsPickFromMap?.Invoke();
        Close();
    }

    private void OnBrowseMbtilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = ".MBTiles Harita Arşivi Seç",
            Filter = "MBTiles Arşivi (*.mbtiles)|*.mbtiles|Tüm Dosyalar (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string layerId = _mapServer.RegisterMbtiles(dialog.FileName);
            MbtilesPathBox.Text = dialog.FileName;
            MbtilesStatusText.Text = $"İÇE AKTARILDI: {layerId.ToUpperInvariant()} (AKTİF KATMAN OLARAK EKLENDİ)";
            MbtilesStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xFA));
            _onLayersUpdated(layerId);
        }
        catch (Exception ex)
        {
            MbtilesStatusText.Text = $"HATA: {ex.Message}";
            MbtilesStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        }
    }

    private void OnAddCustomProviderClick(object sender, RoutedEventArgs e)
    {
        string name = CustomNameBox.Text?.Trim() ?? string.Empty;
        string url = CustomUrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
        {
            CustomProviderStatusText.Text = "KATMAN ADI VE URL ŞABLONU GEREKLİ";
            return;
        }

        if (!int.TryParse(CustomMaxZoomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxZ))
        {
            maxZ = 19;
        }

        maxZ = Math.Clamp(maxZ, 0, 22);
        string id = "custom-" + new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (id == "custom-")
        {
            id = "custom-" + Guid.NewGuid().ToString("N")[..6];
        }

        try
        {
            var config = new TileProviderConfig(id, name.ToUpperInvariant(), url, maxZ, 0, IsBuiltIn: false);
            _mapServer.RegisterProvider(config);
            ReloadProviders(id);
            UpdateTileEstimate();
            CustomProviderStatusText.Text = $"SAĞLAYICI EKLENDİ: {config.Name}";
            _onLayersUpdated(id);
        }
        catch (Exception ex)
        {
            CustomProviderStatusText.Text = $"HATA: {ex.Message}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool TryReadDownloadInputs(
        out TileProviderConfig? provider,
        out double minLat,
        out double maxLat,
        out double minLon,
        out double maxLon,
        out int minZoom,
        out int maxZoom,
        out int maxTiles,
        out string? error)
    {
        provider = ProviderCombo.SelectedItem as TileProviderConfig;
        minLat = maxLat = minLon = maxLon = 0;
        minZoom = maxZoom = maxTiles = 0;
        error = null;

        if (provider is null)
        {
            error = "KATMAN SEÇİLMEDİ";
            return false;
        }

        if (!double.TryParse(MinLatBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out minLat) ||
            !double.TryParse(MaxLatBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxLat) ||
            !double.TryParse(MinLonBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out minLon) ||
            !double.TryParse(MaxLonBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out maxLon))
        {
            error = "KOORDİNATLAR GEÇERSİZ (NOKTA ONDALIK KULLANIN)";
            return false;
        }

        if (!int.TryParse(MinZoomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out minZoom) ||
            !int.TryParse(MaxZoomBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxZoom) ||
            !int.TryParse(MaxTilesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxTiles))
        {
            error = "ZOOM VEYA KARO LİMİTİ GEÇERSİZ";
            return false;
        }

        if (minZoom < 0 || maxZoom > 22 || maxZoom < minZoom || maxTiles <= 0)
        {
            error = "ZOOM (0..22) VEYA MAKS KARO (>0) ARALIĞI GEÇERSİZ";
            return false;
        }

        return true;
    }
}
