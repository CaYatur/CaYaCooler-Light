// ============================================================================
//  CaYaCooler - ESP32-S2/S3 akilli sogutucu denetleyici  (HID-ONLY surum)
//  - Windows 11 Dynamic Lighting (HID LampArray)  : "CaYaLight"
//  - 2x 4-pin PWM fan (25 kHz) + tachometer ile RPM
//  - PC uygulamasi ile haberlesme: VENDOR HID raporlari (CDC/seri YOK)
//  - Isik modlari: OFF / STATIC / TEMP / RAINBOW / STREAM(muzik) / HOST(Win DL)
//
//  NEDEN HID-only: CDC(seri) eklenince Windows cihazi LampArray olarak taniyamadi.
//  Saf HID olunca Dynamic Lighting calisir, haberlesme de HID uzerinden gider.
//
//  Native USB (S2 ve S3'te ayni sekilde calisir) gerektigi icin ESP32 klasik
//  (orijinal) modeller desteklenmez.
//
//  Arduino-ESP32 core 3.x gerekir (ledcAttach API).
//  Kart ayarlari: USB Mode = "USB-OTG (TinyUSB)", USB CDC On Boot = *DISABLED*.
// ============================================================================

#include "USB.h"
#include "USBHID.h"
#include <Adafruit_NeoPixel.h>
#include <Preferences.h>   // ESP flash (NVS) kalici ayar

// ----------------------------- Pin / sabitler -------------------------------
#define LED_PIN       4
#define LED_COUNT     30

#define FAN1_PWM_PIN  5
#define FAN2_PWM_PIN  6
#define FAN1_TACH_PIN 7
#define FAN2_TACH_PIN 15

// Opsiyonel: fanlarin 12V hattini anahtarlayan N-MOSFET'in gate'i (bkz. README
// "MOSFET ile PC kapaliyken fan kapatma"). ESP32 sadece PC/anakart USB'sinden
// beslendigi icin PC kapaninca bu pin de "olur" -> gate'teki pull-down direnc
// fani donanimsal olarak kapatir; bu satir sadece ESP32 acikken gate'i aktif
// HIGH'da tutar. Kullanmiyorsan pin bos birakilabilir, koda dokunmaya gerek yok.
#define FAN_EN_PIN    16

#define PWM_FREQ      25000   // Intel 4-pin fan standardi
#define PWM_RES       8       // 8-bit: 0..255
#define TACH_PPR      2       // devir basina darbe (4-pin standart)

// Isik modlari (PC uygulamasi ile ayni sirada olmali)
enum LightMode { LM_OFF=0, LM_STATIC=1, LM_TEMP=2, LM_RAINBOW=3, LM_STREAM=4, LM_HOST=5 };

// ----------------------------- HID rapor ID'leri ----------------------------
#define LAMP_ARRAY_ATTR_ID     1
#define LAMP_ATTR_REQUEST_ID   2
#define LAMP_ATTR_RESPONSE_ID  3
#define LAMP_MULTI_UPDATE_ID   4
#define LAMP_RANGE_UPDATE_ID   5
#define LAMP_CONTROL_ID        6
#define LAMP_UPDATE_COMPLETE   0x01
#define MULTI_UPDATE_LAMP_COUNT 8

// Vendor HID (PC <-> cihaz). Dynamic Lighting bu collection'i yok sayar.
#define VENDOR_CMD_ID      0x10   // Feature: PC -> cihaz komut
#define VENDOR_STATUS_ID   0x11   // Input:   cihaz -> PC durum
#define VENDOR_CMD_SIZE    48     // bayt. EP0 SET_REPORT TinyUSB tampon siniri
                                  // (CFG_TUD_HID_EP_BUFSIZE=64) asilamaz: 96 idi -> SetFeature STALL.
                                  // FRAME artik 15 LED'lik parcalar halinde gonderilir.
#define VENDOR_STATUS_SIZE 16     // bayt
// Komut kodlari (VENDOR_CMD payload'un ilk bayti)
#define CMD_TEMP   0x01
#define CMD_MODE   0x02
#define CMD_COLOR  0x03
#define CMD_FAN    0x04
#define CMD_BRIGHT 0x05
#define CMD_FRAME  0x06

