// CaYaLight - ESP32-S2/S3 HID LampArray (Windows 11 Dynamic Lighting)
// WS2812 / NeoPixel seridini Windows "Dynamic Lighting" altinda gosterir.
//
// Onemli duzeltmeler (eski koda gore):
//  1) LampMultiUpdate / LampRangeUpdate / LampArrayControl raporlari ARTIK
//     FEATURE raporudur (0xB1), OUTPUT (0x91) DEGIL. Windows, LampArray'i
//     tamamen SET_FEATURE ile surer; Output raporu kullaninca surucu cihazi
//     baglamaz ve Dynamic Lighting'de hic gorunmez. -> _onSetFeature'da islenir.
//  2) LampMultiUpdate yerlesimi: ilk bayt LampCount, sonra LampUpdateFlags,
//     sonra 8x LampId, sonra renkler LAMP BASINA ARDISIK (R,G,B,I) gelir.
//     (Eski kodda LampCount yoktu ve renkler "planar" R[8],G[8],B[8],I[8] idi.)
//  3) IntensityLevelCount = 1 -> host nihai RGB gonderir, intensity yok sayilir.
//  4) Cihaz adi USB string'leri ile ayarlanir: "CaYaLight".
//
// Descriptor, Microsoft'un dogrulanmis ornegiyle bayt-bayt ayni:
// https://github.com/microsoft/ArduinoHidForWindows  (LampArrayReportDescriptor.h)

#include "USB.h"
#include "USBHID.h"
#include <Adafruit_NeoPixel.h>

#define LED_PIN    4
#define LED_COUNT  30

// Rapor ID'leri (descriptor ile birebir)
#define LAMP_ARRAY_ATTR_ID     1   // Feature, 22 bayt
#define LAMP_ATTR_REQUEST_ID   2   // Feature,  2 bayt
#define LAMP_ATTR_RESPONSE_ID  3   // Feature, 28 bayt
#define LAMP_MULTI_UPDATE_ID   4   // Feature, 50 bayt
#define LAMP_RANGE_UPDATE_ID   5   // Feature,  9 bayt
#define LAMP_CONTROL_ID        6   // Feature,  1 bayt

#define LAMP_UPDATE_COMPLETE   0x01
#define MULTI_UPDATE_LAMP_COUNT 8

Adafruit_NeoPixel strip(LED_COUNT, LED_PIN, NEO_GRB + NEO_KHZ800);
USBHID HID;

static volatile uint8_t  autonomousMode  = 1;   // 1: kendi efektini cizer, 0: host kontrolu
static volatile bool     needsUpdate     = false;
static uint16_t          requestedLampId = 0;
static uint8_t           lampColors[LED_COUNT][3];

