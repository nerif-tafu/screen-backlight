using System.Drawing;

namespace BacklightStreamer.Services;

public enum CaptureResult
{
    FrameCaptured,
    NoNewFrame,
    Failed
}

public interface IScreenCapture : IDisposable
{
    int Width { get; }
    int Height { get; }
    string BackendName { get; }
    CaptureResult TryCapture(Span<byte> bgraBuffer, out int stride);
    CaptureResult TryCaptureBands(EdgeBandBuffers bands);
}

public sealed class CompositeScreenCapture : IScreenCapture
{
    private const int DxgiRetryIntervalMs = 5000;

    private DxgiScreenCapture? _dxgi;
    private GdiScreenCapture? _gdi;
    private IScreenCapture? _active;
    private bool _usingDxgi;
    private Rectangle _captureRect;
    private int _monitorIndex;
    private long _nextDxgiRetryAt;

    public int Width => _active?.Width ?? _captureRect.Width;
    public int Height => _active?.Height ?? _captureRect.Height;
    public string BackendName => _active?.BackendName ?? "none";

    public void Initialize(int monitorIndex, Rectangle captureRect)
    {
        DisposeActive();
        _captureRect = captureRect;
        _monitorIndex = monitorIndex;

        if (!TryActivateDxgi())
            ActivateGdi();
    }

    public CaptureResult TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        stride = 0;
        if (_active == null) return CaptureResult.Failed;

        MaybeRetryDxgi();
        var result = _active.TryCapture(bgraBuffer, out stride);
        if (result == CaptureResult.Failed)
            FallBackToGdi();
        return result;
    }

    public CaptureResult TryCaptureBands(EdgeBandBuffers bands)
    {
        if (_active == null) return CaptureResult.Failed;

        MaybeRetryDxgi();
        var result = _active.TryCaptureBands(bands);
        if (result == CaptureResult.Failed)
            FallBackToGdi();
        return result;
    }

    private bool TryActivateDxgi()
    {
        var dxgi = new DxgiScreenCapture();
        try
        {
            dxgi.Initialize(_monitorIndex, _captureRect);
        }
        catch
        {
            dxgi.Dispose();
            _nextDxgiRetryAt = Environment.TickCount64 + DxgiRetryIntervalMs;
            return false;
        }

        _dxgi?.Dispose();
        _dxgi = dxgi;
        _active = dxgi;
        _usingDxgi = true;
        return true;
    }

    private void ActivateGdi()
    {
        _gdi ??= CreateGdi();
        _active = _gdi;
        _usingDxgi = false;
    }

    private GdiScreenCapture CreateGdi()
    {
        var gdi = new GdiScreenCapture();
        gdi.Initialize(_captureRect);
        return gdi;
    }

    // DXGI duplication is lost when a game enters exclusive fullscreen or the
    // desktop switches; GDI keeps working but costs far more CPU, so retry
    // DXGI periodically instead of staying on GDI forever.
    private void MaybeRetryDxgi()
    {
        if (_usingDxgi || Environment.TickCount64 < _nextDxgiRetryAt) return;
        if (TryActivateDxgi())
        {
            _gdi?.Dispose();
            _gdi = null;
        }
    }

    private void FallBackToGdi()
    {
        if (!_usingDxgi) return;
        _dxgi?.Dispose();
        _dxgi = null;
        _nextDxgiRetryAt = Environment.TickCount64 + DxgiRetryIntervalMs;
        ActivateGdi();
    }

    public void Dispose()
    {
        DisposeActive();
        GC.SuppressFinalize(this);
    }

    private void DisposeActive()
    {
        _dxgi?.Dispose();
        _gdi?.Dispose();
        _dxgi = null;
        _gdi = null;
        _active = null;
        _usingDxgi = false;
    }
}
