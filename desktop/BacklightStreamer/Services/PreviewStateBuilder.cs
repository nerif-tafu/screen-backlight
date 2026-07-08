using System.Drawing;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public static class PreviewStateBuilder
{
    public static StreamFramePreview Build(
        AppSettings settings,
        DeviceConfig? config,
        byte[]? capturePixels = null,
        int captureWidth = 0,
        int captureHeight = 0,
        byte[]? stripRgb = null,
        string captureBackend = "",
        bool isLive = false)
    {
        var captureRect = StreamEngine.ResolveCaptureRect(settings);
        var localRect = new Rectangle(0, 0, captureRect.Width, captureRect.Height);
        var inset = settings.BorderInset;
        var radius = settings.SampleRadius;
        var depth = LedLayoutMapper.SampleDepth(localRect, inset, radius);

        SampleRegion?[] regions = [];
        if (config != null)
        {
            var mapper = new LedLayoutMapper();
            mapper.Build(config, localRect, inset, radius);
            regions = mapper.StripRegions;
        }

        return new StreamFramePreview
        {
            Config = config,
            StripRgb = stripRgb ?? [],
            CapturePixels = capturePixels ?? [],
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            SourceCaptureWidth = captureRect.Width,
            SourceCaptureHeight = captureRect.Height,
            BorderInset = inset,
            SampleRadius = radius,
            DiffusionDepth = depth,
            SampleRegions = regions,
            CaptureBackend = captureBackend,
            IsLive = isLive
        };
    }
}
