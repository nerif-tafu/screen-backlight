using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public sealed class LocalApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly StreamEngine _engine;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _port;

    public LocalApiServer(StreamEngine engine) => _engine = engine;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port => _port;

    public void Start(AppSettings settings)
    {
        if (!settings.EnableLocalApi) return;
        Start(settings.ApiPort);
    }

    public void Start(int port)
    {
        Stop();
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore shutdown races
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
            _listener?.Close();
            _listener = null;
        }
    }

    public void RestartIfNeeded(AppSettings settings)
    {
        if (!settings.EnableLocalApi)
        {
            Stop();
            return;
        }

        if (!IsRunning || _port != settings.ApiPort)
            Start(settings.ApiPort);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (context != null)
                    await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = context.Request.HttpMethod.ToUpperInvariant();

            if (path.Equals("/api/settings/schema", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                await WriteJsonAsync(context.Response, 200, SettingsCatalog.All);
                return;
            }

            if (path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase))
            {
                if (method == "GET")
                {
                    var snapshot = SettingsManager.WithLock(s => SettingsCatalog.Snapshot(s));
                    await WriteJsonAsync(context.Response, 200, snapshot);
                    return;
                }

                if (method is "PATCH" or "PUT")
                {
                    var body = await ReadBodyAsync(context.Request);
                    using var doc = JsonDocument.Parse(body);
                    if (SettingsManager.TryApplyPatch(doc.RootElement, out var patchError))
                    {
                        StartupService.Apply(App.Settings.StartOnBoot);
                        RestartApiIfPortChanged();
                        await WriteJsonAsync(context.Response, 200, SettingsCatalog.Snapshot(App.Settings));
                        return;
                    }

                    await WriteJsonAsync(context.Response, 400, new { error = patchError });
                    return;
                }
            }

            if (path.StartsWith("/api/settings/", StringComparison.OrdinalIgnoreCase))
            {
                var key = Uri.UnescapeDataString(path["/api/settings/".Length..]);
                if (method == "GET")
                {
                    if (!SettingsCatalog.Contains(key))
                    {
                        await WriteJsonAsync(context.Response, 404, new { error = $"Unknown setting '{key}'." });
                        return;
                    }

                    var value = SettingsManager.WithLock(s => SettingsCatalog.GetValue(s, key));
                    await WriteJsonAsync(context.Response, 200, new { key, value });
                    return;
                }

                if (method is "PUT" or "PATCH")
                {
                    if (!SettingsCatalog.Contains(key))
                    {
                        await WriteJsonAsync(context.Response, 404, new { error = $"Unknown setting '{key}'." });
                        return;
                    }

                    var body = await ReadBodyAsync(context.Request);
                    using var doc = JsonDocument.Parse(body);
                    var element = doc.RootElement;
                    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("value", out var wrapped))
                        element = wrapped;

                    string? setError = null;
                    try
                    {
                        SettingsManager.ApplyAndSave(s =>
                        {
                            if (!SettingsCatalog.TrySetValue(s, key, element, out setError))
                                throw new InvalidOperationException(setError);
                        });
                        StartupService.Apply(App.Settings.StartOnBoot);
                        RestartApiIfPortChanged();
                        await WriteJsonAsync(context.Response, 200, new
                        {
                            key,
                            value = SettingsCatalog.GetValue(App.Settings, key)
                        });
                    }
                    catch (InvalidOperationException ex)
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = ex.Message });
                    }

                    return;
                }
            }

            if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                var status = _engine.Status;
                await WriteJsonAsync(context.Response, 200, new
                {
                    connected = status.Connected,
                    streaming = status.Streaming,
                    reconnecting = status.Reconnecting,
                    captureFps = status.CaptureFps,
                    sendFps = status.SendFps,
                    message = status.Message,
                    captureBackend = status.CaptureBackend,
                    apiPort = Port,
                    apiEnabled = App.Settings.EnableLocalApi
                });
                return;
            }

            if (path.Equals("/api/connect", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                await _engine.ConnectAsync(App.Settings);
                if (App.Settings.SyncLayoutFromDevice)
                    await _engine.RefreshLayoutAsync(App.Settings);
                await WriteJsonAsync(context.Response, 200, new { ok = true, status = _engine.Status });
                return;
            }

            if (path.Equals("/api/disconnect", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                await _engine.DisconnectAsync();
                await WriteJsonAsync(context.Response, 200, new { ok = true, status = _engine.Status });
                return;
            }

            if (path.Equals("/api/stream/start", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!_engine.Status.Connected)
                    await _engine.ConnectAsync(App.Settings);
                await _engine.StartStreamingAsync(App.Settings);
                await WriteJsonAsync(context.Response, 200, new { ok = true, status = _engine.Status });
                return;
            }

            if (path.Equals("/api/stream/stop", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                await _engine.StopStreamingAsync();
                await WriteJsonAsync(context.Response, 200, new { ok = true, status = _engine.Status });
                return;
            }

            if (path.Equals("/api/layout/sync", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                await _engine.RefreshLayoutAsync(App.Settings);
                await WriteJsonAsync(context.Response, 200, new
                {
                    ok = true,
                    layout = _engine.DeviceConfig
                });
                return;
            }

            await WriteJsonAsync(context.Response, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
        }
        finally
        {
            try { context.Response.OutputStream.Close(); } catch { /* ignore */ }
        }
    }

    private void RestartApiIfPortChanged()
    {
        if (App.Settings.EnableLocalApi && App.Settings.ApiPort != _port)
            Start(App.Settings.ApiPort);
        else if (!App.Settings.EnableLocalApi)
            Stop();
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose() => Stop();
}