// ---- Microsoft'un dogrulanmis LampArray report descriptor'i (HID Usage Tables 1.4) ----
static const uint8_t hid_descriptor[] = {
  0x05, 0x59,                      // UsagePage(Lighting And Illumination)
  0x09, 0x01,                      // UsageId(LampArray)
  0xA1, 0x01,                      // Collection(Application)

  // ---- Report 1: LampArrayAttributes (Feature, 22 bayt) ----
  0x85, LAMP_ARRAY_ATTR_ID,
  0x09, 0x02,                      //   UsageId(LampArrayAttributesReport)
  0xA1, 0x02,                      //   Collection(Logical)
    0x09, 0x03,                    //     LampCount
    0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00,
    0x95, 0x01, 0x75, 0x10, 0xB1, 0x03,
    0x09, 0x04, 0x09, 0x05, 0x09, 0x06, 0x09, 0x07, 0x09, 0x08, // BBox W/H/D, Kind, MinUpdateInterval
    0x27, 0xFF, 0xFF, 0xFF, 0x7F,
    0x95, 0x05, 0x75, 0x20, 0xB1, 0x03,
  0xC0,

  // ---- Report 2: LampAttributesRequest (Feature, 2 bayt) ----
  0x85, LAMP_ATTR_REQUEST_ID,
  0x09, 0x20,
  0xA1, 0x02,
    0x09, 0x21,                    //     LampId
    0x27, 0xFF, 0xFF, 0x00, 0x00,
    0x95, 0x01, 0x75, 0x10, 0xB1, 0x02,
  0xC0,

  // ---- Report 3: LampAttributesResponse (Feature, 28 bayt) ----
  0x85, LAMP_ATTR_RESPONSE_ID,
  0x09, 0x22,
  0xA1, 0x02,
    0x09, 0x21,                    //     LampId
    0xB1, 0x02,
    0x09, 0x23, 0x09, 0x24, 0x09, 0x25, 0x09, 0x27, 0x09, 0x26, // PosX/Y/Z, UpdateLatency, Purposes
    0x27, 0xFF, 0xFF, 0xFF, 0x7F,
    0x95, 0x05, 0x75, 0x20, 0xB1, 0x02,
    0x09, 0x28, 0x09, 0x29, 0x09, 0x2A, 0x09, 0x2B, 0x09, 0x2C, 0x09, 0x2D, // Red/Grn/Blu/Int level, IsProg, InputBinding
    0x26, 0xFF, 0x00,
    0x95, 0x06, 0x75, 0x08, 0xB1, 0x02,
  0xC0,

  // ---- Report 4: LampMultiUpdate (Feature, 50 bayt) ----
  0x85, LAMP_MULTI_UPDATE_ID,
  0x09, 0x50,
  0xA1, 0x02,
    0x09, 0x03,                    //     LampCount (bu rapordaki lamba sayisi, max 8)
    0x25, 0x08, 0x95, 0x01, 0xB1, 0x02,
    0x09, 0x55,                    //     LampUpdateFlags
    0x25, 0x01, 0xB1, 0x02,
    0x09, 0x21,                    //     LampId[8] (16-bit)
    0x27, 0xFF, 0xFF, 0x00, 0x00,
    0x95, 0x08, 0x75, 0x10, 0xB1, 0x02,
    // Renkler LAMBA BASINA ARDISIK: (R,G,B,I) x 8 = 32 bayt
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x26, 0xFF, 0x00,
    0x95, 0x20, 0x75, 0x08, 0xB1, 0x02,
  0xC0,

  // ---- Report 5: LampRangeUpdate (Feature, 9 bayt) ----
  0x85, LAMP_RANGE_UPDATE_ID,
  0x09, 0x60,
  0xA1, 0x02,
    0x09, 0x55,                    //     LampUpdateFlags
    0x25, 0x01, 0x95, 0x01, 0xB1, 0x02,
    0x09, 0x61, 0x09, 0x62,        //     LampIdStart, LampIdEnd
    0x27, 0xFF, 0xFF, 0x00, 0x00,
    0x95, 0x02, 0x75, 0x10, 0xB1, 0x02,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, // R,G,B,I
    0x26, 0xFF, 0x00,
    0x95, 0x04, 0x75, 0x08, 0xB1, 0x02,
  0xC0,

  // ---- Report 6: LampArrayControl (Feature, 1 bayt) ----
  0x85, LAMP_CONTROL_ID,
  0x09, 0x70,
  0xA1, 0x02,
    0x09, 0x71,                    //     AutonomousMode
    0x25, 0x01, 0x95, 0x01, 0xB1, 0x02,
  0xC0,

  0xC0
};

class LampArrayDevice : public USBHIDDevice {
public:
  void begin() { HID.addDevice(this, sizeof(hid_descriptor)); }

  uint16_t _onGetDescriptor(uint8_t* buf) override {
    memcpy(buf, hid_descriptor, sizeof(hid_descriptor));
    return sizeof(hid_descriptor);
  }

  // Windows TUM kontrolu SET_FEATURE ile yapar (multi/range/control dahil).
  void _onSetFeature(uint8_t id, const uint8_t* buf, uint16_t len) override {
    switch (id) {
      case LAMP_ATTR_REQUEST_ID:
        requestedLampId = buf[0] | ((uint16_t)buf[1] << 8);
        break;
      case LAMP_MULTI_UPDATE_ID:
        handleMultiUpdate(buf);
        break;
      case LAMP_RANGE_UPDATE_ID:
        handleRangeUpdate(buf);
        break;
      case LAMP_CONTROL_ID:
        autonomousMode = buf[0];
        if (autonomousMode) needsUpdate = true; // efekte geri don
        break;
    }
  }

  uint16_t _onGetFeature(uint8_t id, uint8_t* buf, uint16_t len) override {
    if (id == LAMP_ARRAY_ATTR_ID)    { sendArrayAttr(buf);                 return 22; }
    if (id == LAMP_ATTR_RESPONSE_ID) {
      sendLampAttr(buf, requestedLampId);
      // Microsoft ornegi gibi: bir sonraki lambaya gec (art arda GET ile enumerate icin)
      if (++requestedLampId >= LED_COUNT) requestedLampId = 0;
      return 28;
    }
    return 0;
  }

private:
  void sendArrayAttr(uint8_t* buf) {
    uint16_t count = LED_COUNT;  memcpy(buf,    &count, 2); // LampCount
    uint32_t w = 300000;         memcpy(buf+2,  &w, 4);     // BBox W (um) = 300 mm
    uint32_t h = 10000;          memcpy(buf+6,  &h, 4);     // BBox H (um)
    uint32_t d = 3000;           memcpy(buf+10, &d, 4);     // BBox D (um)
    uint32_t k = 4;              memcpy(buf+14, &k, 4);     // LampArrayKind = Peripheral
    uint32_t t = 33000;          memcpy(buf+18, &t, 4);     // MinUpdateInterval (us) = 33 ms
  }

