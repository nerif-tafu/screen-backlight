#include "led_controller.h"
#include <math.h>

namespace {
CRGB applyPixelGamma(CRGB c, bool enabled) {
    if (!enabled) return c;
    return CRGB(dim8_video(c.r), dim8_video(c.g), dim8_video(c.b));
}
}  // namespace

LedController& LedController::instance() {
    static LedController inst;
    return inst;
}

EOrder LedController::configToEOrder(ColorOrder order) const {
    switch (order) {
        case ColorOrder::RGB: return RGB;
        case ColorOrder::GRB: return GRB;
        case ColorOrder::RBG: return RBG;
        case ColorOrder::BRG: return BRG;
        case ColorOrder::BGR: return BGR;
        case ColorOrder::GBR: return GBR;
        default: return GRB;
    }
}

bool LedController::begin() {
    applyConfig();
    _initialized = true;
    return true;
}

void LedController::reinitStrip() {
    const auto& cfg = ConfigManager::instance().config();
    _activeCount = cfg.totalLedCount;
    if (_activeCount > MAX_LEDS) _activeCount = MAX_LEDS;
    if (_activeCount == 0) _activeCount = 1;

    FastLED.clear(true);

    EOrder order = configToEOrder(cfg.colorOrder);
    switch (order) {
        case RGB:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, RGB>(_leds, _activeCount);
            break;
        case RBG:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, RBG>(_leds, _activeCount);
            break;
        case BRG:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, BRG>(_leds, _activeCount);
            break;
        case BGR:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, BGR>(_leds, _activeCount);
            break;
        case GBR:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, GBR>(_leds, _activeCount);
            break;
        case GRB:
        default:
            FastLED.addLeds<WS2812B, LED_DATA_PIN, GRB>(_leds, _activeCount);
            break;
    }

    FastLED.setBrightness(_brightness);
    clear();
    show();
}

void LedController::applyConfig() {
    const auto& cfg = ConfigManager::instance().config();
    _brightness = cfg.brightness;
    _gammaEnabled = cfg.gammaCorrection;
    reinitStrip();
    FastLED.setBrightness(_brightness);
}

void LedController::setBrightness(uint8_t brightness) {
    _brightness = brightness;
    FastLED.setBrightness(_brightness);
}

void LedController::show() {
    FastLED.setBrightness(_brightness);
    FastLED.show();
}

void LedController::clear() {
    fill_solid(_leds, _activeCount, CRGB::Black);
}

void LedController::fillSolid(CRGB color) {
    fill_solid(_leds, _activeCount, color);
}

