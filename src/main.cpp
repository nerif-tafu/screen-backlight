#include <Arduino.h>
#include "config.h"
#include "led_controller.h"
#include "webserver.h"

#ifndef FIRMWARE_VERSION
#define FIRMWARE_VERSION "1.0.0"
#endif

#ifndef LED_DATA_PIN
#define LED_DATA_PIN 4  // XIAO ESP32-C3 D2
#endif

#ifndef HOSTNAME
#define HOSTNAME "backlight"
#endif

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.printf("\n\nAmbient Backlight Controller v%s\n", FIRMWARE_VERSION);
    Serial.printf("LED data pin: GPIO %d\n", LED_DATA_PIN);

    ConfigManager::instance().begin();
    ConfigManager::instance().load();

    LedController::instance().begin();
    // Skip boot animation when Wi-Fi must come up quickly
    // LedController::instance().playBootAnimation();

    if (!WebServerManager::instance().begin()) {
        Serial.println("[ERROR] Web server failed to start");
    }

    Serial.println("[READY]");
}

void loop() {
    WebServerManager::instance().loop();
    delay(1);
}