  void sendLampAttr(uint8_t* buf, uint16_t lampId) {
    if (lampId >= LED_COUNT) lampId = 0;
    uint16_t id  = lampId;          memcpy(buf,    &id,  2);
    uint32_t x   = lampId * 10000;  memcpy(buf+2,  &x,   4); // PosX (um)
    uint32_t y   = 5000;            memcpy(buf+6,  &y,   4); // PosY (um)
    uint32_t z   = 0;               memcpy(buf+10, &z,   4); // PosZ
    uint32_t lat = 33000;           memcpy(buf+14, &lat, 4); // UpdateLatency (us)
    uint32_t pur = 0x02;            memcpy(buf+18, &pur, 4); // LampPurpose = Accent
    buf[22] = 0xFF; // RedLevelCount
    buf[23] = 0xFF; // GreenLevelCount
    buf[24] = 0xFF; // BlueLevelCount
    buf[25] = 0x01; // IntensityLevelCount (1 -> host nihai RGB gonderir)
    buf[26] = 0x01; // IsProgrammable = true
    buf[27] = 0x00; // InputBinding (yok)
  }

  // Yerlesim: [0]=LampCount [1]=Flags [2..17]=LampId[8] [18..49]=(R,G,B,I)x8
  void handleMultiUpdate(const uint8_t* buf) {
    uint8_t count = buf[0];
    if (count > MULTI_UPDATE_LAMP_COUNT) count = MULTI_UPDATE_LAMP_COUNT;
    for (uint8_t i = 0; i < count; i++) {
      uint16_t lampId = buf[2 + i * 2] | ((uint16_t)buf[3 + i * 2] << 8);
      if (lampId < LED_COUNT) {
        const uint8_t* c = &buf[18 + i * 4];
        lampColors[lampId][0] = c[0]; // R
        lampColors[lampId][1] = c[1]; // G
        lampColors[lampId][2] = c[2]; // B
        // c[3] = Intensity (kullanilmiyor; IntensityLevelCount=1)
      }
    }
    if (buf[1] & LAMP_UPDATE_COMPLETE) needsUpdate = true;
  }

  // Yerlesim: [0]=Flags [1..2]=Start [3..4]=End [5..8]=R,G,B,I
  void handleRangeUpdate(const uint8_t* buf) {
    uint16_t start = buf[1] | ((uint16_t)buf[2] << 8);
    uint16_t end   = buf[3] | ((uint16_t)buf[4] << 8);
    for (uint16_t i = start; i <= end && i < LED_COUNT; i++) {
      lampColors[i][0] = buf[5];
      lampColors[i][1] = buf[6];
      lampColors[i][2] = buf[7];
    }
    if (buf[0] & LAMP_UPDATE_COMPLETE) needsUpdate = true;
  }
};

LampArrayDevice lampArray;

// strip.show() sadece loop() icinden cagrilir (USB task'inden cagirmak
// WS2812 zamanlamasini bozabilir).
void applyColors() {
  for (int i = 0; i < LED_COUNT; i++)
    strip.setPixelColor(i, strip.Color(lampColors[i][0], lampColors[i][1], lampColors[i][2]));
  strip.show();
}

void rainbowTick() {
  static uint16_t hue = 0;
  for (int i = 0; i < LED_COUNT; i++)
    strip.setPixelColor(i, strip.gamma32(strip.ColorHSV(hue + i * 65536L / LED_COUNT)));
  strip.show();
  hue += 300;
}

void setup() {
  memset(lampColors, 0, sizeof(lampColors));
  strip.begin();
  strip.setBrightness(80);
  strip.show();

  // ---- Cihaz kimligi: Windows'ta "CaYaLight" olarak gorunur ----
  // (USB.begin()'den ONCE ayarlanmali)
  USB.VID(0x303A);                 // Espressif VID
  USB.PID(0x4341);                 // ozel PID (eski onbellegi temizlemek icin farkli tut)
  USB.manufacturerName("CaYaDev");
  USB.productName("CaYaLight");
  USB.serialNumber("CAYA-0001");

  lampArray.begin();
  HID.begin();
  USB.begin();
}

void loop() {
  if (autonomousMode) {
    rainbowTick();
    needsUpdate = false;
    delay(20);
  } else {
    if (needsUpdate) {
      needsUpdate = false;
      applyColors();
    }
    delay(2);
  }
}