Adafruit_NeoPixel strip(LED_COUNT, LED_PIN, NEO_GRB + NEO_KHZ800);
USBHID HID;
Preferences prefs;

// ----------------------------- Durum degiskenleri ---------------------------
static volatile uint8_t  autonomousMode = 1;     // LampArray: 0 = Windows kontrol ediyor
static uint16_t          requestedLampId = 0;
static uint8_t           lampColors[LED_COUNT][3];   // Windows DL'den gelen renkler
static uint8_t           streamBuf[LED_COUNT][3];     // PC STREAM modundan gelen kare
static uint8_t           staticColor[3] = {0, 80, 255};
static uint8_t           ledBrightness   = 80;        // global parlaklik (NVS'de saklanir)

static volatile LightMode currentMode = LM_HOST;  // app yokken Windows DL calissin
static volatile bool      streamDirty  = false;   // PC'den yeni kare geldi
static volatile bool      settingsDirty = false;  // NVS'ye yazilmasi gereken degisiklik var

// Fan / sicaklik
static int      cpuTemp = 0, gpuTemp = 0;         // PC'den gelir
static uint32_t lastTempMs = 0;                   // failsafe icin
static uint8_t  fanMode = 0;                       // 0 = AUTO, 1 = MANUAL
static uint8_t  fanManualDuty = 50;                // %
static uint8_t  fanDutyPct = 30;                   // uygulanan %
static float    smoothTemp = 30.0f;                // EMA

static volatile uint32_t tach1Count = 0, tach2Count = 0;
static uint16_t fan1Rpm = 0, fan2Rpm = 0;

void IRAM_ATTR onTach1() { tach1Count++; }
void IRAM_ATTR onTach2() { tach2Count++; }

// ============================================================================
//  HID LampArray report descriptor (Microsoft dogrulanmis, 292 bayt)
// ============================================================================
static const uint8_t hid_descriptor[] = {
  0x05, 0x59, 0x09, 0x01, 0xA1, 0x01,
  // Report 1: LampArrayAttributes (22)
  0x85, LAMP_ARRAY_ATTR_ID, 0x09, 0x02, 0xA1, 0x02,
    0x09, 0x03, 0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x95, 0x01, 0x75, 0x10, 0xB1, 0x03,
    0x09, 0x04, 0x09, 0x05, 0x09, 0x06, 0x09, 0x07, 0x09, 0x08,
    0x27, 0xFF, 0xFF, 0xFF, 0x7F, 0x95, 0x05, 0x75, 0x20, 0xB1, 0x03,
  0xC0,
  // Report 2: LampAttributesRequest (2)
  0x85, LAMP_ATTR_REQUEST_ID, 0x09, 0x20, 0xA1, 0x02,
    0x09, 0x21, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x95, 0x01, 0x75, 0x10, 0xB1, 0x02,
  0xC0,
  // Report 3: LampAttributesResponse (28)
  0x85, LAMP_ATTR_RESPONSE_ID, 0x09, 0x22, 0xA1, 0x02,
    0x09, 0x21, 0xB1, 0x02,
    0x09, 0x23, 0x09, 0x24, 0x09, 0x25, 0x09, 0x27, 0x09, 0x26,
    0x27, 0xFF, 0xFF, 0xFF, 0x7F, 0x95, 0x05, 0x75, 0x20, 0xB1, 0x02,
    0x09, 0x28, 0x09, 0x29, 0x09, 0x2A, 0x09, 0x2B, 0x09, 0x2C, 0x09, 0x2D,
    0x26, 0xFF, 0x00, 0x95, 0x06, 0x75, 0x08, 0xB1, 0x02,
  0xC0,
  // Report 4: LampMultiUpdate (50)
  0x85, LAMP_MULTI_UPDATE_ID, 0x09, 0x50, 0xA1, 0x02,
    0x09, 0x03, 0x25, 0x08, 0x95, 0x01, 0xB1, 0x02,
    0x09, 0x55, 0x25, 0x01, 0xB1, 0x02,
    0x09, 0x21, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x95, 0x08, 0x75, 0x10, 0xB1, 0x02,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, 0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, 0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, 0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, 0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x26, 0xFF, 0x00, 0x95, 0x20, 0x75, 0x08, 0xB1, 0x02,
  0xC0,
  // Report 5: LampRangeUpdate (9)
  0x85, LAMP_RANGE_UPDATE_ID, 0x09, 0x60, 0xA1, 0x02,
    0x09, 0x55, 0x25, 0x01, 0x95, 0x01, 0xB1, 0x02,
    0x09, 0x61, 0x09, 0x62, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x95, 0x02, 0x75, 0x10, 0xB1, 0x02,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, 0x26, 0xFF, 0x00, 0x95, 0x04, 0x75, 0x08, 0xB1, 0x02,
  0xC0,
  // Report 6: LampArrayControl (1)
  0x85, LAMP_CONTROL_ID, 0x09, 0x70, 0xA1, 0x02,
    0x09, 0x71, 0x25, 0x01, 0x95, 0x01, 0xB1, 0x02,
  0xC0,
  0xC0,   // LampArray application collection kapanir

  // ---- Vendor TLC: PC <-> cihaz haberlesme (DL bunu yok sayar) ----
  0x06, 0x00, 0xFF,                 // Usage Page (Vendor-Defined 0xFF00)
  0x09, 0x01,                       // Usage (0x01)
  0xA1, 0x01,                       // Collection (Application)
    0x15, 0x00, 0x26, 0xFF, 0x00,   // Logical Min 0, Max 255
    0x75, 0x08,                     // Report Size 8
    0x85, VENDOR_CMD_ID,            // Report ID 0x10
    0x09, 0x02, 0x95, VENDOR_CMD_SIZE, 0xB1, 0x02,     // Feature, 96 bayt (PC->cihaz)
    0x85, VENDOR_STATUS_ID,         // Report ID 0x11
    0x09, 0x03, 0x95, VENDOR_STATUS_SIZE, 0x81, 0x02,  // Input, 16 bayt (cihaz->PC)
  0xC0
};

