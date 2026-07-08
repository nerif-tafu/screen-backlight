using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using BacklightStreamer.Models;
using BacklightStreamer.Services;
using MessageBox = System.Windows.MessageBox;

namespace BacklightStreamer.Views;

public partial class MainWindow : Window
{
    private NotifyIcon? _tray;
    private bool _forceClose;
    private bool _loadingUi;
    private System.Windows.Threading.DispatcherTimer? _saveDebounceTimer;
    private System.Windows.Threading.DispatcherTimer? _brightnessDebounceTimer;
    private AppSettings Settings => App.Settings;
    private StreamEngine Engine => App.Engine;

    public MainWindow()
    {
        InitializeComponent();
        WireAutoSaveHandlers();
        TrySetWindowIcon();
        Engine.StatusChanged += OnEngineStatusChanged;
        Engine.FramePreview += OnFramePreview;
        PopulateMonitors();
        LoadSettingsToUi();
        UpdateLayoutSummary();
        UpdateControls();

        Loaded += async (_, _) =>
        {
            ApplyPreviewPanelHeight();
            if (Settings.AutoConnect)
                await SafeConnectAsync();
            if (Settings.AutoStream && Engine.Status.Connected)
                await SafeStartStreamAsync();
        };
    }

    private void ApplyPreviewPanelHeight()
    {
        var height = Math.Clamp(Settings.PreviewPanelHeight, 120, 720);
        PreviewRowDef.Height = new GridLength(height, GridUnitType.Pixel);
    }

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var height = (int)Math.Round(PreviewRowDef.ActualHeight);
        if (height < 120) return;
        Settings.PreviewPanelHeight = height;
        PersistSettings();
    }

    private void WireAutoSaveHandlers()
    {
        HostBox.TextChanged += (_, _) => SchedulePersistSettings();
        CaptureXBox.TextChanged += (_, _) => SchedulePersistSettings();
        CaptureYBox.TextChanged += (_, _) => SchedulePersistSettings();
        CaptureWidthBox.TextChanged += (_, _) => SchedulePersistSettings();
        CaptureHeightBox.TextChanged += (_, _) => SchedulePersistSettings();
    }

    private void SchedulePersistSettings()
    {
        if (_loadingUi || !IsLoaded) return;

        _saveDebounceTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Tick -= SaveDebounce_Tick;
        _saveDebounceTimer.Tick += SaveDebounce_Tick;
        _saveDebounceTimer.Start();
    }

    private void SaveDebounce_Tick(object? sender, EventArgs e)
    {
        _saveDebounceTimer?.Stop();
        PersistSettings();
        UpdatePreviewGuide();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        PersistSettings();
    }

    private void TrySetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!File.Exists(iconPath)) return;
        Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
    }

    private void LoadSettingsToUi()
    {
        _loadingUi = true;
        try
        {
            HostBox.Text = Settings.DeviceHost;
            ApplySavedMonitorSelection();
            CustomRegionCheck.IsChecked = Settings.UseCustomCaptureRegion;
            CaptureXBox.Text = Settings.CaptureX.ToString();
            CaptureYBox.Text = Settings.CaptureY.ToString();
            CaptureWidthBox.Text = Settings.CaptureWidth.ToString();
            CaptureHeightBox.Text = Settings.CaptureHeight.ToString();
            InsetSlider.Value = Settings.BorderInset;
            RadiusSlider.Value = Settings.SampleRadius;
            FpsSlider.Value = Settings.TargetFps;
            SmoothingSlider.Value = Settings.ColorSmoothing;
            BrightnessSlider.Value = Settings.Brightness;
            AutoConnectCheck.IsChecked = Settings.AutoConnect;
            AutoStreamCheck.IsChecked = Settings.AutoStream;
            StartOnBootCheck.IsChecked = Settings.StartOnBoot;
            StartMinimizedCheck.IsChecked = Settings.StartMinimized;
            MinimizeToTrayCheck.IsChecked = Settings.MinimizeToTray;
            UpdateCustomRegionEnabled();
            UpdateSliderLabels();
            UpdateBrightnessLabel();
            UpdatePreviewGuide();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ApplySliderSettings()
    {
        Settings.BorderInset = (int)InsetSlider.Value;
        Settings.SampleRadius = (int)RadiusSlider.Value;
        Settings.TargetFps = (int)FpsSlider.Value;
        Settings.ColorSmoothing = (int)SmoothingSlider.Value;
        Settings.Brightness = (int)BrightnessSlider.Value;
    }

    private void UpdateBrightnessLabel()
    {
        BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();
    }

    private void ApplyDeviceBrightnessToUi()
    {
        var brightness = Engine.DeviceConfig?.Brightness ?? Settings.Brightness;
        _loadingUi = true;
        try
        {
            BrightnessSlider.Value = Math.Clamp(brightness, 0, 255);
            Settings.Brightness = (int)BrightnessSlider.Value;
            UpdateBrightnessLabel();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loadingUi) return;

        UpdateBrightnessLabel();
        Settings.Brightness = (int)BrightnessSlider.Value;
        PersistSettings();
        ScheduleBrightnessApply();
    }

    private void ScheduleBrightnessApply()
    {
        if (!Engine.Status.Connected) return;

        _brightnessDebounceTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _brightnessDebounceTimer.Stop();
        _brightnessDebounceTimer.Tick -= BrightnessDebounce_Tick;
        _brightnessDebounceTimer.Tick += BrightnessDebounce_Tick;
        _brightnessDebounceTimer.Start();
    }

    private async void BrightnessDebounce_Tick(object? sender, EventArgs e)
    {
        _brightnessDebounceTimer?.Stop();
        if (!Engine.Status.Connected) return;

        try
        {
            await Engine.SetBrightnessAsync(Settings, (int)BrightnessSlider.Value);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Brightness update failed: {ex.Message}";
        }
    }

    private void UpdatePreviewGuide()
    {
        ApplySliderSettings();
        SaveCaptureFieldsToSettings();
        var guide = PreviewStateBuilder.Build(Settings, Engine.DeviceConfig);
        PreviewControl.UpdateGuide(guide);
    }

    private void SaveCaptureFieldsToSettings()
    {
        Settings.MonitorIndex = MonitorCombo.SelectedIndex >= 0 ? MonitorCombo.SelectedIndex : 0;
        Settings.UseCustomCaptureRegion = CustomRegionCheck.IsChecked == true;
        Settings.CaptureX = ParseInt(CaptureXBox.Text);
        Settings.CaptureY = ParseInt(CaptureYBox.Text);
        Settings.CaptureWidth = ParseInt(CaptureWidthBox.Text);
        Settings.CaptureHeight = ParseInt(CaptureHeightBox.Text);
    }

    private void UpdateSliderLabels()
    {
        InsetValueText.Text = ((int)InsetSlider.Value).ToString();
        RadiusValueText.Text = ((int)RadiusSlider.Value).ToString();
        FpsValueText.Text = ((int)FpsSlider.Value).ToString();
        SmoothingValueText.Text = SmoothingLabel((int)SmoothingSlider.Value);
    }

    private static string SmoothingLabel(int value) =>
        value <= 0 ? "Off" : $"{value} FPS";

    private void SamplingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateSliderLabels();
        ApplySliderSettings();
        UpdatePreviewGuide();
        PersistSettings();
    }

    private void SyncUiToSettings()
    {
        Settings.DeviceHost = HostBox.Text.Trim();
        if (MonitorCombo.SelectedIndex >= 0)
            Settings.MonitorIndex = MonitorCombo.SelectedIndex;
        Settings.UseCustomCaptureRegion = CustomRegionCheck.IsChecked == true;
        Settings.CaptureX = ParseInt(CaptureXBox.Text);
        Settings.CaptureY = ParseInt(CaptureYBox.Text);
        Settings.CaptureWidth = ParseInt(CaptureWidthBox.Text);
        Settings.CaptureHeight = ParseInt(CaptureHeightBox.Text);
        Settings.BorderInset = (int)InsetSlider.Value;
        Settings.SampleRadius = (int)RadiusSlider.Value;
        Settings.TargetFps = (int)FpsSlider.Value;
        Settings.ColorSmoothing = (int)SmoothingSlider.Value;
        Settings.Brightness = (int)BrightnessSlider.Value;
        var previewHeight = (int)Math.Round(PreviewRowDef.ActualHeight);
        if (previewHeight >= 120)
            Settings.PreviewPanelHeight = previewHeight;
        Settings.AutoConnect = AutoConnectCheck.IsChecked == true;
        Settings.AutoStream = AutoStreamCheck.IsChecked == true;
        Settings.StartOnBoot = StartOnBootCheck.IsChecked == true;
        Settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        Settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
    }

    private void PersistSettings()
    {
        if (_loadingUi) return;
        SyncUiToSettings();
        SettingsStore.Save(Settings);
        StartupService.Apply(Settings.StartOnBoot);
    }

    private void PopulateMonitors()
    {
        _loadingUi = true;
        try
        {
            MonitorCombo.Items.Clear();
            foreach (var screen in Screen.AllScreens)
            {
                var primary = screen.Primary ? " (primary)" : "";
                MonitorCombo.Items.Add($"{screen.DeviceName} {screen.Bounds.Width}x{screen.Bounds.Height}{primary}");
            }
            if (MonitorCombo.Items.Count == 0)
                MonitorCombo.Items.Add("No monitors");
            ApplySavedMonitorSelection();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ApplySavedMonitorSelection()
    {
        if (MonitorCombo.Items.Count == 0) return;
        MonitorCombo.SelectedIndex = Math.Clamp(
            Settings.MonitorIndex,
            0,
            MonitorCombo.Items.Count - 1);
    }

    private void UpdateLayoutSummary()
    {
        var cfg = Engine.DeviceConfig;
        if (cfg == null)
        {
            LayoutSummaryText.Text = "Layout: not loaded — connect and sync from device.";
            return;
        }

        LayoutSummaryText.Text =
            $"Strip length {cfg.TotalLedCount} · layout total {cfg.LayoutTotal} · " +
            $"L {cfg.LeftStart}-{cfg.LeftEnd}, T {cfg.TopStart}-{cfg.TopEnd}, " +
            $"R {cfg.RightStart}-{cfg.RightEnd}, B {cfg.BottomStart}-{cfg.BottomEnd}";
    }

    private void UpdateControls()
    {
        var connected = Engine.Status.Connected;
        ConnectBtn.IsEnabled = !connected;
        DisconnectBtn.IsEnabled = connected;
        SyncLayoutBtn.IsEnabled = connected;
        StreamBtn.Content = Engine.Status.Streaming ? "Stop streaming" : "Start streaming";
        StreamBtn.IsEnabled = connected;
        BrightnessSlider.IsEnabled = connected;
        StatusText.Text = Engine.Status.Message;
        FpsText.Text = Engine.Status.Streaming
            ? $"Capture {Engine.Status.CaptureFps:F1} FPS · Send {Engine.Status.SendFps:F1} FPS · {Engine.Status.CaptureBackend}"
            : connected ? "Connected — ready to stream" : "Not connected";
    }

    private void OnEngineStatusChanged(StreamStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateControls();
            if (_tray != null)
                _tray.Text = status.Streaming
                    ? $"Backlight Streamer — {status.SendFps:F0} FPS"
                    : "Backlight Streamer";
        });
    }

    private void OnFramePreview(StreamFramePreview preview)
    {
        Dispatcher.Invoke(() => PreviewControl.UpdatePreview(preview));
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeToTrayCheck.IsChecked == true)
            HideToTray();
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e) => await SafeConnectAsync();

    private async Task SafeConnectAsync()
    {
        try
        {
            PersistSettings();
            await Engine.ConnectAsync(Settings);
            if (Settings.SyncLayoutFromDevice)
                await Engine.RefreshLayoutAsync(Settings);
            UpdateLayoutSummary();
            ApplyDeviceBrightnessToUi();
            UpdateControls();
            UpdatePreviewGuide();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Connect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        await Engine.DisconnectAsync();
        UpdateLayoutSummary();
        UpdateControls();
        UpdatePreviewGuide();
    }

    private async void SyncLayoutBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistSettings();
            await Engine.RefreshLayoutAsync(Settings);
            UpdateLayoutSummary();
            ApplyDeviceBrightnessToUi();
            UpdatePreviewGuide();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StreamBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Engine.Status.Streaming)
        {
            await Engine.StopStreamingAsync();
            UpdatePreviewGuide();
            UpdateControls();
            return;
        }

        await SafeStartStreamAsync();
    }

    private async Task SafeStartStreamAsync()
    {
        try
        {
            PersistSettings();
            if (!Engine.Status.Connected)
                await Engine.ConnectAsync(Settings);
            await Engine.StartStreamingAsync(Settings);
            UpdateControls();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Stream failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingUi || !IsLoaded) return;
        if (MonitorCombo.SelectedIndex < 0) return;
        Settings.MonitorIndex = MonitorCombo.SelectedIndex;
        UpdatePreviewGuide();
        PersistSettings();
    }

    private void CustomRegionCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCustomRegionEnabled();
        UpdatePreviewGuide();
        PersistSettings();
    }

    private void UpdateCustomRegionEnabled()
    {
        var enabled = CustomRegionCheck.IsChecked == true;
        CaptureXBox.IsEnabled = enabled;
        CaptureYBox.IsEnabled = enabled;
        CaptureWidthBox.IsEnabled = enabled;
        CaptureHeightBox.IsEnabled = enabled;
    }

    private void UseFullMonitor_Click(object sender, RoutedEventArgs e)
    {
        CustomRegionCheck.IsChecked = false;
        Settings.UseCustomCaptureRegion = false;
        UpdateCustomRegionEnabled();
        UpdatePreviewGuide();
        PersistSettings();
    }

    public void HideToTray()
    {
        EnsureTrayIcon();
        Hide();
        ShowInTaskbar = false;
        _tray!.Visible = true;
    }

    private void EnsureTrayIcon()
    {
        if (_tray != null) return;

        Icon? icon = null;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            icon = new Icon(iconPath);

        _tray = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Backlight Streamer",
            Visible = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Start streaming", null, async (_, _) => await SafeStartStreamAsync());
        menu.Items.Add("Stop streaming", null, async (_, _) =>
        {
            await Engine.StopStreamingAsync();
            UpdateControls();
        });
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _forceClose = true;
            Close();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;
        if (MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosed(e);
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, out var value) ? value : 0;
}
