using System.Diagnostics;
using System.Drawing;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public sealed class StreamEngine : IDisposable
{
    public event Action<StreamStatus>? StatusChanged;
    public event Action<StreamFramePreview>? FramePreview;

    private readonly DeviceApiClient _api = new();
    private readonly WebSocketStreamer _ws = new();
    private readonly CompositeScreenCapture _capture = new();
    private readonly LedLayoutMapper _mapper = new();
    private readonly FrameInterpolator _interpolator = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private byte[]? _frameBuffer;
    private byte[]? _stripRgbBuffer;
    private byte[]? _outputRgbBuffer;
    private byte[]? _packetBuffer;
    private ushort _frameNumber;
    private long _lastPreviewTicks;

    public DeviceConfig? DeviceConfig { get; private set; }
    public StreamStatus Status { get; private set; } = new();

    public async Task ConnectAsync(AppSettings settings, CancellationToken ct = default)
    {
        DeviceConfig = await _api.FetchConfigAsync(settings.DeviceHost, ct)
            ?? throw new InvalidOperationException("Could not load device config.");

        await _ws.ConnectAsync(settings.DeviceHost, ct);
        UpdateStatus(s => s with { Connected = true, Message = "Connected" });
    }

    public async Task DisconnectAsync()
    {
        await StopStreamingAsync();
        await _ws.DisconnectAsync();
        UpdateStatus(s => s with { Connected = false, Streaming = false, Message = "Disconnected" });
    }

    public async Task RefreshLayoutAsync(AppSettings settings, CancellationToken ct = default)
    {
        DeviceConfig = await _api.FetchConfigAsync(settings.DeviceHost, ct)
            ?? throw new InvalidOperationException("Could not refresh device config.");
        UpdateStatus(s => s with { Message = "Layout synced from device" });
    }

    public async Task SetBrightnessAsync(AppSettings settings, int brightness, CancellationToken ct = default)
    {
        brightness = Math.Clamp(brightness, 0, 255);
        if (DeviceConfig != null)
            DeviceConfig.Brightness = brightness;

        if (_ws.IsConnected)
            await _ws.SendJsonAsync($"{{\"cmd\":\"brightness\",\"value\":{brightness}}}", ct);

        await _api.SaveBrightnessAsync(settings.DeviceHost, brightness, ct);
    }

    public Task StartStreamingAsync(AppSettings settings)
    {
        if (_loopTask != null) return Task.CompletedTask;
        if (DeviceConfig == null)
            throw new InvalidOperationException("Connect to device first.");

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => StreamLoop(settings, _loopCts.Token));
        UpdateStatus(s => s with { Streaming = true, Message = "Starting stream…" });
        return Task.CompletedTask;
    }

    public async Task StopStreamingAsync()
    {
        if (_loopCts == null) return;
        _loopCts.Cancel();
        try
        {
            if (_loopTask != null)
                await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loopCts.Dispose();
            _loopCts = null;
            _loopTask = null;
            UpdateStatus(s => s with { Streaming = false, CaptureFps = 0, SendFps = 0, Message = "Stream stopped" });
        }
    }

    private async Task StreamLoop(AppSettings settings, CancellationToken ct)
    {
        var config = DeviceConfig!;
        var captureRect = ResolveCaptureRect(settings);

        _capture.Initialize(settings.MonitorIndex, captureRect);
        var localRect = new Rectangle(0, 0, captureRect.Width, captureRect.Height);
        _mapper.Build(config, localRect, settings.BorderInset, settings.SampleRadius);

        var stripCount = Math.Max(1, config.TotalLedCount);
        var streamCount = StreamPacketBuilder.LayoutLedCount(config);
        _frameBuffer = new byte[captureRect.Width * captureRect.Height * 4];
        _stripRgbBuffer = new byte[stripCount * 3];
        _outputRgbBuffer = new byte[stripCount * 3];
        _packetBuffer = new byte[4 + streamCount * 3];
        _frameNumber = 0;
        _interpolator.Reset(stripCount * 3);

        await _ws.SendJsonAsync("{\"cmd\":\"off\"}", ct);

        var sw = Stopwatch.StartNew();
        var captureCount = 0;
        var sendCount = 0;
        var statsAt = sw.Elapsed;
        var captureBackend = _capture.BackendName;
        var lastInset = settings.BorderInset;
        var lastRadius = settings.SampleRadius;
        var nextCaptureAt = TimeSpan.Zero;
        var nextSendAt = TimeSpan.Zero;
        var keyframeAt = TimeSpan.Zero;

        UpdateStatus(s => s with
        {
            Message = $"Streaming via {captureBackend}",
            CaptureBackend = captureBackend
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (settings.BorderInset != lastInset || settings.SampleRadius != lastRadius)
                {
                    _mapper.Build(config, localRect, settings.BorderInset, settings.SampleRadius);
                    lastInset = settings.BorderInset;
                    lastRadius = settings.SampleRadius;
                }

                var captureFps = Math.Clamp(settings.TargetFps, 1, 240);
                var blendFps = settings.ColorSmoothing > 0
                    ? Math.Clamp(settings.ColorSmoothing, 1, 240)
                    : captureFps;
                if (config.MaxFps > 0)
                    blendFps = Math.Min(blendFps, config.MaxFps);

                var interpolate = settings.ColorSmoothing > 0;
                var captureInterval = TimeSpan.FromSeconds(1.0 / captureFps);
                var sendInterval = TimeSpan.FromSeconds(1.0 / blendFps);

                var now = sw.Elapsed;
                var capturedThisTick = false;

                if (now >= nextCaptureAt)
                {
                    if (_capture.TryCapture(_frameBuffer, out _))
                    {
                        ColorSampler.SampleFrame(
                            _frameBuffer,
                            _capture.Width,
                            _capture.Height,
                            _mapper.StripRegions,
                            _stripRgbBuffer);

                        if (interpolate)
                        {
                            _interpolator.PushKeyframe(_stripRgbBuffer);
                            keyframeAt = now;
                        }
                        else
                        {
                            _stripRgbBuffer.AsSpan().CopyTo(_outputRgbBuffer!);
                        }

                        captureCount++;
                        capturedThisTick = true;
                    }

                    nextCaptureAt = now + captureInterval;
                }

                var shouldSend = interpolate
                    ? _interpolator.HasKeyframe && now >= nextSendAt
                    : capturedThisTick;

                if (shouldSend)
                {
                    if (interpolate)
                    {
                        var elapsed = (now - keyframeAt).TotalSeconds;
                        var t = captureInterval.TotalSeconds > 0
                            ? elapsed / captureInterval.TotalSeconds
                            : 1.0;
                        _interpolator.WriteBlend(t, _outputRgbBuffer!);
                    }

                    StreamPacketBuilder.PackStreamFrame(_outputRgbBuffer!, config, _packetBuffer.AsSpan(4));
                    WriteHeader(_packetBuffer, ++_frameNumber, streamCount);
                    await _ws.SendBinaryAsync(_packetBuffer, ct);
                    sendCount++;
                    EmitPreview(config, captureBackend, settings);

                    if (interpolate)
                        nextSendAt = now + sendInterval;
                }

                if (sw.Elapsed - statsAt >= TimeSpan.FromSeconds(1))
                {
                    var elapsed = (sw.Elapsed - statsAt).TotalSeconds;
                    UpdateStatus(s => s with
                    {
                        CaptureFps = captureCount / elapsed,
                        SendFps = sendCount / elapsed,
                        CaptureBackend = captureBackend
                    });
                    captureCount = 0;
                    sendCount = 0;
                    statsAt = sw.Elapsed;
                }

                var waitUntil = interpolate
                    ? MinPositive(nextCaptureAt - now, _interpolator.HasKeyframe ? nextSendAt - now : TimeSpan.FromMilliseconds(4))
                    : nextCaptureAt - now;

                if (waitUntil > TimeSpan.Zero)
                    await Task.Delay(waitUntil, ct);
                else
                    await Task.Delay(1, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UpdateStatus(s => s with { Message = ex.Message, Streaming = false });
        }
    }

    private static TimeSpan MinPositive(TimeSpan a, TimeSpan b)
    {
        if (a <= TimeSpan.Zero) return b > TimeSpan.Zero ? b : TimeSpan.FromMilliseconds(1);
        if (b <= TimeSpan.Zero) return a;
        return a < b ? a : b;
    }

    private void EmitPreview(DeviceConfig config, string captureBackend, AppSettings settings)
    {
        if (FramePreview == null || _outputRgbBuffer == null || _frameBuffer == null) return;

        var now = Environment.TickCount64;
        if (now - _lastPreviewTicks < 66) return;
        _lastPreviewTicks = now;

        var (pixels, previewW, previewH) = DownscaleBgra(
            _frameBuffer,
            _capture.Width,
            _capture.Height,
            480);

        FramePreview.Invoke(PreviewStateBuilder.Build(
            settings,
            config,
            pixels,
            previewW,
            previewH,
            (byte[])_outputRgbBuffer.Clone(),
            captureBackend,
            isLive: true));
    }

    private static (byte[] pixels, int width, int height) DownscaleBgra(
        byte[] src,
        int srcW,
        int srcH,
        int maxWidth)
    {
        if (srcW <= maxWidth)
            return ((byte[])src.Clone(), srcW, srcH);

        var dstW = maxWidth;
        var dstH = Math.Max(1, (int)Math.Round(srcH * (maxWidth / (double)srcW)));
        var dst = new byte[dstW * dstH * 4];

        for (var y = 0; y < dstH; y++)
        {
            var srcY = y * srcH / dstH;
            var srcRow = srcY * srcW * 4;
            var dstRow = y * dstW * 4;
            for (var x = 0; x < dstW; x++)
            {
                var srcX = x * srcW / dstW;
                var srcI = srcRow + srcX * 4;
                var dstI = dstRow + x * 4;
                dst[dstI] = src[srcI];
                dst[dstI + 1] = src[srcI + 1];
                dst[dstI + 2] = src[srcI + 2];
                dst[dstI + 3] = 255;
            }
        }

        return (dst, dstW, dstH);
    }

    private static void WriteHeader(byte[] packet, ushort frameNumber, int ledCount)
    {
        packet[0] = (byte)(frameNumber & 0xFF);
        packet[1] = (byte)((frameNumber >> 8) & 0xFF);
        packet[2] = (byte)(ledCount & 0xFF);
        packet[3] = (byte)((ledCount >> 8) & 0xFF);
    }

    public static Rectangle ResolveCaptureRect(AppSettings settings)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("No monitors detected.");

        var index = Math.Clamp(settings.MonitorIndex, 0, screens.Length - 1);
        var screen = screens[index].Bounds;

        if (!settings.UseCustomCaptureRegion
            || settings.CaptureWidth <= 0
            || settings.CaptureHeight <= 0)
            return screen;

        return Rectangle.FromLTRB(
            screen.Left + settings.CaptureX,
            screen.Top + settings.CaptureY,
            screen.Left + settings.CaptureX + settings.CaptureWidth,
            screen.Top + settings.CaptureY + settings.CaptureHeight);
    }

    private void UpdateStatus(Func<StreamStatus, StreamStatus> update)
    {
        Status = update(Status);
        StatusChanged?.Invoke(Status);
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _capture.Dispose();
        _api.Dispose();
        _ = _ws.DisposeAsync();
    }
}

public record StreamStatus(
    bool Connected = false,
    bool Streaming = false,
    double CaptureFps = 0,
    double SendFps = 0,
    string Message = "Idle",
    string CaptureBackend = "");
