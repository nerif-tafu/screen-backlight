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
    private ID3D11Texture2D? _staging;
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

        var texDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        };
        _staging = _device!.CreateTexture2D(texDesc);
        _initialized = true;
        targetOutput.Dispose();
    }

    public bool TryCapture(Span<byte> bgraBuffer, out int stride)
    {
        stride = 0;
        if (!_initialized || _duplication == null || _staging == null || _device == null)
            return false;

        var result = _duplication.AcquireNextFrame(100, out _, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            _initialized = false;
            return false;
        }

        result.CheckError();

        try
        {
            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            var srcBox = new Box(_left, _top, 0, _left + _width, _top + _height, 1);
            _device.ImmediateContext.CopySubresourceRegion(_staging, 0, 0, 0, 0, desktopTexture, 0, srcBox);

            var mapped = _device.ImmediateContext.Map(_staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                stride = (int)mapped.RowPitch;
                var rowBytes = _width * 4;
                if (bgraBuffer.Length < rowBytes * _height)
                    throw new InvalidOperationException("Capture buffer too small.");

                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    fixed (byte* dstBase = bgraBuffer)
                    {
                        for (var y = 0; y < _height; y++)
                            Buffer.MemoryCopy(src + y * mapped.RowPitch, dstBase + y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                _device.ImmediateContext.Unmap(_staging, 0);
            }
        }
        finally
        {
            _duplication.ReleaseFrame();
        }

        return true;
    }

    public void Dispose()
    {
        DisposeGpu();
        GC.SuppressFinalize(this);
    }

    private void DisposeGpu()
    {
        _staging?.Dispose();
        _duplication?.Dispose();
        _device?.Dispose();
        _adapter?.Dispose();
        _staging = null;
        _duplication = null;
        _device = null;
        _adapter = null;
        _initialized = false;
    }
}
