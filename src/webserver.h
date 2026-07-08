#pragma once

#include <Arduino.h>
#include <ESPAsyncWebServer.h>
#include <WiFi.h>
#include <DNSServer.h>
#include <ESPmDNS.h>

class WebServerManager {
public:
    static WebServerManager& instance();

    bool begin();
    void loop();

    bool isApMode() const { return _apMode; }
    int32_t rssi() const;
    const char* ipAddress() const;
    uint32_t uptimeSeconds() const;

    void requestReboot(uint32_t delayMs = 1000);

private:
    WebServerManager() = default;

    bool connectWifi();
    bool startAccessPoint();
    void setupRoutes();
    void setupCaptivePortal();

    void handleGetConfig(AsyncWebServerRequest* request);
    void handlePostConfig(AsyncWebServerRequest* request, uint8_t* data, size_t len);
    void handleGetStatus(AsyncWebServerRequest* request);
    void handlePostReboot(AsyncWebServerRequest* request);
    void handlePostTestPattern(AsyncWebServerRequest* request, uint8_t* data, size_t len);
    void handleExportConfig(AsyncWebServerRequest* request);
    void handleImportConfig(AsyncWebServerRequest* request, uint8_t* data, size_t len);

    AsyncWebServer _server{80};
    DNSServer _dns;
    bool _apMode = false;
    bool _fsOk = false;
    bool _rebootPending = false;
    uint32_t _rebootAt = 0;
    char _ipStr[16] = "0.0.0.0";
};
