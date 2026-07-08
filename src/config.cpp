#include "config.h"
#include <ArduinoJson.h>

ConfigManager& ConfigManager::instance() {
    static ConfigManager inst;
    return inst;
}

bool ConfigManager::begin() {
    return _prefs.begin(NAMESPACE, false);
}

void ConfigManager::setDefaults() {
    memset(_config.wifiSsid, 0, sizeof(_config.wifiSsid));
    memset(_config.wifiPassword, 0, sizeof(_config.wifiPassword));
    _config.totalLedCount = 120;
    _config.left = {0, 29};
    _config.right = {60, 89};
    _config.top = {30, 59};
    _config.bottom = {90, 119};
    _config.brightness = 128;
    _config.colorOrder = ColorOrder::GRB;
    _config.gammaCorrection = true;
    _config.maxFps = 60;
    _config.reverseLeft = false;
    _config.reverseTop = false;
    _config.reverseRight = false;
    _config.reverseBottom = false;
}

void ConfigManager::migrateLegacyLayout() {
    if (_prefs.getUChar("layoutVer", 0) >= 2) return;

    if (!_prefs.isKey("leftLeds")) {
        _prefs.putUChar("layoutVer", 2);
        return;
    }

    uint16_t offset = 0;
    auto migrateEdge = [&](const char* countKey, EdgeRange& range) {
        uint16_t count = _prefs.getUShort(countKey, 0);
        range.start = offset;
        range.end = count > 0 ? static_cast<uint16_t>(offset + count - 1) : offset;
        offset = static_cast<uint16_t>(offset + count);
    };

    migrateEdge("leftLeds", _config.left);
    migrateEdge("topLeds", _config.top);
    migrateEdge("rightLeds", _config.right);
    migrateEdge("bottomLeds", _config.bottom);
    _prefs.putUChar("layoutVer", 2);
}

void ConfigManager::load() {
    setDefaults();

    _config.totalLedCount = _prefs.getUShort("totalLeds", _config.totalLedCount);
    _config.left.start = _prefs.getUShort("leftStart", _config.left.start);
    _config.left.end = _prefs.getUShort("leftEnd", _config.left.end);
    _config.right.start = _prefs.getUShort("rightStart", _config.right.start);
    _config.right.end = _prefs.getUShort("rightEnd", _config.right.end);
    _config.top.start = _prefs.getUShort("topStart", _config.top.start);
    _config.top.end = _prefs.getUShort("topEnd", _config.top.end);
    _config.bottom.start = _prefs.getUShort("bottomStart", _config.bottom.start);
    _config.bottom.end = _prefs.getUShort("bottomEnd", _config.bottom.end);
    _config.brightness = _prefs.getUChar("brightness", _config.brightness);
    _config.colorOrder = static_cast<ColorOrder>(_prefs.getUChar("colorOrder", static_cast<uint8_t>(_config.colorOrder)));
    _config.gammaCorrection = _prefs.getBool("gamma", _config.gammaCorrection);
    _config.maxFps = _prefs.getUChar("maxFps", _config.maxFps);
    _config.reverseLeft = _prefs.getBool("revLeft", _config.reverseLeft);
    _config.reverseTop = _prefs.getBool("revTop", _config.reverseTop);
    _config.reverseRight = _prefs.getBool("revRight", _config.reverseRight);
    _config.reverseBottom = _prefs.getBool("revBottom", _config.reverseBottom);

    migrateLegacyLayout();

    _prefs.getString("wifiSsid", _config.wifiSsid, sizeof(_config.wifiSsid));
    _prefs.getString("wifiPass", _config.wifiPassword, sizeof(_config.wifiPassword));

#if defined(WIFI_SSID)
    strlcpy(_config.wifiSsid, WIFI_SSID, sizeof(_config.wifiSsid));
#if defined(WIFI_PASSWORD)
    strlcpy(_config.wifiPassword, WIFI_PASSWORD, sizeof(_config.wifiPassword));
#endif
    save();
#endif
}

