using System.Drawing;

namespace BacklightStreamer.Services;

public interface IScreenCapture : IDisposable
{
    int Width { get; }
    int Height { get; }
    string BackendName { get; }
    bool TryCapture(Span<byte> bgraBuffer, out int stride);
}

public sealed class CompositeScreenCapture : IScreenCapture
{
    private DxgiScreenCapture? _dxgi;
    private GdiScreenCapture? _gdi;
    private IScreenCapture? _active;
    private bool _usingDxgi;
    private Rectangle _captureRect;

    public int Width => _active?.Width ?? _captureRect.Width;
    public int Height => _active?.Height ?? _captureRect.Height;
    public string BackendName => _active?.BackendName ?? "none";

    public void Initialize(int monitorIndex, Rectangle captureRect)
    {
        DisposeActive();
        _captureRect = captureRect;

        _dxgi = new DxgiScreenCapture();
        try
        {
            _dxgi.Initialize(monitorIndex, captureRect);
            _active = _dxgi;
            _usingDxgi = true;
            return;
        }
        catch
        {
            _dxgi.Dispose();
            _dxgi = null;
        }

        _gdi = new GdiScreenCapture();
        _gdi.Initialize(captureRect);
        _active = _gdi;
        _usingDxgi = false;
    }

    public bool TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        if (_active == null)
        {
            stride = 0;
            return false;
        }

        if (_active.TryCapture(bgraBuffer, out stride))
            return true;

        if (_usingDxgi && _gdi == null)
        {
            _dxgi?.Dispose();
            _dxgi = null;
            _gdi = new GdiScreenCapture();
            _gdi.Initialize(_captureRect);
            _active = _gdi;
            _usingDxgi = false;
        }

        stride = 0;
        return false;
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
    }
}
