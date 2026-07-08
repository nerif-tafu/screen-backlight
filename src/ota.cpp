#include "ota.h"
#include "led_controller.h"
#include <Update.h>
#include <ArduinoOTA.h>
#include <WiFi.h>
#include <ArduinoJson.h>

#ifndef HOSTNAME
#define HOSTNAME "backlight"
#endif

#ifndef FIRMWARE_VERSION
#define FIRMWARE_VERSION "1.0.0"
#endif

OtaManager& OtaManager::instance() {
    static OtaManager inst;
    return inst;
}

void OtaManager::setupArduinoOta() {
    if (WiFi.getMode() == WIFI_AP) return;

    ArduinoOTA.setHostname(HOSTNAME);
    ArduinoOTA.setRebootOnSuccess(true);

    ArduinoOTA.onStart([this]() {
        _active = true;
        _progress = 0;
        _lastError[0] = '\0';
        LedController::instance().setTestPattern(TestPattern::Off);
    });

    ArduinoOTA.onProgress([this](unsigned int progress, unsigned int total) {
        _progress = total ? static_cast<uint8_t>((progress * 100) / total) : 0;
    });

    ArduinoOTA.onError([this](ota_error_t error) {
        snprintf(_lastError, sizeof(_lastError), "OTA error %u", static_cast<unsigned>(error));
        _active = false;
    });

    ArduinoOTA.onEnd([this]() {
        _progress = 100;
        _active = false;
    });

    ArduinoOTA.begin();
    Serial.println("[OTA] ArduinoOTA ready");
}

void OtaManager::begin() {
    setupArduinoOta();
}

void OtaManager::loop() {
    if (WiFi.getMode() != WIFI_AP) {
        ArduinoOTA.handle();
    }
}

void OtaManager::handleOtaStatus(AsyncWebServerRequest* request) {
    JsonDocument doc;
    JsonObject obj = doc.to<JsonObject>();
    obj["active"] = _active;
    obj["progress"] = _progress;
    obj["error"] = _lastError;
    String body;
    serializeJson(doc, body);
    request->send(200, "application/json", body);
}

void OtaManager::registerRoutes(AsyncWebServer* server) {
    server->on("/api/ota/status", HTTP_GET, [this](AsyncWebServerRequest* request) {
        handleOtaStatus(request);
    });

    server->on("/api/ota/update", HTTP_POST,
               [this](AsyncWebServerRequest* request) {
                   if (Update.hasError()) {
                       request->send(500, "application/json", "{\"error\":\"Update failed\"}");
                       _active = false;
                       return;
                   }
                   request->send(200, "application/json", "{\"ok\":true,\"rebooting\":true}");
                   request->onDisconnect([]() { ESP.restart(); });
               },
               [this](AsyncWebServerRequest* request, String filename, size_t index, uint8_t* data, size_t len, bool final) {
                   (void)request;
                   if (index == 0) {
                       _active = true;
                       _progress = 0;
                       _lastError[0] = '\0';
                       LedController::instance().setTestPattern(TestPattern::Off);

                       if (!Update.begin(UPDATE_SIZE_UNKNOWN, U_FLASH)) {
                           Update.printError(Serial);
                           snprintf(_lastError, sizeof(_lastError), "Update.begin failed (%u)", Update.getError());
                           _active = false;
                           return;
                       }
                       Serial.printf("[OTA] Web upload started: %s\n", filename.c_str());
                   }

                   if (Update.write(data, len) != len) {
                       Update.printError(Serial);
                       snprintf(_lastError, sizeof(_lastError), "Write failed (%u)", Update.getError());
                       Update.abort();
                       _active = false;
                       return;
                   }

                   if (Update.size() > 0) {
                       _progress = static_cast<uint8_t>((Update.progress() * 100) / Update.size());
                   }

                   if (final) {
                       Serial.printf("[OTA] Web upload complete (%u bytes)\n", static_cast<unsigned>(Update.progress()));
                       if (!Update.end(true)) {
                           Update.printError(Serial);
                           snprintf(_lastError, sizeof(_lastError), "Update.end failed (%u)", Update.getError());
                           _active = false;
                       } else {
                           _progress = 100;
                       }
                   }
               });
}
