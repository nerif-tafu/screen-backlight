using System.Numerics;

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
        var stride = width * 4;

        for (var stripIndex = 0; stripIndex < stripRegions.Length; stripIndex++)
        {
            var region = stripRegions[stripIndex];
            var offset = stripIndex * 3;
            if (region is null)
            {
                rgbOut[offset] = 0;
                rgbOut[offset + 1] = 0;
                rgbOut[offset + 2] = 0;
                continue;
            }

            AverageRegion(bgra, width, height, stride, region.Value, out rgbOut[offset], out rgbOut[offset + 1], out rgbOut[offset + 2]);
        }
    }

    private static void AverageRegion(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        SampleRegion region,
        out byte r,
        out byte g,
        out byte b)
    {
        var x0 = Math.Clamp(region.Left, 0, width - 1);
        var x1 = Math.Clamp(region.Right - 1, 0, width - 1);
        var y0 = Math.Clamp(region.Top, 0, height - 1);
        var y1 = Math.Clamp(region.Bottom - 1, 0, height - 1);

        if (x1 < x0 || y1 < y0)
        {
            r = g = b = 0;
            return;
        }

        Vector4 sum = Vector4.Zero;
        var count = 0;

        for (var y = y0; y <= y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x <= x1; x++)
            {
                var i = row + x * 4;
                if (i + 2 >= bgra.Length) continue;
                sum.X += bgra[i + 2];
                sum.Y += bgra[i + 1];
                sum.Z += bgra[i];
                count++;
            }
        }

        if (count == 0)
        {
            r = g = b = 0;
            return;
        }

        var inv = 1f / count;
        r = (byte)Math.Clamp((int)(sum.X * inv), 0, 255);
        g = (byte)Math.Clamp((int)(sum.Y * inv), 0, 255);
        b = (byte)Math.Clamp((int)(sum.Z * inv), 0, 255);
    }
}
