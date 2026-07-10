namespace BacklightStreamer.Services;

public static class ColorSampler
{
    public static void SampleFrame(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        SampleRegion?[] stripRegions,
        Span<byte> rgbOut)
    {
        rgbOut.Clear();

        for (var stripIndex = 0; stripIndex < stripRegions.Length; stripIndex++)
        {
            var region = stripRegions[stripIndex];
            if (region is null) continue;

            var offset = stripIndex * 3;
            var r = region.Value;
            Average(bgra, width, height, r.Left, r.Top, r.Right, r.Bottom,
                out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
        }
    }

    public static void SampleBands(
        EdgeBandBuffers bands,
        SampleRegion?[] stripRegions,
        Span<byte> rgbOut)
    {
        rgbOut.Clear();
        var t = bands.Thickness;
        var w = bands.Width;
        var h = bands.Height;

        for (var stripIndex = 0; stripIndex < stripRegions.Length; stripIndex++)
        {
            var region = stripRegions[stripIndex];
            if (region is null) continue;

            var offset = stripIndex * 3;
            var r = region.Value;

            // Each band covers the frame's full width (top/bottom) or full
            // height (left/right), so any region fully inside a band samples
            // identical pixels to the full-frame path; translate to band-local
            // coordinates and average. Regions that fit no band (only possible
            // with degenerate inset settings) stay black — the engine falls
            // back to full-frame capture before that can happen.
            if (r.Bottom <= t)
                Average(bands.Top, w, t, r.Left, r.Top, r.Right, r.Bottom,
                    out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
            else if (r.Top >= h - t)
                Average(bands.Bottom, w, t, r.Left, r.Top - (h - t), r.Right, r.Bottom - (h - t),
                    out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
            else if (r.Right <= t)
                Average(bands.Left, t, h, r.Left, r.Top, r.Right, r.Bottom,
                    out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
            else if (r.Left >= w - t)
                Average(bands.Right, t, h, r.Left - (w - t), r.Top, r.Right - (w - t), r.Bottom,
                    out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
        }
    }

    private static void Average(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        out byte r,
        out byte g,
        out byte b)
    {
        x0 = Math.Clamp(x0, 0, width);
        x1 = Math.Clamp(x1, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        y1 = Math.Clamp(y1, 0, height);

        if (x1 <= x0 || y1 <= y0)
        {
            r = g = b = 0;
            return;
        }

        long sumR = 0, sumG = 0, sumB = 0;
        var stride = width * 4;
        var rowBytes = (x1 - x0) * 4;

        for (var y = y0; y < y1; y++)
        {
            var row = bgra.Slice(y * stride + x0 * 4, rowBytes);
            for (var i = 0; i < row.Length; i += 4)
            {
                sumB += row[i];
                sumG += row[i + 1];
                sumR += row[i + 2];
            }
        }

        var count = (x1 - x0) * (y1 - y0);
        r = (byte)(sumR / count);
        g = (byte)(sumG / count);
        b = (byte)(sumB / count);
    }
}
