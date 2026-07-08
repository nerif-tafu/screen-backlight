#include "webserver.h"
#include "config.h"
#include "led_controller.h"
#include "websocket.h"
#include "ota.h"
#include <ArduinoJson.h>
#include <LittleFS.h>
#include <esp_partition.h>

WebServerManager& WebServerManager::instance() {
    static WebServerManager inst;
    return inst;
}

const char* WebServerManager::ipAddress() const {
    return _ipStr;
}

int32_t WebServerManager::rssi() const {
    if (_apMode) return 0;
    return WiFi.RSSI();
}

uint32_t WebServerManager::uptimeSeconds() const {
    return millis() / 1000;
}

void WebServerManager::requestReboot(uint32_t delayMs) {
    _rebootPending = true;
    _rebootAt = millis() + delayMs;
}

bool WebServerManager::connectWifi() {
    const auto& cfg = ConfigManager::instance().config();
    if (!ConfigManager::instance().hasWifiCredentials()) return false;

    WiFi.mode(WIFI_STA);
    WiFi.setHostname(HOSTNAME);
    WiFi.begin(cfg.wifiSsid, cfg.wifiPassword);

    Serial.printf("[WiFi] Connecting to %s", cfg.wifiSsid);
    uint32_t start = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - start < 15000) {
        delay(250);
        Serial.print('.');
    }
    Serial.println();

    if (WiFi.status() != WL_CONNECTED) {
        Serial.println("[WiFi] Connection failed");
        return false;
    }

    _apMode = false;
    strlcpy(_ipStr, WiFi.localIP().toString().c_str(), sizeof(_ipStr));
    Serial.printf("[WiFi] Connected: %s RSSI=%d\n", _ipStr, WiFi.RSSI());

    if (MDNS.begin(HOSTNAME)) {
        MDNS.addService("http", "tcp", 80);
        MDNS.addService("ws", "tcp", 80);
        Serial.printf("[mDNS] http://%s.local\n", HOSTNAME);
    }
    return true;
}

bool WebServerManager::startAccessPoint() {
    WiFi.mode(WIFI_AP);
    WiFi.softAP(AP_SSID, AP_PASSWORD[0] ? AP_PASSWORD : nullptr);
    _apMode = true;
    strlcpy(_ipStr, WiFi.softAPIP().toString().c_str(), sizeof(_ipStr));
    Serial.printf("[WiFi] AP mode: %s (%s)\n", AP_SSID, _ipStr);

    _dns.start(53, "*", WiFi.softAPIP());
    return true;
}

void WebServerManager::setupCaptivePortal() {
    _server.onNotFound([this](AsyncWebServerRequest* request) {
        if (_apMode) {
            request->redirect(String("http://") + _ipStr);
        } else {
            request->send(404, "text/plain", "Not found");
        }
    });
}

void WebServerManager::handleGetConfig(AsyncWebServerRequest* request) {
    JsonDocument doc;
    JsonObject obj = doc.to<JsonObject>();
    ConfigManager::instance().toJson(obj);
    doc["firmwareVersion"] = FIRMWARE_VERSION;
    String body;
    serializeJson(doc, body);
    request->send(200, "application/json", body);
}

void WebServerManager::handlePostConfig(AsyncWebServerRequest* request, uint8_t* data, size_t len) {
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, data, len);
    if (err) {
        request->send(400, "application/json", "{\"error\":\"Invalid JSON\"}");
        return;
    }

    String error;
    bool layoutChanged = ConfigManager::instance().applyFromJson(doc.as<JsonObject>(), error);
    if (!error.isEmpty()) {
        request->send(400, "application/json", String("{\"error\":\"") + error + "\"}");
        return;
    }

    LedController::instance().applyConfig();
    if (layoutChanged) {
        // no-op; layout used by preview only on client
    }

    JsonDocument resp;
    JsonObject respObj = resp.to<JsonObject>();
    ConfigManager::instance().toJson(respObj);
    resp["ok"] = true;
    String body;
    serializeJson(resp, body);
    request->send(200, "application/json", body);
}

