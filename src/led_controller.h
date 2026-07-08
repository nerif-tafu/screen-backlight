#pragma once

#include "config.h"
#include <FastLED.h>

enum class TestPattern : uint8_t {
    None = 0,
    Off,
    SolidWhite,
    SolidRed,
    SolidGreen,
    SolidBlue,
    Rainbow,
    ColorWipe,
    TheaterChase,
    BreathingWhite,
    RotatingHue,
    EdgeIdentification,
};

class LedController {
public:
    static LedController& instance();

    bool begin();
    void applyConfig();

    // Direct buffer access for WebSocket streaming
    CRGB* buffer() { return _leds; }
    uint16_t activeLedCount() const { return _activeCount; }

    void setBrightness(uint8_t brightness);
    void show();

    // Copy RGB data from external buffer (no allocation)
    void setFromRgb(const uint8_t* rgb, uint16_t count);

    void clear();
    void fillSolid(CRGB color);

    // Test patterns
    void setTestPattern(TestPattern pattern);
    TestPattern activeTestPattern() const { return _testPattern; }
    void updateTestPattern();  // Call from loop when pattern active

    void stopTestPattern() { _testPattern = TestPattern::None; }

    // Edge layout helpers for preview / identification
    void fillEdge(uint8_t edgeIndex, CRGB color);  // 0=left,1=top,2=right,3=bottom
    uint16_t edgeStartIndex(uint8_t edgeIndex) const;
    uint16_t edgeLength(uint8_t edgeIndex) const;

    // Boot animation
    void playBootAnimation();

private:
    LedController() = default;

    EOrder configToEOrder(ColorOrder order) const;
    void reinitStrip();

    CRGB _leds[MAX_LEDS];
    uint16_t _activeCount = 0;
    uint8_t _brightness = 128;
    bool _gammaEnabled = true;
    TestPattern _testPattern = TestPattern::None;
    uint32_t _patternTick = 0;
    uint16_t _patternPhase = 0;
    bool _initialized = false;
};

const char* testPatternName(TestPattern p);
TestPattern testPatternFromName(const char* name);