void ConfigManager::save() {
    _prefs.putUShort("totalLeds", _config.totalLedCount);
    _prefs.putUShort("leftStart", _config.left.start);
    _prefs.putUShort("leftEnd", _config.left.end);
    _prefs.putUShort("rightStart", _config.right.start);
    _prefs.putUShort("rightEnd", _config.right.end);
    _prefs.putUShort("topStart", _config.top.start);
    _prefs.putUShort("topEnd", _config.top.end);
    _prefs.putUShort("bottomStart", _config.bottom.start);
    _prefs.putUShort("bottomEnd", _config.bottom.end);
    _prefs.putUChar("layoutVer", 2);
    _prefs.putUChar("brightness", _config.brightness);
    _prefs.putUChar("colorOrder", static_cast<uint8_t>(_config.colorOrder));
    _prefs.putBool("gamma", _config.gammaCorrection);
    _prefs.putUChar("maxFps", _config.maxFps);
    _prefs.putBool("revLeft", _config.reverseLeft);
    _prefs.putBool("revTop", _config.reverseTop);
    _prefs.putBool("revRight", _config.reverseRight);
    _prefs.putBool("revBottom", _config.reverseBottom);
    _prefs.putString("wifiSsid", _config.wifiSsid);
    _prefs.putString("wifiPass", _config.wifiPassword);
}

bool ConfigManager::hasWifiCredentials() const {
    return _config.wifiSsid[0] != '\0';
}

uint16_t ConfigManager::layoutTotal() const {
    return edgeSpanLength(_config.left.start, _config.left.end)
         + edgeSpanLength(_config.right.start, _config.right.end)
         + edgeSpanLength(_config.top.start, _config.top.end)
         + edgeSpanLength(_config.bottom.start, _config.bottom.end);
}

bool ConfigManager::layoutExceedsStrip() const {
    if (_config.totalLedCount == 0) return true;

    const EdgeRange edges[] = {
        _config.left, _config.right, _config.top, _config.bottom
    };
    for (const EdgeRange& edge : edges) {
        if (edge.start >= _config.totalLedCount || edge.end >= _config.totalLedCount) {
            return true;
        }
    }
    return false;
}

const EdgeRange& ConfigManager::edgeByIndex(const DeviceConfig& cfg, uint8_t edgeIndex) {
    switch (edgeIndex) {
        case 0: return cfg.left;
        case 1: return cfg.top;
        case 2: return cfg.right;
        case 3: return cfg.bottom;
        default: return cfg.left;
    }
}

bool ConfigManager::reverseByIndex(const DeviceConfig& cfg, uint8_t edgeIndex) {
    switch (edgeIndex) {
        case 0: return cfg.reverseLeft;
        case 1: return cfg.reverseTop;
        case 2: return cfg.reverseRight;
        case 3: return cfg.reverseBottom;
        default: return false;
    }
}

bool ConfigManager::validateEdgeRange(const EdgeRange& range, String& errorOut) const {
    if (range.start >= MAX_LEDS || range.end >= MAX_LEDS) {
        errorOut = "Edge index out of range";
        return false;
    }
    if (range.start >= _config.totalLedCount || range.end >= _config.totalLedCount) {
        errorOut = "Edge index exceeds strip length";
        return false;
    }
    return true;
}

static ColorOrder parseColorOrder(const char* s) {
    if (!s) return ColorOrder::GRB;
    if (strcasecmp(s, "RGB") == 0) return ColorOrder::RGB;
    if (strcasecmp(s, "GRB") == 0) return ColorOrder::GRB;
    if (strcasecmp(s, "RBG") == 0) return ColorOrder::RBG;
    if (strcasecmp(s, "BRG") == 0) return ColorOrder::BRG;
    if (strcasecmp(s, "BGR") == 0) return ColorOrder::BGR;
    if (strcasecmp(s, "GBR") == 0) return ColorOrder::GBR;
    return ColorOrder::GRB;
}

static const char* colorOrderToString(ColorOrder order) {
    switch (order) {
        case ColorOrder::RGB: return "RGB";
        case ColorOrder::GRB: return "GRB";
        case ColorOrder::RBG: return "RBG";
        case ColorOrder::BRG: return "BRG";
        case ColorOrder::BGR: return "BGR";
        case ColorOrder::GBR: return "GBR";
        default: return "GRB";
    }
}

static void edgeToJson(JsonObject& obj, const char* prefix, const EdgeRange& range) {
    obj[String(prefix) + "Start"] = range.start;
    obj[String(prefix) + "End"] = range.end;
    obj[String(prefix) + "Count"] = edgeSpanLength(range.start, range.end);
}