void WebServerManager::handleGetStatus(AsyncWebServerRequest* request) {
    auto& ws = WebSocketHandler::instance();
    JsonDocument doc;
    JsonObject obj = doc.to<JsonObject>();
    obj["ip"] = _ipStr;
    obj["hostname"] = HOSTNAME;
    obj["rssi"] = rssi();
    obj["freeHeap"] = ESP.getFreeHeap();
    obj["uptime"] = uptimeSeconds();
    obj["wsFps"] = ws.wsFps();
    obj["wsClients"] = ws.clientCount();
    obj["wsFrames"] = ws.framesReceived();
    obj["lastFrame"] = ws.lastFrameNumber();
    obj["firmwareVersion"] = FIRMWARE_VERSION;
    obj["apMode"] = _apMode;
    obj["testPattern"] = testPatternName(LedController::instance().activeTestPattern());
    obj["activeLeds"] = ConfigManager::instance().config().totalLedCount;
    obj["layoutTotal"] = ConfigManager::instance().layoutTotal();
    obj["otaActive"] = OtaManager::instance().active();
    obj["otaProgress"] = OtaManager::instance().progress();

    String body;
    serializeJson(doc, body);
    request->send(200, "application/json", body);
}

void WebServerManager::handlePostReboot(AsyncWebServerRequest* request) {
    request->send(200, "application/json", "{\"ok\":true,\"rebooting\":true}");
    requestReboot(1000);
}

void WebServerManager::handlePostTestPattern(AsyncWebServerRequest* request, uint8_t* data, size_t len) {
    JsonDocument doc;
    if (deserializeJson(doc, data, len)) {
        request->send(400, "application/json", "{\"error\":\"Invalid JSON\"}");
        return;
    }

    const char* pattern = doc["pattern"];
    if (!pattern) {
        request->send(400, "application/json", "{\"error\":\"Missing pattern\"}");
        return;
    }

    TestPattern p = testPatternFromName(pattern);
    if (p == TestPattern::None) {
        request->send(400, "application/json", "{\"error\":\"Unknown pattern\"}");
        return;
    }

    LedController::instance().setTestPattern(p);
    request->send(200, "application/json", String("{\"ok\":true,\"pattern\":\"") + pattern + "\"}");
}

void WebServerManager::handleExportConfig(AsyncWebServerRequest* request) {
    String json = ConfigManager::instance().exportJson();
    request->send(200, "application/json", json);
}

void WebServerManager::handleImportConfig(AsyncWebServerRequest* request, uint8_t* data, size_t len) {
    String error;
    if (!ConfigManager::instance().importJson((const char*)data, error)) {
        request->send(400, "application/json", String("{\"error\":\"") + error + "\"}");
        return;
    }
    LedController::instance().applyConfig();
    request->send(200, "application/json", "{\"ok\":true}");
}