class LampArrayDevice : public USBHIDDevice {
public:
  void begin() { HID.addDevice(this, sizeof(hid_descriptor)); }

  uint16_t _onGetDescriptor(uint8_t* buf) override {
    memcpy(buf, hid_descriptor, sizeof(hid_descriptor));
    return sizeof(hid_descriptor);
  }

  void _onSetFeature(uint8_t id, const uint8_t* buf, uint16_t len) override {
    switch (id) {
      case LAMP_ATTR_REQUEST_ID:
        requestedLampId = buf[0] | ((uint16_t)buf[1] << 8);
        break;
      case LAMP_MULTI_UPDATE_ID: handleMultiUpdate(buf); break;
      case LAMP_RANGE_UPDATE_ID: handleRangeUpdate(buf); break;
      case LAMP_CONTROL_ID:
        autonomousMode = buf[0];
        // Windows kontrolu birakirsa (autonomous=1) son renkleri temizle -> kapali
        if (autonomousMode) memset(lampColors, 0, sizeof(lampColors));
        break;
      case VENDOR_CMD_ID:
        handleVendorCmd(buf);
        break;
    }
  }

  uint16_t _onGetFeature(uint8_t id, uint8_t* buf, uint16_t len) override {
    if (id == LAMP_ARRAY_ATTR_ID)    { sendArrayAttr(buf); return 22; }
    if (id == LAMP_ATTR_RESPONSE_ID) {
      sendLampAttr(buf, requestedLampId);
      if (++requestedLampId >= LED_COUNT) requestedLampId = 0;
      return 28;
    }
    return 0;
  }

private:
  void sendArrayAttr(uint8_t* buf) {
    uint16_t count = LED_COUNT; memcpy(buf,    &count, 2);
    uint32_t w = 300000;        memcpy(buf+2,  &w, 4);
    uint32_t h = 10000;         memcpy(buf+6,  &h, 4);
    uint32_t d = 3000;          memcpy(buf+10, &d, 4);
    uint32_t k = 4;             memcpy(buf+14, &k, 4);   // LampArrayKind = Peripheral
    uint32_t t = 33000;         memcpy(buf+18, &t, 4);
  }
  void sendLampAttr(uint8_t* buf, uint16_t lampId) {
    if (lampId >= LED_COUNT) lampId = 0;
    uint16_t id  = lampId;         memcpy(buf,    &id,  2);
    uint32_t x   = lampId * 10000; memcpy(buf+2,  &x,   4);
    uint32_t y   = 5000;           memcpy(buf+6,  &y,   4);
    uint32_t z   = 0;              memcpy(buf+10, &z,   4);
    uint32_t lat = 33000;          memcpy(buf+14, &lat, 4);
    uint32_t pur = 0x02;           memcpy(buf+18, &pur, 4);
    buf[22]=0xFF; buf[23]=0xFF; buf[24]=0xFF; buf[25]=0x01; buf[26]=0x01; buf[27]=0x00;
  }
  void handleMultiUpdate(const uint8_t* buf) {
    uint8_t count = buf[0];
    if (count > MULTI_UPDATE_LAMP_COUNT) count = MULTI_UPDATE_LAMP_COUNT;
    for (uint8_t i = 0; i < count; i++) {
      uint16_t id = buf[2 + i*2] | ((uint16_t)buf[3 + i*2] << 8);
      if (id < LED_COUNT) {
        const uint8_t* c = &buf[18 + i*4];
        lampColors[id][0]=c[0]; lampColors[id][1]=c[1]; lampColors[id][2]=c[2];
      }
    }
  }
  void handleRangeUpdate(const uint8_t* buf) {
    uint16_t s = buf[1] | ((uint16_t)buf[2] << 8);
    uint16_t e = buf[3] | ((uint16_t)buf[4] << 8);
    for (uint16_t i = s; i <= e && i < LED_COUNT; i++) {
      lampColors[i][0]=buf[5]; lampColors[i][1]=buf[6]; lampColors[i][2]=buf[7];
    }
  }

