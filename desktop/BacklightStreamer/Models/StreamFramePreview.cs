using BacklightStreamer.Services;

namespace BacklightStreamer.Models;

public sealed class StreamFramePreview
{
    public DeviceConfig? Config { get; init; }
    public byte[] StripRgb { get; init; } = [];
    public byte[] CapturePixels { get; init; } = [];
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public int SourceCaptureWidth { get; init; }
    public int SourceCaptureHeight { get; init; }
    public int BorderInset { get; init; }
    public int SampleRadius { get; init; }
    public int DiffusionDepth { get; init; }
    public SampleRegion?[] SampleRegions { get; init; } = [];
    public string CaptureBackend { get; init; } = "";
    public bool IsLive { get; init; }
}
