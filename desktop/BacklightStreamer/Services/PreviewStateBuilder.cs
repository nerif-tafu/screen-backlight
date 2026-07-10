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
        bool isLive = false,
        SampleRegion?[]? precomputedRegions = null,
        Size? sourceSize = null)
    {
        int sourceW, sourceH;
        if (sourceSize is { Width: > 0, Height: > 0 } size)
        {
            sourceW = size.Width;
            sourceH = size.Height;
        }
        else
        {
            var captureRect = StreamEngine.ResolveCaptureRect(settings);
            sourceW = captureRect.Width;
            sourceH = captureRect.Height;
        }

        var localRect = new Rectangle(0, 0, sourceW, sourceH);
        var inset = settings.BorderInset;
        var radius = settings.SampleRadius;
        var depth = LedLayoutMapper.SampleDepth(localRect, inset, radius);

        var regions = precomputedRegions;
        if (regions == null)
        {
            regions = [];
            if (config != null)
            {
                var mapper = new LedLayoutMapper();
                mapper.Build(config, localRect, inset, radius);
                regions = mapper.StripRegions;
            }
        }

        return new StreamFramePreview
        {
            Config = config,
            StripRgb = stripRgb ?? [],
            CapturePixels = capturePixels ?? [],
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            SourceCaptureWidth = sourceW,
            SourceCaptureHeight = sourceH,
            BorderInset = inset,
            SampleRadius = radius,
            DiffusionDepth = depth,
            SampleRegions = regions,
            CaptureBackend = captureBackend,
            IsLive = isLive
        };
    }
}