  // Vendor komut: d[0]=komut kodu, d[1..]=payload
  void handleVendorCmd(const uint8_t* d) {
    switch (d[0]) {
      case CMD_TEMP:  cpuTemp = d[1]; gpuTemp = d[2]; lastTempMs = millis(); break;
      case CMD_MODE:  if (d[1] <= LM_HOST) { currentMode = (LightMode)d[1]; settingsDirty = true; } break;
      case CMD_COLOR: staticColor[0]=d[1]; staticColor[1]=d[2]; staticColor[2]=d[3]; settingsDirty = true; break;
      case CMD_FAN:   fanMode = d[1]; fanManualDuty = constrain(d[2], 0, 100); settingsDirty = true; break;
      case CMD_BRIGHT: ledBrightness = d[1]; strip.setBrightness(ledBrightness); settingsDirty = true; break;
      case CMD_FRAME: {
        // Parcali kare: d[1]=baslangic LED, d[2]=adet, d[3..]=RGB. Son parca gelince render et.
        uint8_t start = d[1];
        uint8_t count = d[2];
        for (uint8_t i = 0; i < count && (start + i) < LED_COUNT; i++) {
          streamBuf[start + i][0] = d[3 + i*3];
          streamBuf[start + i][1] = d[4 + i*3];
          streamBuf[start + i][2] = d[5 + i*3];
        }
        if (start + count >= LED_COUNT) streamDirty = true;
        break;
      }
    }
  }
};
LampArrayDevice lampArray;

// ============================================================================
//  Isik render
// ============================================================================
void renderRainbow() {
  static uint16_t hue = 0;
  for (int i = 0; i < LED_COUNT; i++)
    strip.setPixelColor(i, strip.gamma32(strip.ColorHSV(hue + i * 65536L / LED_COUNT)));
  hue += 300;
}

void renderTemp() {
  // Termometre: sicakliga gore kac LED yanar + mavi(serin)->kirmizi(sicak)
  int t = max(cpuTemp, gpuTemp);
  int lit = map(constrain(t, 30, 85), 30, 85, 1, LED_COUNT);
  for (int i = 0; i < LED_COUNT; i++) {
    if (i < lit) {
      uint16_t hue = map(i, 0, LED_COUNT - 1, 43690, 0); // 240deg->0deg
      strip.setPixelColor(i, strip.gamma32(strip.ColorHSV(hue, 255, 255)));
    } else {
      strip.setPixelColor(i, 0);
    }
  }
}

