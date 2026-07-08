#pragma once

#include <Arduino.h>
#include <Preferences.h>
#include <ArduinoJson.h>

static constexpr uint16_t MAX_LEDS = 512;

enum class ColorOrder : uint8_t {
    RGB = 0,
    GRB = 1,
    RBG = 2,
    BRG = 3,
    BGR = 4,
    GBR = 5,
};

struct EdgeRange {
    uint16_t start;
    uint16_t end;
};

struct DeviceConfig {
    char wifiSsid[33];
    char wifiPassword[65];

    uint16_t totalLedCount;
    EdgeRange left;
    EdgeRange right;
    EdgeRange top;
    EdgeRange bottom;
    uint8_t brightness;
    ColorOrder colorOrder;

    bool gammaCorrection;
    uint8_t maxFps;

    bool reverseLeft;
    bool reverseTop;
    bool reverseRight;
    bool reverseBottom;
};

inline uint16_t edgeSpanLength(uint16_t start, uint16_t end) {
    return (start <= end) ? static_cast<uint16_t>(end - start + 1)
                          : static_cast<uint16_t>(start - end + 1);
}

inline uint16_t edgeSpanMaxIndex(uint16_t start, uint16_t end) {
    return (start > end) ? start : end;
}

class ConfigManager {
public:
    static ConfigManager& instance();

    bool begin();
    void load();
    void save();

    DeviceConfig& config() { return _config; }
    const DeviceConfig& config() const { return _config; }

    void setDefaults();
    bool hasWifiCredentials() const;
    uint16_t layoutTotal() const;
    bool layoutExceedsStrip() const;

    static const EdgeRange& edgeByIndex(const DeviceConfig& cfg, uint8_t edgeIndex);
    static bool reverseByIndex(const DeviceConfig& cfg, uint8_t edgeIndex);

    bool applyFromJson(const JsonObject& obj, String& errorOut);

    void toJson(JsonObject& obj) const;
    String exportJson() const;
    bool importJson(const char* json, String& errorOut);

private:
    ConfigManager() = default;

    void migrateLegacyLayout();
    bool validateEdgeRange(const EdgeRange& range, String& errorOut) const;

    Preferences _prefs;
    DeviceConfig _config{};
    static constexpr const char* NAMESPACE = "backlight";
};
