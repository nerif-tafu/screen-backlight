using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public sealed class DeviceApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<DeviceConfig?> FetchConfigAsync(string host, CancellationToken ct = default)
    {
        var uri = BuildUri(host, "/api/config");
        return await _http.GetFromJsonAsync<DeviceConfig>(uri, ct);
    }

    public async Task SaveBrightnessAsync(string host, int brightness, CancellationToken ct = default)
    {
        var uri = BuildUri(host, "/api/config");
        using var response = await _http.PostAsJsonAsync(uri, new { brightness }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonDocument?> FetchStatusAsync(string host, CancellationToken ct = default)
    {
        var uri = BuildUri(host, "/api/status");
        await using var stream = await _http.GetStreamAsync(uri, ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static Uri BuildUri(string host, string path)
    {
        host = host.Trim();
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;
        return new Uri(new Uri(host.TrimEnd('/')), path);
    }

    public void Dispose() => _http.Dispose();
}
