#pragma once

#include <Arduino.h>
#include <ESPAsyncWebServer.h>

class WebSocketHandler {
public:
    static WebSocketHandler& instance();

    void begin(AsyncWebServer* server);
    void loop();

    // Stats exposed to status API
    float wsFps() const { return _fps; }
    uint8_t clientCount() const { return _clientCount; }
    uint32_t framesReceived() const { return _frameCount; }
    uint16_t lastFrameNumber() const { return _lastFrameNumber; }

private:
    WebSocketHandler() = default;

    void onEvent(AsyncWebSocket* server, AsyncWebSocketClient* client,
                 AwsEventType type, void* arg, uint8_t* data, size_t len);
    void handleBinaryFrame(AsyncWebSocketClient* client, uint8_t* data, size_t len);
    void handleJsonCommand(AsyncWebSocketClient* client, const char* json, size_t len);
    bool shouldAcceptFrame();

    AsyncWebSocket _ws{"/ws"};
    float _fps = 0.0f;
    uint8_t _clientCount = 0;
    uint32_t _frameCount = 0;
    uint16_t _lastFrameNumber = 0;
    uint32_t _fpsWindowStart = 0;
    uint16_t _fpsWindowFrames = 0;
    uint32_t _lastAcceptedFrameMs = 0;

    // Fixed receive buffer for binary frames (max payload: 4 + 3*MAX_LEDS)
    static constexpr size_t RX_BUFFER_SIZE = 4 + 3 * 512;
    uint8_t _rxBuffer[RX_BUFFER_SIZE];
};
