using System.Drawing;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public readonly struct SampleRegion(int left, int top, int right, int bottom)
{
    public int Left { get; } = left;
    public int Top { get; } = top;
    public int Right { get; } = right;
    public int Bottom { get; } = bottom;

    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

public sealed class LedLayoutMapper
{
    public SampleRegion?[] StripRegions { get; private set; } = [];

    /// <summary>Inward extent of the averaging band, measured from the inset boundary.</summary>
    public static int SampleDepth(Rectangle captureRect, int borderInset, int sampleRadius)
    {
        var innerW = Math.Max(1, captureRect.Width - borderInset * 2);
        var innerH = Math.Max(1, captureRect.Height - borderInset * 2);
        var maxDepth = Math.Max(1, Math.Min(innerW, innerH) / 2);
        return Math.Clamp(sampleRadius, 1, maxDepth);
    }

    public void Build(DeviceConfig config, Rectangle captureRect, int borderInset, int sampleRadius)
    {
        var total = Math.Max(1, config.TotalLedCount);
        var regions = new SampleRegion?[total];

        MapEdge(regions, config.LeftStart, config.LeftEnd, captureRect, borderInset, sampleRadius, EdgeSide.Left);
        MapEdge(regions, config.TopStart, config.TopEnd, captureRect, borderInset, sampleRadius, EdgeSide.Top);
        MapEdge(regions, config.RightStart, config.RightEnd, captureRect, borderInset, sampleRadius, EdgeSide.Right);
        MapEdge(regions, config.BottomStart, config.BottomEnd, captureRect, borderInset, sampleRadius, EdgeSide.Bottom);

        StripRegions = regions;
    }

    private enum EdgeSide { Left, Top, Right, Bottom }

    private static void MapEdge(
        SampleRegion?[] regions,
        int start,
        int end,
        Rectangle rect,
        int inset,
        int radius,
        EdgeSide side)
    {
        var count = SpanLength(start, end);
        if (count <= 0) return;

        var depth = SampleDepth(rect, inset, radius);
        var innerW = Math.Max(1, rect.Width - inset * 2);
        var innerH = Math.Max(1, rect.Height - inset * 2);
        var innerBottom = rect.Height - inset;
        var innerRight = rect.Width - inset;

        var forward = start <= end;
        for (var i = 0; i < count; i++)
        {
            var stripIndex = forward ? start + i : start - i;
            if (stripIndex < 0 || stripIndex >= regions.Length) continue;

            var segStart = i / (double)count;
            var segEnd = (i + 1) / (double)count;
            var segTop = inset + (int)(segStart * innerH);
            var segBottom = Math.Min(innerBottom, inset + (int)Math.Ceiling(segEnd * innerH));
            var segLeft = inset + (int)(segStart * innerW);
            var segRight = Math.Min(innerRight, inset + (int)Math.Ceiling(segEnd * innerW));

            regions[stripIndex] = side switch
            {
                EdgeSide.Left => new SampleRegion(
                    inset,
                    segTop,
                    inset + depth,
                    segBottom),
                EdgeSide.Top => new SampleRegion(
                    segLeft,
                    inset,
                    segRight,
                    inset + depth),
                EdgeSide.Right => new SampleRegion(
                    innerRight - depth,
                    segTop,
                    innerRight,
                    segBottom),
                EdgeSide.Bottom => new SampleRegion(
                    segLeft,
                    innerBottom - depth,
                    segRight,
                    innerBottom),
                _ => null
            };
        }
    }

    private static int SpanLength(int start, int end) =>
        start <= end ? end - start + 1 : start - end + 1;
}
