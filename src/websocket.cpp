#include "websocket.h"
#include "config.h"
#include "led_controller.h"
#include <ArduinoJson.h>

WebSocketHandler& WebSocketHandler::instance() {
    static WebSocketHandler inst;
    return inst;
}

void WebSocketHandler::begin(AsyncWebServer* server) {
    _ws.onEvent([this](AsyncWebSocket* s, AsyncWebSocketClient* c, AwsEventType t,
                       void* arg, uint8_t* data, size_t len) {
        onEvent(s, c, t, arg, data, len);
    });
    server->addHandler(&_ws);
    _fpsWindowStart = millis();
}

void WebSocketHandler::loop() {
    _ws.cleanupClients();

    uint32_t now = millis();
    if (now - _fpsWindowStart >= 1000) {
        _fps = _fpsWindowFrames * 1000.0f / (now - _fpsWindowStart);
        _fpsWindowStart = now;
        _fpsWindowFrames = 0;
    }
}

bool WebSocketHandler::shouldAcceptFrame() {
    const auto& cfg = ConfigManager::instance().config();
    if (cfg.maxFps == 0) return true;

    uint32_t minInterval = 1000 / cfg.maxFps;
    uint32_t now = millis();
    if (now - _lastAcceptedFrameMs < minInterval) return false;
    _lastAcceptedFrameMs = now;
    return true;
}

void WebSocketHandler::handleBinaryFrame(AsyncWebSocketClient* client, uint8_t* data, size_t len) {
    if (len < 4) return;

    uint16_t frameNumber = (uint16_t)data[0] | ((uint16_t)data[1] << 8);
    uint16_t ledCount = (uint16_t)data[2] | ((uint16_t)data[3] << 8);

    size_t expected = 4 + (size_t)ledCount * 3;
    if (len != expected) return;
    if (ledCount > MAX_LEDS) return;
    if (ledCount > ConfigManager::instance().layoutTotal()) return;
    if (!shouldAcceptFrame()) return;

    _lastFrameNumber = frameNumber;
    _frameCount++;
    _fpsWindowFrames++;

    LedController::instance().setFromStreamRgb(data + 4, ledCount);
}

void WebSocketHandler::handleJsonCommand(AsyncWebSocketClient* client, const char* json, size_t len) {
    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, json, len);
    if (err) return;

    const char* cmd = doc["cmd"];
    if (!cmd) return;

    auto& leds = LedController::instance();
    auto& cfgMgr = ConfigManager::instance();

    if (strcmp(cmd, "brightness") == 0 && doc["value"].is<int>()) {
        uint8_t b = doc["value"];
        cfgMgr.config().brightness = b;
        cfgMgr.save();
        leds.setBrightness(b);
        leds.applyConfig();
    } else if (strcmp(cmd, "off") == 0) {
        leds.setTestPattern(TestPattern::Off);
    } else if (strcmp(cmd, "test") == 0 && doc["pattern"].is<const char*>()) {
        TestPattern p = testPatternFromName(doc["pattern"]);
        if (p != TestPattern::None) leds.setTestPattern(p);
    }
}

void WebSocketHandler::onEvent(AsyncWebSocket* server, AsyncWebSocketClient* client,
                               AwsEventType type, void* arg, uint8_t* data, size_t len) {
    switch (type) {
        case WS_EVT_CONNECT:
            _clientCount = server->count();
            break;
        case WS_EVT_DISCONNECT:
            _clientCount = server->count();
            break;
        case WS_EVT_DATA: {
            AwsFrameInfo* info = (AwsFrameInfo*)arg;
            if (info->final && info->index == 0 && info->len == len) {
                if (info->opcode == WS_BINARY) {
                    handleBinaryFrame(client, data, len);
                } else if (info->opcode == WS_TEXT) {
                    handleJsonCommand(client, (const char*)data, len);
                }
            } else if (info->index + len <= RX_BUFFER_SIZE) {
                // Reassemble fragmented messages into fixed buffer
                memcpy(_rxBuffer + info->index, data, len);
                if (info->final && info->index + len == info->len) {
                    if (info->opcode == WS_BINARY) {
                        handleBinaryFrame(client, _rxBuffer, info->len);
                    } else if (info->opcode == WS_TEXT) {
                        handleJsonCommand(client, (const char*)_rxBuffer, info->len);
                    }
                }
            }
            break;
        }
        case WS_EVT_PONG:
        case WS_EVT_ERROR:
            break;
    }
}
