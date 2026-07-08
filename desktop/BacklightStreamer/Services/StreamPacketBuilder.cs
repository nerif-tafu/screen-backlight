using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public static class StreamPacketBuilder
{
    public static int LayoutLedCount(DeviceConfig config) =>
        Math.Max(1, config.LayoutTotal > 0 ? config.LayoutTotal : config.TotalLedCount);

    public static IEnumerable<int> StreamStripIndices(DeviceConfig config)
    {
        foreach (var idx in EdgeStripIndices(config.LeftStart, config.LeftEnd, config.ReverseLeft))
            yield return idx;
        foreach (var idx in EdgeStripIndices(config.TopStart, config.TopEnd, config.ReverseTop))
            yield return idx;
        foreach (var idx in EdgeStripIndices(config.RightStart, config.RightEnd, config.ReverseRight))
            yield return idx;
        foreach (var idx in EdgeStripIndices(config.BottomStart, config.BottomEnd, config.ReverseBottom))
            yield return idx;
    }

    public static IEnumerable<int> EdgeStripIndicesForPreview(int start, int end, bool reverse) =>
        EdgeStripIndices(start, end, reverse);

    public static void PackStreamFrame(ReadOnlySpan<byte> stripRgb, DeviceConfig config, Span<byte> streamRgbOut)
    {
        streamRgbOut.Clear();
        var pos = 0;
        foreach (var stripIndex in StreamStripIndices(config))
        {
            if (pos + 2 >= streamRgbOut.Length) break;
            var src = stripIndex * 3;
            if (src + 2 >= stripRgb.Length)
            {
                streamRgbOut[pos] = 0;
                streamRgbOut[pos + 1] = 0;
                streamRgbOut[pos + 2] = 0;
            }
            else
            {
                streamRgbOut[pos] = stripRgb[src];
                streamRgbOut[pos + 1] = stripRgb[src + 1];
                streamRgbOut[pos + 2] = stripRgb[src + 2];
            }

            pos += 3;
        }
    }

    private static IEnumerable<int> EdgeStripIndices(int start, int end, bool reverse)
    {
        if (SpanLength(start, end) <= 0) yield break;

        var step = start <= end ? 1 : -1;
        if (reverse) step = -step;

        var idx = start;
        while (true)
        {
            yield return idx;
            if (idx == end) break;
            idx += step;
        }
    }

    private static int SpanLength(int start, int end) =>
        start <= end ? end - start + 1 : start - end + 1;
}
