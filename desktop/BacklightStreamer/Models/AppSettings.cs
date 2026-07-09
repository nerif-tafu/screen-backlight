namespace BacklightStreamer.Models;

public sealed class AppSettings
{
    public string DeviceHost { get; set; } = "192.168.3.180";
    public int MonitorIndex { get; set; }
    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public bool UseCustomCaptureRegion { get; set; }
    public int BorderInset { get; set; } = 4;
    public int SampleRadius { get; set; } = 6;
    public int TargetFps { get; set; } = 60;
    public int ColorSmoothing { get; set; } = 30;
    public int Brightness { get; set; } = 128;
    public int PreviewPanelHeight { get; set; } = 260;
    public bool AutoConnect { get; set; } = true;
    public bool AutoStream { get; set; }
    public bool StartOnBoot { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool SyncLayoutFromDevice { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectIntervalMs { get; set; } = 250;
    public int ApiPort { get; set; } = 7890;
    public bool EnableLocalApi { get; set; } = true;
}
