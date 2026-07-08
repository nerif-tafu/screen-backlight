using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BacklightStreamer.Services;

public sealed class GdiScreenCapture : IScreenCapture
{
    private Rectangle _captureRect;
    private bool _initialized;

    public int Width => _captureRect.Width;
    public int Height => _captureRect.Height;
    public string BackendName => "GDI";

    public void Initialize(Rectangle captureRect)
    {
        if (captureRect.Width <= 0 || captureRect.Height <= 0)
            throw new InvalidOperationException("Capture region has invalid size.");

        _captureRect = captureRect;
        _initialized = true;
    }

    public bool TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        stride = 0;
        if (!_initialized) return false;

        var rowBytes = _captureRect.Width * 4;
        if (bgraBuffer.Length < rowBytes * _captureRect.Height)
            return false;

        using var bitmap = new Bitmap(_captureRect.Width, _captureRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(_captureRect.Left, _captureRect.Top, 0, 0, _captureRect.Size, CopyPixelOperation.SourceCopy);
        }

        var locked = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            stride = rowBytes;
            unsafe
            {
                var src = (byte*)locked.Scan0;
                for (var y = 0; y < _captureRect.Height; y++)
                {
                    new ReadOnlySpan<byte>(src + y * locked.Stride, rowBytes)
                        .CopyTo(bgraBuffer.Slice(y * rowBytes, rowBytes));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return true;
    }

    public void Dispose()
    {
        _initialized = false;
        GC.SuppressFinalize(this);
    }
}
