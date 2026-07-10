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
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private byte[]? _frameBuffer;
    private byte[]? _stripRgbBuffer;
    private byte[]? _outputRgbBuffer;
    private byte[]? _packetBuffer;
    private readonly EdgeBandBuffers _bands = new();
    private bool _bandsUsable;
    private bool _lastFrameWasFull;
    private readonly byte[]?[] _previewPixelBuffers = new byte[2][];
    private readonly byte[]?[] _previewStripBuffers = new byte[2][];
    private int _previewBufferIndex;
    private ushort _frameNumber;
    private long _lastPreviewTicks;
    private long _lastSendOkAt;
    private bool _autoReconnectEnabled;
    private bool _streamDesired;
    private AppSettings? _activeSettings;

    public DeviceConfig? DeviceConfig { get; private set; }
    public StreamStatus Status { get; private set; } = new();

    /// <summary>
    /// When false (main window hidden or minimized) the engine captures only
    /// the screen-edge bands it actually samples and skips preview generation
    /// entirely, keeping background impact minimal while gaming.
    /// </summary>
    public bool PreviewEnabled { get; set; } = true;

    public async Task ConnectAsync(AppSettings settings, CancellationToken ct = default)
    {
        _activeSettings = settings;
        _autoReconnectEnabled = true;
        if (!await EstablishConnectionAsync(settings, ct, userInitiated: true))
            throw new InvalidOperationException(Status.Message);
        StartWatchdog(settings);
    }

    public async Task DisconnectAsync()
    {
        _autoReconnectEnabled = false;
        _streamDesired = false;
        StopWatchdog();
        await StopStreamingAsync();
        await _ws.DisconnectAsync();
        UpdateStatus(s => s with
        {
            Connected = false,
            Streaming = false,
            Reconnecting = false,
            Message = "Disconnected"
        });
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
        _activeSettings = settings;
        _streamDesired = true;
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
        _streamDesired = false;
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
            UpdateStatus(s => s with { Streaming = false, CaptureFps = 0, SendFps = 0, Message = Status.Connected ? "Connected — stream stopped" : Status.Message });
        }
    }

    private void StartWatchdog(AppSettings settings)
    {
        StopWatchdog();
        _watchdogCts = new CancellationTokenSource();
        _watchdogTask = Task.Run(() => WatchdogLoop(_watchdogCts.Token));
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = null;
        _watchdogTask = null;
    }

    private async Task WatchdogLoop(CancellationToken ct)
    {
        var healthCheckAt = 0L;
        while (!ct.IsCancellationRequested)
        {
            var settings = _activeSettings ?? App.Settings;
            try
            {
                if (_autoReconnectEnabled && settings.AutoReconnect)
                {
                    if (!_ws.IsConnected)
                    {
                        await TryReconnectAsync(settings, ct);
                    }
                    else if (Environment.TickCount64 - healthCheckAt >= 5000)
                    {
                        healthCheckAt = Environment.TickCount64;
                        // A recent successful stream send already proves the
                        // device is reachable — only spend an HTTP ping when
                        // the socket has been quiet.
                        var streamHealthy = _streamDesired
                            && Environment.TickCount64 - Volatile.Read(ref _lastSendOkAt) < 3000;
                        if (!streamHealthy && !await PingDeviceAsync(settings, ct))
                            await MarkConnectionLostAsync("Device unreachable");
                    }
                }

                if (_streamDesired && _loopTask is { IsCompleted: true } && _activeSettings != null)
                {
                    _loopTask = null;
                    _loopCts?.Dispose();
                    _loopCts = new CancellationTokenSource();
                    _loopTask = Task.Run(() => StreamLoop(_activeSettings, _loopCts.Token));
                    UpdateStatus(s => s with { Streaming = true, Message = "Resuming stream…" });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // keep watchdog alive
            }

            // Poll fast only while actually reconnecting; a healthy connection
            // needs no sub-second wakeups.
            var delay = _ws.IsConnected
                ? 1000
                : Math.Clamp(settings.ReconnectIntervalMs, 100, 5000);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> PingDeviceAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            return await _api.FetchConfigAsync(settings.DeviceHost, cts.Token) != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryReconnectAsync(AppSettings settings, CancellationToken ct)
    {
        if (!await _reconnectLock.WaitAsync(0, ct))
            return _ws.IsConnected;

        try
        {
            if (_ws.IsConnected) return true;

            UpdateStatus(s => s with
            {
                Connected = false,
                Reconnecting = true,
                Message = "Reconnecting…"
            });

            return await EstablishConnectionAsync(settings, ct, userInitiated: false);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task<bool> EstablishConnectionAsync(AppSettings settings, CancellationToken ct, bool userInitiated)
    {
        try
        {
            DeviceConfig = await _api.FetchConfigAsync(settings.DeviceHost, ct)
                ?? throw new InvalidOperationException("Could not load device config.");

            await _ws.ConnectAsync(settings.DeviceHost, ct);
            UpdateStatus(s => s with
            {
                Connected = true,
                Reconnecting = false,
                Message = userInitiated ? "Connected" : "Reconnected"
            });
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus(s => s with
            {
                Connected = false,
                Reconnecting = _autoReconnectEnabled && settings.AutoReconnect,
                Message = userInitiated ? ex.Message : "Reconnecting…"
            });
            return false;
        }
    }

    private async Task<bool> EnsureConnectedAsync(AppSettings settings, CancellationToken ct)
    {
        if (_ws.IsConnected) return true;
        if (!_autoReconnectEnabled || !settings.AutoReconnect) return false;
        return await TryReconnectAsync(settings, ct);
    }

    private async Task MarkConnectionLostAsync(string message)
    {
        await _ws.InvalidateAsync();
        UpdateStatus(s => s with
        {
            Connected = false,
            Reconnecting = _autoReconnectEnabled && (_activeSettings?.AutoReconnect ?? true),
            Message = message
        });
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
        var frameBytes = captureRect.Width * captureRect.Height * 4;
        _stripRgbBuffer = new byte[stripCount * 3];
        _outputRgbBuffer = new byte[stripCount * 3];
        _packetBuffer = new byte[4 + streamCount * 3];
        _frameNumber = 0;
        _interpolator.Reset(stripCount * 3);
        ConfigureBands(settings, localRect);

        if (await EnsureConnectedAsync(settings, ct))
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
                    ConfigureBands(settings, localRect);
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
                    // Full-frame readback is only needed to render the on-screen
                    // preview; while the window is hidden, read back just the
                    // edge bands the LEDs sample (orders of magnitude less data).
                    var useFullFrame = !_bandsUsable || (PreviewEnabled && FramePreview != null);
                    // The full-frame buffer is large (~33 MB at 4K); only
                    // allocate it once the preview actually needs it.
                    if (useFullFrame && (_frameBuffer == null || _frameBuffer.Length != frameBytes))
                        _frameBuffer = new byte[frameBytes];
                    var captureResult = useFullFrame
                        ? _capture.TryCapture(_frameBuffer, out _)
                        : _capture.TryCaptureBands(_bands);

                    if (captureResult == CaptureResult.FrameCaptured)
                    {
                        if (useFullFrame)
                            ColorSampler.SampleFrame(
                                _frameBuffer,
                                _capture.Width,
                                _capture.Height,
                                _mapper.StripRegions,
                                _stripRgbBuffer);
                        else
                            ColorSampler.SampleBands(_bands, _mapper.StripRegions, _stripRgbBuffer);
                        _lastFrameWasFull = useFullFrame;

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

                    if (await EnsureConnectedAsync(settings, ct))
                    {
                        StreamPacketBuilder.PackStreamFrame(_outputRgbBuffer!, config, _packetBuffer.AsSpan(4));
                        WriteHeader(_packetBuffer, ++_frameNumber, streamCount);
                        if (await _ws.SendBinaryAsync(_packetBuffer, ct))
                        {
                            sendCount++;
                            Volatile.Write(ref _lastSendOkAt, Environment.TickCount64);
                            EmitPreview(config, captureBackend, settings);
                        }
                        else
                        {
                            await MarkConnectionLostAsync("Connection lost — reconnecting…");
                        }
                    }

                    if (interpolate)
                        nextSendAt = now + sendInterval;
                }

                if (sw.Elapsed - statsAt >= TimeSpan.FromSeconds(1))
                {
                    var elapsed = (sw.Elapsed - statsAt).TotalSeconds;
                    captureBackend = _capture.BackendName;
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

    private void ConfigureBands(AppSettings settings, Rectangle localRect)
    {
        var depth = LedLayoutMapper.SampleDepth(localRect, settings.BorderInset, settings.SampleRadius);
        var thickness = settings.BorderInset + depth;
        // Bands must fully contain every sample region; if the inset pushes
        // regions deeper than the capture rect allows, stay on the full-frame
        // path so colors remain correct.
        _bandsUsable = thickness <= Math.Min(localRect.Width, localRect.Height);
        if (_bandsUsable)
            _bands.Configure(localRect.Width, localRect.Height, thickness);
    }

    private void EmitPreview(DeviceConfig config, string captureBackend, AppSettings settings)
    {
        if (!PreviewEnabled || !_lastFrameWasFull) return;
        if (FramePreview == null || _outputRgbBuffer == null || _frameBuffer == null) return;

        var now = Environment.TickCount64;
        if (now - _lastPreviewTicks < 66) return;
        _lastPreviewTicks = now;

        // Alternate between two reusable buffers so the UI thread can still be
        // reading the previous preview while the next one is written.
        _previewBufferIndex ^= 1;
        var (pixels, previewW, previewH) = DownscaleBgra(
            _frameBuffer,
            _capture.Width,
            _capture.Height,
            480,
            ref _previewPixelBuffers[_previewBufferIndex]);

        var strip = _previewStripBuffers[_previewBufferIndex];
        if (strip == null || strip.Length != _outputRgbBuffer.Length)
            _previewStripBuffers[_previewBufferIndex] = strip = new byte[_outputRgbBuffer.Length];
        _outputRgbBuffer.AsSpan().CopyTo(strip);

        FramePreview.Invoke(PreviewStateBuilder.Build(
            settings,
            config,
            pixels,
            previewW,
            previewH,
            strip,
            captureBackend,
            isLive: true,
            precomputedRegions: _mapper.StripRegions,
            sourceSize: new Size(_capture.Width, _capture.Height)));
    }

    private static (byte[] pixels, int width, int height) DownscaleBgra(
        byte[] src,
        int srcW,
        int srcH,
        int maxWidth,
        ref byte[]? buffer)
    {
        int dstW, dstH;
        if (srcW <= maxWidth)
        {
            dstW = srcW;
            dstH = srcH;
        }
        else
        {
            dstW = maxWidth;
            dstH = Math.Max(1, (int)Math.Round(srcH * (maxWidth / (double)srcW)));
        }

        var needed = dstW * dstH * 4;
        if (buffer == null || buffer.Length != needed)
            buffer = new byte[needed];

        if (srcW <= maxWidth)
        {
            src.AsSpan(0, needed).CopyTo(buffer);
            return (buffer, dstW, dstH);
        }

        var dst = buffer;
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
        _autoReconnectEnabled = false;
        StopWatchdog();
        _loopCts?.Cancel();
        _capture.Dispose();
        _api.Dispose();
        _reconnectLock.Dispose();
        _ = _ws.DisposeAsync();
    }
}

public record StreamStatus(
    bool Connected = false,
    bool Streaming = false,
    bool Reconnecting = false,
    double CaptureFps = 0,
    double SendFps = 0,
    string Message = "Idle",
    string CaptureBackend = "");
