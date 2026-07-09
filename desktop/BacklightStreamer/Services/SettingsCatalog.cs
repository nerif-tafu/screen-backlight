using System.Reflection;
using System.Text.Json;
using BacklightStreamer.Models;

namespace BacklightStreamer.Services;

public sealed record SettingDefinition(
    string Key,
    string Type,
    string Description,
    object? DefaultValue = null,
    object? Minimum = null,
    object? Maximum = null);

public static class SettingsCatalog
{
    private static readonly Dictionary<string, PropertyInfo> Properties =
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => ToCamelCase(p.Name), p => p, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SettingDefinition> Definitions = BuildDefinitions();

    public static IReadOnlyList<SettingDefinition> All => Definitions.Values.ToList();

    public static bool Contains(string key) => Definitions.ContainsKey(NormalizeKey(key));

    public static SettingDefinition GetDefinition(string key) =>
        Definitions[NormalizeKey(key)];

    public static Dictionary<string, object?> Snapshot(AppSettings settings)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, prop) in Properties)
            result[key] = prop.GetValue(settings);
        return result;
    }

    public static object? GetValue(AppSettings settings, string key) =>
        Properties[NormalizeKey(key)].GetValue(settings);

    public static bool TrySetValue(AppSettings settings, string key, JsonElement value, out string? error)
    {
        error = null;
        key = NormalizeKey(key);
        if (!Properties.TryGetValue(key, out var prop))
        {
            error = $"Unknown setting '{key}'.";
            return false;
        }

        try
        {
            var converted = ConvertJson(value, prop.PropertyType);
            if (converted is null && prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
            {
                error = $"Invalid value for '{key}'.";
                return false;
            }

            if (!Validate(key, converted, out error))
                return false;

            prop.SetValue(settings, converted);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryApplyPatch(AppSettings settings, JsonElement patch, out string? error)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            error = "PATCH body must be a JSON object.";
            return false;
        }

        foreach (var property in patch.EnumerateObject())
        {
            if (!TrySetValue(settings, property.Name, property.Value, out error))
                return false;
        }

        error = null;
        return true;
    }

    private static bool Validate(string key, object? value, out string? error)
    {
        error = null;
        if (!Definitions.TryGetValue(key, out var def)) return true;

        if (value is int i)
        {
            if (def.Minimum is int min && i < min)
            {
                error = $"{key} must be >= {min}.";
                return false;
            }
            if (def.Maximum is int max && i > max)
            {
                error = $"{key} must be <= {max}.";
                return false;
            }
        }

        if (key.Equals("deviceHost", StringComparison.OrdinalIgnoreCase) && value is string host && string.IsNullOrWhiteSpace(host))
        {
            error = "deviceHost cannot be empty.";
            return false;
        }

        return true;
    }

    private static object? ConvertJson(JsonElement value, Type targetType)
    {
        if (targetType == typeof(string))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

        if (targetType == typeof(int))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out i)) return i;
            return null;
        }

        if (targetType == typeof(bool))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
            return null;
        }

        return JsonSerializer.Deserialize(value.GetRawText(), targetType);
    }

    private static string NormalizeKey(string key) => ToCamelCase(key.Trim());

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static Dictionary<string, SettingDefinition> BuildDefinitions() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["deviceHost"] = new("deviceHost", "string", "ESP32 IP or hostname", "192.168.3.180"),
        ["monitorIndex"] = new("monitorIndex", "int", "Zero-based monitor index to capture", 0, 0, 16),
        ["captureX"] = new("captureX", "int", "Custom capture region X offset (px)", 0, 0, 10000),
        ["captureY"] = new("captureY", "int", "Custom capture region Y offset (px)", 0, 0, 10000),
        ["captureWidth"] = new("captureWidth", "int", "Custom capture region width (px, 0 = full monitor)", 0, 0, 10000),
        ["captureHeight"] = new("captureHeight", "int", "Custom capture region height (px, 0 = full monitor)", 0, 0, 10000),
        ["useCustomCaptureRegion"] = new("useCustomCaptureRegion", "bool", "Use custom capture crop", false),
        ["borderInset"] = new("borderInset", "int", "Pixels inset from screen edge before sampling", 4, 0, 160),
        ["sampleRadius"] = new("sampleRadius", "int", "Pixels inward from inset to average per LED", 6, 1, 300),
        ["targetFps"] = new("targetFps", "int", "Screen capture rate (FPS)", 60, 1, 240),
        ["colorSmoothing"] = new("colorSmoothing", "int", "Blend/send FPS between captures (0 = off)", 30, 0, 240),
        ["brightness"] = new("brightness", "int", "Device LED brightness", 128, 0, 255),
        ["previewPanelHeight"] = new("previewPanelHeight", "int", "Preview panel height in the UI (px)", 260, 120, 720),
        ["autoConnect"] = new("autoConnect", "bool", "Connect automatically on launch", true),
        ["autoStream"] = new("autoStream", "bool", "Start streaming automatically after connect", false),
        ["startOnBoot"] = new("startOnBoot", "bool", "Launch app on Windows sign-in", false),
        ["startMinimized"] = new("startMinimized", "bool", "Start minimized to tray", false),
        ["minimizeToTray"] = new("minimizeToTray", "bool", "Minimize button hides to tray", true),
        ["syncLayoutFromDevice"] = new("syncLayoutFromDevice", "bool", "Pull LED layout from device on connect", true),
        ["apiPort"] = new("apiPort", "int", "Local HTTP API port (127.0.0.1)", 7890, 1024, 65535),
        ["enableLocalApi"] = new("enableLocalApi", "bool", "Enable local HTTP settings API", true),
        ["autoReconnect"] = new("autoReconnect", "bool", "Automatically reconnect after brief device Wi-Fi drops", true),
        ["reconnectIntervalMs"] = new("reconnectIntervalMs", "int", "Delay between reconnect attempts (ms)", 250, 100, 5000),
    };
}