void renderFromBuffer(uint8_t buf[][3]) {
  for (int i = 0; i < LED_COUNT; i++)
    strip.setPixelColor(i, strip.Color(buf[i][0], buf[i][1], buf[i][2]));
}

void renderLights() {
  switch (currentMode) {
    case LM_OFF:    strip.clear(); break;
    case LM_STATIC: for (int i=0;i<LED_COUNT;i++) strip.setPixelColor(i, strip.Color(staticColor[0],staticColor[1],staticColor[2])); break;
    case LM_TEMP:   renderTemp(); break;
    case LM_RAINBOW:renderRainbow(); break;
    case LM_STREAM: renderFromBuffer(streamBuf); break;
    case LM_HOST:
      // Windows kontrolde -> onun renkleri; Windows bosta -> kapali (rainbow YOK)
      if (!autonomousMode) renderFromBuffer(lampColors);
      else                 strip.clear();
      break;
  }
  strip.show();
}

// ============================================================================
//  Fan kontrol
// ============================================================================
uint8_t fanCurve(float t) {
  // Sicaklik (C) -> duty (%) lineer interpolasyon. Min %20, <35C kapali degil.
  const int T[] = {35, 50, 60, 70, 80};
  const int D[] = {20, 35, 50, 75, 100};
  const int N = 5;
  if (t <= T[0]) return D[0];
  if (t >= T[N-1]) return D[N-1];
  for (int i = 1; i < N; i++) {
    if (t < T[i]) {
      float f = (t - T[i-1]) / (float)(T[i] - T[i-1]);
      return (uint8_t)(D[i-1] + f * (D[i] - D[i-1]));
    }
  }
  return D[N-1];
}

void updateFans() {
  uint32_t now = millis();

  // RPM olcumu (1 sn pencere)
  static uint32_t lastRpmMs = 0;
  if (now - lastRpmMs >= 1000) {
    noInterrupts(); uint32_t c1 = tach1Count, c2 = tach2Count; tach1Count = 0; tach2Count = 0; interrupts();
    float secs = (now - lastRpmMs) / 1000.0f;
    fan1Rpm = (uint16_t)(c1 * 60.0f / TACH_PPR / secs);
    fan2Rpm = (uint16_t)(c2 * 60.0f / TACH_PPR / secs);
    lastRpmMs = now;
  }

  // Hedef duty
  uint8_t target;
  bool stale = (now - lastTempMs) > 5000;
  if (fanMode == 1) {
    target = fanManualDuty;                 // MANUAL
  } else if (stale) {
    target = 80;                            // failsafe: PC verisi yoksa guvenli yuksek
  } else {
    int t = max(cpuTemp, gpuTemp);
    smoothTemp = smoothTemp * 0.8f + t * 0.2f;  // EMA, salinim onleme
    target = fanCurve(smoothTemp);
  }

  // Yumusak gecis (ani siplama yok)
  if (target > fanDutyPct) fanDutyPct++;
  else if (target < fanDutyPct) fanDutyPct--;

  uint8_t duty = map(fanDutyPct, 0, 100, 0, (1 << PWM_RES) - 1);
  ledcWrite(FAN1_PWM_PIN, duty);
  ledcWrite(FAN2_PWM_PIN, duty);
}

// ============================================================================
//  Vendor HID durum gonderimi (cihaz -> PC)
// ============================================================================
void sendVendorStatus() {
  uint8_t b[VENDOR_STATUS_SIZE];
  memset(b, 0, sizeof(b));
  b[0] = (uint8_t)cpuTemp;
  b[1] = (uint8_t)gpuTemp;
  b[2] = fan1Rpm & 0xFF; b[3] = fan1Rpm >> 8;
  b[4] = fan2Rpm & 0xFF; b[5] = fan2Rpm >> 8;
  b[6] = fanDutyPct;
  b[7] = fanMode;
  b[8] = (uint8_t)currentMode;
  b[9] = autonomousMode ? 0 : 1;   // 1 = Windows kontrolde
  // --- Cihazda kayitli ayarlar (PC bunlari okuyup gosterir) ---
  b[10] = fanManualDuty;
  b[11] = ledBrightness;
  b[12] = staticColor[0]; b[13] = staticColor[1]; b[14] = staticColor[2];
  HID.SendReport(VENDOR_STATUS_ID, b, VENDOR_STATUS_SIZE);
}