void ConfigManager::toJson(JsonObject& obj) const {
    obj["totalLedCount"] = _config.totalLedCount;
    edgeToJson(obj, "left", _config.left);
    edgeToJson(obj, "right", _config.right);
    edgeToJson(obj, "top", _config.top);
    edgeToJson(obj, "bottom", _config.bottom);
    obj["layoutTotal"] = layoutTotal();
    obj["layoutExceedsStrip"] = layoutExceedsStrip();
    obj["brightness"] = _config.brightness;
    obj["colorOrder"] = colorOrderToString(_config.colorOrder);
    obj["gammaCorrection"] = _config.gammaCorrection;
    obj["maxFps"] = _config.maxFps;
    obj["reverseLeft"] = _config.reverseLeft;
    obj["reverseTop"] = _config.reverseTop;
    obj["reverseRight"] = _config.reverseRight;
    obj["reverseBottom"] = _config.reverseBottom;
    obj["wifiConfigured"] = hasWifiCredentials();
    if (hasWifiCredentials()) {
        obj["wifiSsid"] = _config.wifiSsid;
    }
}

static bool readEdgeFromJson(const JsonObject& obj, const char* prefix, EdgeRange& range) {
    bool changed = false;
    String startKey = String(prefix) + "Start";
    String endKey = String(prefix) + "End";
    if (obj[startKey.c_str()].is<uint16_t>()) {
        range.start = obj[startKey.c_str()];
        changed = true;
    }
    if (obj[endKey.c_str()].is<uint16_t>()) {
        range.end = obj[endKey.c_str()];
        changed = true;
    }
    return changed;
}

bool ConfigManager::applyFromJson(const JsonObject& obj, String& errorOut) {
    bool layoutChanged = false;

    if (obj["totalLedCount"].is<uint16_t>()) {
        uint16_t v = obj["totalLedCount"];
        if (v == 0 || v > MAX_LEDS) {
            errorOut = "totalLedCount out of range";
            return false;
        }
        _config.totalLedCount = v;
    }

    DeviceConfig candidate = _config;
    layoutChanged |= readEdgeFromJson(obj, "left", candidate.left);
    layoutChanged |= readEdgeFromJson(obj, "right", candidate.right);
    layoutChanged |= readEdgeFromJson(obj, "top", candidate.top);
    layoutChanged |= readEdgeFromJson(obj, "bottom", candidate.bottom);

    if (layoutChanged) {
        if (!validateEdgeRange(candidate.left, errorOut)) return false;
        if (!validateEdgeRange(candidate.right, errorOut)) return false;
        if (!validateEdgeRange(candidate.top, errorOut)) return false;
        if (!validateEdgeRange(candidate.bottom, errorOut)) return false;
        _config.left = candidate.left;
        _config.right = candidate.right;
        _config.top = candidate.top;
        _config.bottom = candidate.bottom;
    }

    if (obj["brightness"].is<uint8_t>()) _config.brightness = obj["brightness"];
    if (obj["gammaCorrection"].is<bool>()) _config.gammaCorrection = obj["gammaCorrection"];
    if (obj["maxFps"].is<uint8_t>()) _config.maxFps = obj["maxFps"];
    if (obj["reverseLeft"].is<bool>()) _config.reverseLeft = obj["reverseLeft"];
    if (obj["reverseTop"].is<bool>()) _config.reverseTop = obj["reverseTop"];
    if (obj["reverseRight"].is<bool>()) _config.reverseRight = obj["reverseRight"];
    if (obj["reverseBottom"].is<bool>()) _config.reverseBottom = obj["reverseBottom"];
    if (obj["colorOrder"].is<const char*>()) {
        _config.colorOrder = parseColorOrder(obj["colorOrder"]);
    }

    if (obj["wifiSsid"].is<const char*>()) {
        strlcpy(_config.wifiSsid, obj["wifiSsid"], sizeof(_config.wifiSsid));
    }
    if (obj["wifiPassword"].is<const char*>()) {
        strlcpy(_config.wifiPassword, obj["wifiPassword"], sizeof(_config.wifiPassword));
    }

    save();
    return layoutChanged;
}

String ConfigManager::exportJson() const {
    JsonDocument doc;
    JsonObject obj = doc.to<JsonObject>();
    toJson(obj);
    obj["wifiPassword"] = _config.wifiPassword;
    String out;
    serializeJson(doc, out);
    return out;
}

bool ConfigManager::importJson(const char* json, String& errorOut) {
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        errorOut = err.c_str();
        return false;
    }
    if (!doc.is<JsonObject>()) {
        errorOut = "Expected JSON object";
        return false;
    }
    applyFromJson(doc.as<JsonObject>(), errorOut);
    return errorOut.isEmpty();
}
