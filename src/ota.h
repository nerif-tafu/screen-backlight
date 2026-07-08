#pragma once

#include <Arduino.h>
#include <ESPAsyncWebServer.h>

class OtaManager {
public:
    static OtaManager& instance();

    void begin();
    void loop();
    void registerRoutes(AsyncWebServer* server);

    bool active() const { return _active; }
    uint8_t progress() const { return _progress; }
    const char* lastError() const { return _lastError; }

private:
    OtaManager() = default;

    void setupArduinoOta();
    void handleOtaStatus(AsyncWebServerRequest* request);

    bool _active = false;
    uint8_t _progress = 0;
    char _lastError[64]{};
};
