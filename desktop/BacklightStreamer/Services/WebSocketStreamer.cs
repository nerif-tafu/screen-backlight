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
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _ws.ConnectAsync(uri, ct);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            await _ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendJsonAsync(string json, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
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
