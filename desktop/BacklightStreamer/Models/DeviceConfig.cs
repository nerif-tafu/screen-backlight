using System.Text.Json.Serialization;

namespace BacklightStreamer.Models;

public sealed class DeviceConfig
{
    [JsonPropertyName("totalLedCount")] public int TotalLedCount { get; set; } = 120;
    [JsonPropertyName("leftStart")] public int LeftStart { get; set; }
    [JsonPropertyName("leftEnd")] public int LeftEnd { get; set; } = 29;
    [JsonPropertyName("topStart")] public int TopStart { get; set; } = 30;
    [JsonPropertyName("topEnd")] public int TopEnd { get; set; } = 59;
    [JsonPropertyName("rightStart")] public int RightStart { get; set; } = 60;
    [JsonPropertyName("rightEnd")] public int RightEnd { get; set; } = 89;
    [JsonPropertyName("bottomStart")] public int BottomStart { get; set; } = 90;
    [JsonPropertyName("bottomEnd")] public int BottomEnd { get; set; } = 119;
    [JsonPropertyName("layoutTotal")] public int LayoutTotal { get; set; } = 120;
    [JsonPropertyName("brightness")] public int Brightness { get; set; } = 128;
    [JsonPropertyName("maxFps")] public int MaxFps { get; set; } = 60;
    [JsonPropertyName("reverseLeft")] public bool ReverseLeft { get; set; }
    [JsonPropertyName("reverseTop")] public bool ReverseTop { get; set; }
    [JsonPropertyName("reverseRight")] public bool ReverseRight { get; set; }
    [JsonPropertyName("reverseBottom")] public bool ReverseBottom { get; set; }
}