void LedController::setFromRgb(const uint8_t* rgb, uint16_t count) {
    if (!_initialized || !rgb) return;
    _testPattern = TestPattern::None;

    if (count > _activeCount) count = _activeCount;

    for (uint16_t i = 0; i < count; i++) {
        _leds[i] = CRGB(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
        if (_gammaEnabled) {
            _leds[i] = applyPixelGamma(_leds[i], true);
        }
    }
    for (uint16_t i = count; i < _activeCount; i++) {
        _leds[i] = CRGB::Black;
    }

    show();
}

uint16_t LedController::edgeLength(uint8_t edgeIndex) const {
    const EdgeRange& edge = ConfigManager::edgeByIndex(ConfigManager::instance().config(), edgeIndex);
    return edgeSpanLength(edge.start, edge.end);
}

uint16_t LedController::edgeStartIndex(uint8_t edgeIndex) const {
    const auto& cfg = ConfigManager::instance().config();
    uint16_t streamOffset = 0;
    for (uint8_t i = 0; i < edgeIndex; i++) {
        const EdgeRange& edge = ConfigManager::edgeByIndex(cfg, i);
        streamOffset = static_cast<uint16_t>(streamOffset + edgeSpanLength(edge.start, edge.end));
    }
    return streamOffset;
}

void LedController::fillEdge(uint8_t edgeIndex, CRGB color) {
    const auto& cfg = ConfigManager::instance().config();
    const EdgeRange& edge = ConfigManager::edgeByIndex(cfg, edgeIndex);
    bool reverse = ConfigManager::reverseByIndex(cfg, edgeIndex);

    int step = (edge.start <= edge.end) ? 1 : -1;
    if (reverse) step = -step;

    uint16_t idx = edge.start;
    while (true) {
        if (idx < _activeCount) _leds[idx] = color;
        if (idx == edge.end) break;
        idx = static_cast<uint16_t>(idx + step);
    }
}

void LedController::setTestPattern(TestPattern pattern) {
    _testPattern = pattern;
    _patternTick = millis();
    _patternPhase = 0;

    switch (pattern) {
        case TestPattern::Off:
            clear();
            show();
            _testPattern = TestPattern::None;
            break;
        case TestPattern::SolidWhite: fillSolid(CRGB::White); show(); break;
        case TestPattern::SolidRed: fillSolid(CRGB::Red); show(); break;
        case TestPattern::SolidGreen: fillSolid(CRGB::Green); show(); break;
        case TestPattern::SolidBlue: fillSolid(CRGB::Blue); show(); break;
        case TestPattern::EdgeIdentification:
            clear();
            fillEdge(0, CRGB::Red);
            fillEdge(1, CRGB::Green);
            fillEdge(2, CRGB::Blue);
            fillEdge(3, CRGB::White);
            show();
            break;
        default:
            break;
    }
}

void LedController::updateTestPattern() {
    if (_testPattern == TestPattern::None || _testPattern == TestPattern::Off) return;

    uint32_t now = millis();
    if (now - _patternTick < 30) return;  // ~33 FPS cap for patterns
    _patternTick = now;
    _patternPhase++;

    switch (_testPattern) {
        case TestPattern::Rainbow:
            fill_rainbow(_leds, _activeCount, _patternPhase, 7);
            break;
        case TestPattern::ColorWipe: {
            clear();
            uint16_t idx = _patternPhase % (_activeCount + 1);
            if (idx < _activeCount) _leds[idx] = CRGB::White;
            break;
        }
        case TestPattern::TheaterChase: {
            fill_solid(_leds, _activeCount, CRGB::Black);
            for (uint16_t i = 0; i < _activeCount; i++) {
                if ((i + _patternPhase) % 3 == 0) _leds[i] = CRGB::White;
            }
            break;
        }
        case TestPattern::BreathingWhite: {
            uint8_t b = (exp(sin(millis() / 2000.0 * PI)) * 127.0) + 128;
            CRGB c = applyPixelGamma(CRGB(b, b, b), _gammaEnabled);
            fill_solid(_leds, _activeCount, c);
            break;
        }
        case TestPattern::RotatingHue: {
            for (uint16_t i = 0; i < _activeCount; i++) {
                _leds[i] = CHSV((i * 2 + _patternPhase) % 256, 255, 255);
            }
            break;
        }
        default:
            return;
    }
    FastLED.setBrightness(_brightness);
    FastLED.show();
}

void LedController::playBootAnimation() {
    const uint16_t n = _activeCount;
    for (uint16_t i = 0; i < n; i++) {
        _leds[i] = CRGB(0, 0, 80);
        FastLED.show();
        delay(5);
    }
    for (uint16_t i = 0; i < n; i++) {
        _leds[i] = CRGB::Black;
        FastLED.show();
        delay(3);
    }
}

const char* testPatternName(TestPattern p) {
    switch (p) {
        case TestPattern::Off: return "off";
        case TestPattern::SolidWhite: return "solid_white";
        case TestPattern::SolidRed: return "solid_red";
        case TestPattern::SolidGreen: return "solid_green";
        case TestPattern::SolidBlue: return "solid_blue";
        case TestPattern::Rainbow: return "rainbow";
        case TestPattern::ColorWipe: return "color_wipe";
        case TestPattern::TheaterChase: return "theater_chase";
        case TestPattern::BreathingWhite: return "breathing_white";
        case TestPattern::RotatingHue: return "rotating_hue";
        case TestPattern::EdgeIdentification: return "edge_identification";
        default: return "none";
    }
}

TestPattern testPatternFromName(const char* name) {
    if (!name) return TestPattern::None;
    if (strcmp(name, "off") == 0) return TestPattern::Off;
    if (strcmp(name, "solid_white") == 0) return TestPattern::SolidWhite;
    if (strcmp(name, "solid_red") == 0) return TestPattern::SolidRed;
    if (strcmp(name, "solid_green") == 0) return TestPattern::SolidGreen;
    if (strcmp(name, "solid_blue") == 0) return TestPattern::SolidBlue;
    if (strcmp(name, "rainbow") == 0) return TestPattern::Rainbow;
    if (strcmp(name, "color_wipe") == 0) return TestPattern::ColorWipe;
    if (strcmp(name, "theater_chase") == 0) return TestPattern::TheaterChase;
    if (strcmp(name, "breathing_white") == 0) return TestPattern::BreathingWhite;
    if (strcmp(name, "rotating_hue") == 0) return TestPattern::RotatingHue;
    if (strcmp(name, "edge_identification") == 0) return TestPattern::EdgeIdentification;
    return TestPattern::None;
}
