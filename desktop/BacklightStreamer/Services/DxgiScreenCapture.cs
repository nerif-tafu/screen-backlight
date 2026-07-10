using System.Drawing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Device = Vortice.Direct3D11.ID3D11Device;
using MapFlags = Vortice.Direct3D11.MapFlags;
using static Vortice.Direct3D11.D3D11;

namespace BacklightStreamer.Services;

public sealed class DxgiScreenCapture : IScreenCapture
{
    private IDXGIAdapter1? _adapter;
    private Device? _device;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingFull;
    private ID3D11Texture2D? _stagingTop;
    private ID3D11Texture2D? _stagingBottom;
    private ID3D11Texture2D? _stagingLeft;
    private ID3D11Texture2D? _stagingRight;
    private int _bandThickness;
    private int _left;
    private int _top;
    private int _width;
    private int _height;
    private bool _initialized;

    public int Width => _width;
    public int Height => _height;
    public string BackendName => "DXGI";

    public void Initialize(int monitorIndex, Rectangle captureRect)
    {
        DisposeGpu();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var targetOutput = MonitorCaptureHelper.FindOutputForScreen(monitorIndex, factory, out _adapter)
            ?? throw new InvalidOperationException($"Monitor index {monitorIndex} not found for DXGI capture.");

        var outputDesc = targetOutput.Description;
        _left = captureRect.Left - outputDesc.DesktopCoordinates.Left;
        _top = captureRect.Top - outputDesc.DesktopCoordinates.Top;
        _width = captureRect.Width;
        _height = captureRect.Height;

        if (_width <= 0 || _height <= 0)
            throw new InvalidOperationException("Capture region has invalid size.");

        D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 },
            out _device).CheckError();

        _duplication = targetOutput.DuplicateOutput(_device!);
        _initialized = true;
        targetOutput.Dispose();
    }

    public CaptureResult TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        stride = 0;
        if (!_initialized || _duplication == null || _device == null)
            return CaptureResult.Failed;

        var rowBytes = _width * 4;
        if (bgraBuffer.Length < rowBytes * _height)
            return CaptureResult.Failed;

        _stagingFull ??= CreateStaging(_width, _height);

        var result = _duplication.AcquireNextFrame(100, out _, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return CaptureResult.NoNewFrame;
        if (result.Failure)
        {
            _initialized = false;
            return CaptureResult.Failed;
        }

        try
        {
            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            CopyRegion(desktopTexture, _stagingFull, _left, _top, _width, _height);
            ReadStaging(_stagingFull, bgraBuffer, _width, _height);
            stride = rowBytes;
        }
        finally
        {
            _duplication.ReleaseFrame();
        }

        return CaptureResult.FrameCaptured;
    }

    public CaptureResult TryCaptureBands(EdgeBandBuffers bands)
    {
        if (!_initialized || _duplication == null || _device == null || !bands.IsConfigured)
            return CaptureResult.Failed;
        if (bands.Width != _width || bands.Height != _height)
            return CaptureResult.Failed;

        EnsureBandStagings(bands.Thickness);

        var result = _duplication.AcquireNextFrame(100, out _, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return CaptureResult.NoNewFrame;
        if (result.Failure)
        {
            _initialized = false;
            return CaptureResult.Failed;
        }

        var t = bands.Thickness;
        try
        {
            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            CopyRegion(desktopTexture, _stagingTop!, _left, _top, _width, t);
            CopyRegion(desktopTexture, _stagingBottom!, _left, _top + _height - t, _width, t);
            CopyRegion(desktopTexture, _stagingLeft!, _left, _top, t, _height);
            CopyRegion(desktopTexture, _stagingRight!, _left + _width - t, _top, t, _height);

            ReadStaging(_stagingTop!, bands.Top, _width, t);
            ReadStaging(_stagingBottom!, bands.Bottom, _width, t);
            ReadStaging(_stagingLeft!, bands.Left, t, _height);
            ReadStaging(_stagingRight!, bands.Right, t, _height);
        }
        finally
        {
            _duplication.ReleaseFrame();
        }

        return CaptureResult.FrameCaptured;
    }

    private void CopyRegion(ID3D11Texture2D src, ID3D11Texture2D dst, int x, int y, int w, int h)
    {
        var srcBox = new Box(x, y, 0, x + w, y + h, 1);
        _device!.ImmediateContext.CopySubresourceRegion(dst, 0, 0, 0, 0, src, 0, srcBox);
    }

    private void ReadStaging(ID3D11Texture2D staging, Span<byte> dst, int width, int height)
    {
        var mapped = _device!.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            if (dst.Length < rowBytes * height)
                throw new InvalidOperationException("Capture buffer too small.");

            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dstBase = dst)
                {
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(src + y * mapped.RowPitch, dstBase + y * rowBytes, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _device.ImmediateContext.Unmap(staging, 0);
        }
    }

    private void EnsureBandStagings(int thickness)
    {
        if (_bandThickness == thickness && _stagingTop != null) return;

        DisposeBandStagings();
        _bandThickness = thickness;
        _stagingTop = CreateStaging(_width, thickness);
        _stagingBottom = CreateStaging(_width, thickness);
        _stagingLeft = CreateStaging(thickness, _height);
        _stagingRight = CreateStaging(thickness, _height);
    }

    private ID3D11Texture2D CreateStaging(int width, int height)
    {
        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        };
        return _device!.CreateTexture2D(texDesc);
    }

    public void Dispose()
    {
        DisposeGpu();
        GC.SuppressFinalize(this);
    }

    private void DisposeBandStagings()
    {
        _stagingTop?.Dispose();
        _stagingBottom?.Dispose();
        _stagingLeft?.Dispose();
        _stagingRight?.Dispose();
        _stagingTop = null;
        _stagingBottom = null;
        _stagingLeft = null;
        _stagingRight = null;
        _bandThickness = 0;
    }

    private void DisposeGpu()
    {
        _stagingFull?.Dispose();
        DisposeBandStagings();
        _duplication?.Dispose();
        _device?.Dispose();
        _adapter?.Dispose();
        _stagingFull = null;
        _duplication = null;
        _device = null;
        _adapter = null;
        _initialized = false;
    }
}