void WebServerManager::setupRoutes() {
    _server.on("/", HTTP_GET, [](AsyncWebServerRequest* r) {
        if (LittleFS.exists("/index.html")) r->send(LittleFS, "/index.html", "text/html");
        else r->send(503, "text/plain", "Web UI not uploaded — run uploadfs");
    });
    _server.on("/index.html", HTTP_GET, [](AsyncWebServerRequest* r) {
        if (LittleFS.exists("/index.html")) r->send(LittleFS, "/index.html", "text/html");
        else r->send(404, "text/plain", "Not found");
    });
    _server.on("/app.js", HTTP_GET, [](AsyncWebServerRequest* r) {
        if (LittleFS.exists("/app.js")) r->send(LittleFS, "/app.js", "application/javascript");
        else r->send(404, "text/plain", "Not found");
    });
    _server.on("/style.css", HTTP_GET, [](AsyncWebServerRequest* r) {
        if (LittleFS.exists("/style.css")) r->send(LittleFS, "/style.css", "text/css");
        else r->send(404, "text/plain", "Not found");
    });

    _server.on("/api/fs", HTTP_GET, [this](AsyncWebServerRequest* r) {
        JsonDocument doc;
        doc["mounted"] = _fsOk;
        doc["indexHtml"] = LittleFS.exists("/index.html");
        JsonArray parts = doc["partitions"].to<JsonArray>();
        esp_partition_iterator_t it = esp_partition_find(ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_ANY, NULL);
        while (it) {
            const esp_partition_t* p = esp_partition_get(it);
            JsonObject o = parts.add<JsonObject>();
            o["label"] = p->label;
            o["subtype"] = p->subtype;
            o["offset"] = p->address;
            o["size"] = p->size;
            it = esp_partition_next(it);
        }
        esp_partition_iterator_release(it);
        JsonArray files = doc["files"].to<JsonArray>();
        if (_fsOk) {
            File root = LittleFS.open("/");
            if (root) {
                for (File f = root.openNextFile(); f; f = root.openNextFile()) {
                    files.add(f.name());
                }
            }
        }
        String body;
        serializeJson(doc, body);
        r->send(200, "application/json", body);
    });

    _server.on("/api/config", HTTP_GET, [this](AsyncWebServerRequest* r) { handleGetConfig(r); });
    _server.on("/api/config", HTTP_POST, [](AsyncWebServerRequest* r) {},
               NULL,
               [this](AsyncWebServerRequest* r, uint8_t* d, size_t l, size_t, size_t) {
                   handlePostConfig(r, d, l);
               });

    _server.on("/api/status", HTTP_GET, [this](AsyncWebServerRequest* r) { handleGetStatus(r); });
    _server.on("/api/reboot", HTTP_POST, [this](AsyncWebServerRequest* r) { handlePostReboot(r); });

    _server.on("/api/testpattern", HTTP_POST, [](AsyncWebServerRequest* r) {},
               NULL,
               [this](AsyncWebServerRequest* r, uint8_t* d, size_t l, size_t, size_t) {
                   handlePostTestPattern(r, d, l);
               });

    _server.on("/api/config/export", HTTP_GET, [this](AsyncWebServerRequest* r) { handleExportConfig(r); });
    _server.on("/api/config/import", HTTP_POST, [](AsyncWebServerRequest* r) {},
               NULL,
               [this](AsyncWebServerRequest* r, uint8_t* d, size_t l, size_t, size_t) {
                   handleImportConfig(r, d, l);
               });

    // Captive portal detection endpoints
    _server.on("/generate_204", HTTP_GET, [this](AsyncWebServerRequest* r) { r->redirect("/"); });
    _server.on("/hotspot-detect.html", HTTP_GET, [this](AsyncWebServerRequest* r) { r->redirect("/"); });
    _server.on("/connecttest.txt", HTTP_GET, [this](AsyncWebServerRequest* r) { r->redirect("/"); });
    _server.on("/fwlink", HTTP_GET, [this](AsyncWebServerRequest* r) { r->redirect("/"); });

    setupCaptivePortal();
}

bool WebServerManager::begin() {
    _fsOk = LittleFS.begin(false);
    if (_fsOk) {
        bool hasIndex = LittleFS.exists("/index.html");
        Serial.printf("[FS] Mounted index.html=%s\n", hasIndex ? "yes" : "no");
        if (!hasIndex) _fsOk = false;
    } else {
        Serial.println("[FS] Mount failed (label=spiffs)");
    }

    if (!connectWifi()) {
        startAccessPoint();
    }

    setupRoutes();
    WebSocketHandler::instance().begin(&_server);
    OtaManager::instance().registerRoutes(&_server);
    _server.begin();
    OtaManager::instance().begin();
    Serial.printf("[HTTP] Server started (%s) http://%s\n", _apMode ? "AP" : "STA", _ipStr);
    return true;
}

void WebServerManager::loop() {
    if (_apMode) _dns.processNextRequest();
    WebSocketHandler::instance().loop();
    OtaManager::instance().loop();

    if (_rebootPending && millis() >= _rebootAt) {
        ESP.restart();
    }

    LedController::instance().updateTestPattern();
    LedController::instance().updateIdleSleep();
}
