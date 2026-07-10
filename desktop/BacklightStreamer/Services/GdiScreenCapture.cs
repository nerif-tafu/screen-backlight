using System.Drawing;
using System.Drawing.Imaging;

namespace BacklightStreamer.Services;

public sealed class GdiScreenCapture : IScreenCapture
{
    private Rectangle _captureRect;
    private bool _initialized;
    private Bitmap? _fullBitmap;
    private Bitmap? _topBitmap;
    private Bitmap? _bottomBitmap;
    private Bitmap? _leftBitmap;
    private Bitmap? _rightBitmap;
    private int _bandThickness;

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

    public CaptureResult TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        stride = 0;
        if (!_initialized) return CaptureResult.Failed;

        var rowBytes = _captureRect.Width * 4;
        if (bgraBuffer.Length < rowBytes * _captureRect.Height)
            return CaptureResult.Failed;

        try
        {
            _fullBitmap ??= new Bitmap(_captureRect.Width, _captureRect.Height, PixelFormat.Format32bppArgb);
            CopyScreenInto(_fullBitmap, _captureRect.Left, _captureRect.Top, _captureRect.Size);
            CopyBitmapRows(_fullBitmap, bgraBuffer, rowBytes);
            stride = rowBytes;
            return CaptureResult.FrameCaptured;
        }
        catch
        {
            return CaptureResult.Failed;
        }
    }

    public CaptureResult TryCaptureBands(EdgeBandBuffers bands)
    {
        if (!_initialized || !bands.IsConfigured) return CaptureResult.Failed;
        if (bands.Width != _captureRect.Width || bands.Height != _captureRect.Height)
            return CaptureResult.Failed;

        var t = bands.Thickness;
        try
        {
            EnsureBandBitmaps(t);
            var left = _captureRect.Left;
            var top = _captureRect.Top;
            CopyScreenInto(_topBitmap!, left, top, new Size(_captureRect.Width, t));
            CopyScreenInto(_bottomBitmap!, left, top + _captureRect.Height - t, new Size(_captureRect.Width, t));
            CopyScreenInto(_leftBitmap!, left, top, new Size(t, _captureRect.Height));
            CopyScreenInto(_rightBitmap!, left + _captureRect.Width - t, top, new Size(t, _captureRect.Height));

            CopyBitmapRows(_topBitmap!, bands.Top, _captureRect.Width * 4);
            CopyBitmapRows(_bottomBitmap!, bands.Bottom, _captureRect.Width * 4);
            CopyBitmapRows(_leftBitmap!, bands.Left, t * 4);
            CopyBitmapRows(_rightBitmap!, bands.Right, t * 4);
            return CaptureResult.FrameCaptured;
        }
        catch
        {
            return CaptureResult.Failed;
        }
    }

    private static void CopyScreenInto(Bitmap bitmap, int screenX, int screenY, Size size)
    {
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(screenX, screenY, 0, 0, size, CopyPixelOperation.SourceCopy);
    }

    private static void CopyBitmapRows(Bitmap bitmap, Span<byte> dst, int rowBytes)
    {
        var locked = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                var src = (byte*)locked.Scan0;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    new ReadOnlySpan<byte>(src + y * locked.Stride, rowBytes)
                        .CopyTo(dst.Slice(y * rowBytes, rowBytes));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    private void EnsureBandBitmaps(int thickness)
    {
        if (_bandThickness == thickness && _topBitmap != null) return;

        DisposeBandBitmaps();
        _bandThickness = thickness;
        _topBitmap = new Bitmap(_captureRect.Width, thickness, PixelFormat.Format32bppArgb);
        _bottomBitmap = new Bitmap(_captureRect.Width, thickness, PixelFormat.Format32bppArgb);
        _leftBitmap = new Bitmap(thickness, _captureRect.Height, PixelFormat.Format32bppArgb);
        _rightBitmap = new Bitmap(thickness, _captureRect.Height, PixelFormat.Format32bppArgb);
    }

    private void DisposeBandBitmaps()
    {
        _topBitmap?.Dispose();
        _bottomBitmap?.Dispose();
        _leftBitmap?.Dispose();
        _rightBitmap?.Dispose();
        _topBitmap = null;
        _bottomBitmap = null;
        _leftBitmap = null;
        _rightBitmap = null;
        _bandThickness = 0;
    }

    public void Dispose()
    {
        _fullBitmap?.Dispose();
        _fullBitmap = null;
        DisposeBandBitmaps();
        _initialized = false;
        GC.SuppressFinalize(this);
    }
}
