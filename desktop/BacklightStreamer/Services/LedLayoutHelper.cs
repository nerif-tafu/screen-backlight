using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public static class LedLayoutHelper
{
    public static int EdgeSpanLength(int start, int end) =>
        start <= end ? end - start + 1 : start - end + 1;

    public static (int HorizontalLeds, int VerticalLeds) AspectFromConfig(DeviceConfig? config)
    {
        if (config == null) return (16, 9);

        var horizontal = Math.Max(
            EdgeSpanLength(config.TopStart, config.TopEnd),
            EdgeSpanLength(config.BottomStart, config.BottomEnd));
        var vertical = Math.Max(
            EdgeSpanLength(config.LeftStart, config.LeftEnd),
            EdgeSpanLength(config.RightStart, config.RightEnd));

        return (Math.Max(1, horizontal), Math.Max(1, vertical));
    }
}
