using System.Net.WebSockets;
using System.Text;

namespace BacklightStreamer.Services;

public sealed class WebSocketStreamer : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, CancellationToken ct = default)
    {
        await DisconnectAsync();

        host = host.Trim();
        if (!host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            host = "ws://" + host.TrimEnd('/');

        var uri = new Uri(new Uri(host), "/ws");
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        await _ws.ConnectAsync(uri, ct);
    }

    public async Task<bool> SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return false;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return false;
            await _ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct);
            return true;
        }
        catch
        {
            await InvalidateAsync();
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<bool> SendJsonAsync(string json, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return false;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return false;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch
        {
            await InvalidateAsync();
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task InvalidateAsync()
    {
        if (_ws == null) return;
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "lost", CancellationToken.None);
        }
        catch
        {
            // ignore close errors
        }
        finally
        {
            _ws.Dispose();
            _ws = null;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_ws == null) return;
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch
        {
            // ignore close errors
        }
        finally
        {
            _ws.Dispose();
            _ws = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }
}