// ----------------------------- NVS (flash) kalici ayar ----------------------
void loadSettings() {
  prefs.begin("cooler", true);
  uint8_t m = prefs.getUChar("mode", LM_HOST);
  currentMode    = (m <= LM_HOST) ? (LightMode)m : LM_HOST;
  fanMode        = prefs.getUChar("fanmode", 0);
  fanManualDuty  = prefs.getUChar("fanduty", 50);
  ledBrightness  = prefs.getUChar("bright", 80);
  staticColor[0] = prefs.getUChar("sr", 0);
  staticColor[1] = prefs.getUChar("sg", 80);
  staticColor[2] = prefs.getUChar("sb", 255);
  prefs.end();
  strip.setBrightness(ledBrightness);
}

void saveSettings() {
  prefs.begin("cooler", false);
  prefs.putUChar("mode", (uint8_t)currentMode);   // NVS ayni degeri tekrar yazmaz
  prefs.putUChar("fanmode", fanMode);
  prefs.putUChar("fanduty", fanManualDuty);
  prefs.putUChar("bright", ledBrightness);
  prefs.putUChar("sr", staticColor[0]);
  prefs.putUChar("sg", staticColor[1]);
  prefs.putUChar("sb", staticColor[2]);
  prefs.end();
}

// ============================================================================
//  setup / loop
// ============================================================================
void setup() {
  memset(lampColors, 0, sizeof(lampColors));
  memset(streamBuf, 0, sizeof(streamBuf));

  strip.begin();
  loadSettings();    // NVS'den mod/fan/parlaklik/renk yukle
  strip.show();

  // Fan PWM
  ledcAttach(FAN1_PWM_PIN, PWM_FREQ, PWM_RES);
  ledcAttach(FAN2_PWM_PIN, PWM_FREQ, PWM_RES);
  // Fan tachometer
  pinMode(FAN1_TACH_PIN, INPUT_PULLUP);
  pinMode(FAN2_TACH_PIN, INPUT_PULLUP);
  attachInterrupt(FAN1_TACH_PIN, onTach1, FALLING);
  attachInterrupt(FAN2_TACH_PIN, onTach2, FALLING);
  // Fan gucu MOSFET'i: ESP32 calisir calismaz aktif et (asil kapatma PC/ESP32
  // gucu kesilince donanimsal olarak, pull-down direncle olur).
  pinMode(FAN_EN_PIN, OUTPUT);
  digitalWrite(FAN_EN_PIN, HIGH);

  // USB kimligi -> Windows'ta "CaYaLight"
  USB.VID(0x303A);
  USB.PID(0x4343);   // HID-only surum -> yeni PID (Windows sifirdan okusun)
  USB.manufacturerName("CaYaDev");
  USB.productName("CaYaLight");
  USB.serialNumber("CAYA-0002");   // descriptor degisti -> Windows taze okusun (yeni instance)

  lampArray.begin();
  HID.begin();
  USB.begin();

  lastTempMs = millis();
}

void loop() {
  static uint32_t lastRender = 0, lastFan = 0, lastStatus = 0;
  uint32_t now = millis();

  // STREAM (muzik) modunda kare gelir gelmez render et -> en dusuk gecikme
  if (currentMode == LM_STREAM) {
    if (streamDirty) { streamDirty = false; renderLights(); }
  } else if (now - lastRender >= 16) {   // diger modlar ~60 fps
    lastRender = now; renderLights();
  }
  if (now - lastFan    >= 50)  { lastFan = now; updateFans(); }
  if (now - lastStatus >= 500) { lastStatus = now; sendVendorStatus(); }

  // Ayar degisikligini NVS'ye yaz (flash yipranmasini onlemek icin seyrek;
  // NVS zaten ayni degeri tekrar yazmaz, bu yuzden 700 ms guvenli)
  static uint32_t lastSave = 0;
  if (settingsDirty && now - lastSave >= 700) {
    settingsDirty = false; lastSave = now; saveSettings();
  }
}
