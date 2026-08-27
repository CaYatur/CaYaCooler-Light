# CaYaCooler

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows 11](https://img.shields.io/badge/Platform-Windows%2011-0078D6)]()
[![MCU: ESP32-S2%2FS3](https://img.shields.io/badge/MCU-ESP32--S2%20%2F%20S3-E7352C)]()

**[English](README.md) | Türkçe**

ESP32-S2/S3 tabanlı akıllı soğutucu denetleyici — Windows 11 **Dynamic Lighting** (HID LampArray) desteği, 2× 4-pin PWM fan kontrolü/RPM okuma ve bir C# masaüstü uygulamasıyla CPU/GPU sıcaklığına, fan hızına ve müziğe tepki veren aydınlatma sağlar.

```
firmware/CaYaCooler/CaYaCooler.ino  -> ESP32-S2/S3 firmware (HID-only)
pc-app/                             -> C# .NET 8 WinForms uygulaması (CaYaControl)
CaYaLight/CaYaLight.ino             -> sadece Dynamic Lighting (test/sade sürüm)
```

- **Windows 11 Dynamic Lighting** (HID LampArray) — cihaz adı **CaYaLight**
- **2× 4-pin PWM fan** kontrolü (25 kHz) + tachometer ile **RPM** okuma
- **PC uygulaması** (sistem tepsisi + GUI): CPU/GPU sıcaklığı, fan RPM/mod, ışık modu, müzik moduyla ses reaktif aydınlatma
- **Saf HID** cihaz: hem Dynamic Lighting (LampArray) hem PC haberleşmesi (vendor HID raporları) aynı HID arabiriminden. **CDC/seri yok** (CDC eklenince Windows LampArray'i tanımıyordu).

> **Ayarlar** uygulama klasöründeki `cayacontrol.settings.json` dosyasına otomatik kaydedilir (ışık modu, fan, parlaklık, tüm müzik ayarları, seçili GPU).

---

## 0) Kullanılan donanım (referans)

Bu projede test edilen parçalar — birebir aynısını kullanmak zorunda değilsin, eşdeğerleri de çalışır:

| Parça | Not |
|---|---|
| ESP32-S2 Mini geliştirme kartı | Native USB OTG gerekli — orijinal ESP32 (S2/S3 öneki olmayan) **desteklenmez**. ESP32-S3 kartlarda da aynı firmware sorunsuz çalışır. |
| 2× 4-pin PWM kasa fanı (12V) | Standart Intel 4-pin pinout (GND / +12V / Tach / PWM). |
| WS2812/NeoPixel LED şeridi (30 LED) | `LED_COUNT` firmware'de değiştirilebilir. |
| Logic-level N-kanal MOSFET + 100Ω gate direnci + 100kΩ pull-down direnci | Aşağıdaki "PC kapalıyken fanı kes" devresi için. |

---

## 1) Donanım / Kablolama

### 4-pin PWM fan konektörü (standart pinout)

| Pin | Kablo | İşlev | Bağlantı |
|-----|-------|-------|----------|
| 1 | Siyah | GND | 12V kaynak GND **+** ESP32 GND (ortak şart) |
| 2 | Sarı  | +12V | 12V kaynak + |
| 3 | Yeşil | Tach (RPM) | ESP32 tach pini, 3.3V'a 10k pull-up |
| 4 | Mavi  | PWM (hız) | ESP32 PWM pini (3.3V çalışır; 5V level-shifter ideal) |

### ESP32-S2/S3 pin haritası

| İşlev | GPIO |
|-------|------|
| LED DIN (WS2812) | 4 |
| Fan 1 PWM | 5 |
| Fan 2 PWM | 6 |
| Fan 1 Tach | 7 |
| Fan 2 Tach | 15 |
| Fan güç MOSFET gate (FAN_EN, opsiyonel) | 16 |

> Kart pin çıkışların farklıysa (özellikle GPIO16, klon kartlarda flash/PSRAM için ayrılmış olabilir) yukarıdaki numaraları kendi kartının pinout diyagramına göre firmware'de güncelle.

### Önemli notlar

- **Fanları USB'den besleme.** Ayrı 12V kaynak (veya PC PSU Molex: sarı=12V, siyah=GND). 12V GND ile ESP32 GND **ortak** olmalı.
- Tach hatları açık-kollektördür; kod `INPUT_PULLUP` kullanır ama harici 10k pull-up daha güvenilirdir.
- LED şeridi: DIN'e ~330Ω seri direnç; 30 LED tam beyazda ~1.8A çeker → 5V'u ayrı besle, GND ortak. 3.3V→5V level shifter DIN'de kararlılığı artırır.
- İki fanı aynı hızda sürmek yeterliyse PWM hatlarını tek GPIO'ya paralelleyebilirsin; ama ayrı RPM için ayrı tach gerekir.

### "PC kapalıyken fanı kes" — MOSFET güç anahtarı

Fanların 12V hattı, gate'i ESP32'ye bağlı bir **N-kanal logic-level MOSFET** üzerinden anahtarlanır. Güvenlik tamamen **donanımsal**: ESP32 sadece PC/anakart USB güç aldığı sürece çalışır, dolayısıyla PC kapanınca ESP32 de kapanır → gate'teki **100kΩ pull-down direnç** gate'i LOW'a çeker → MOSFET kesime gider → fan **her koşulda** kapanır (firmware çökse veya hiç yüklenmese bile).

```
                +12V (ayrı besleme) ──────────────┬─────────────┐
                                                   │             │
                                              Fan 1 || Fan 2     │
                                             (4-pin PWM, paralel)│
                                                   │             │
                     ESP32 GPIO5 ── PWM ───────────┤             │
                     ESP32 GPIO7 ── tach ──────────┤             │
                                                   │             │
                                              fan(-) / GND        │
                                                   │             │
                                                   ▼             │
                                          ┌─────────────────┐    │
                     ESP32 GPIO16 ──100Ω──┤ G   N-MOSFET  D ├────┘
                                          │                 │
                                          │        S        │
                                          └────────┬────────┘
                                                   │
                    GPIO16 ──100kΩ pull-down──┐    │
                                               ├────┘
                                              GND (ESP32 + 12V kaynak ortak)
```

- Gate'e giden 100Ω direnç MOSFET anahtarlama darbesini sınırlar (isteğe bağlı ama önerilir).
- 100kΩ pull-down, ESP32 pini yüksek empedanslı (reset/boot anı, güç yokken) olduğunda gate'i kesin LOW'da tutar — **fan varsayılan olarak kapalı**, sadece ESP32 açıkça HIGH yazınca açılır.
- Firmware, `setup()` içinde bu pini (`FAN_EN_PIN`, varsayılan GPIO16) açılışta HIGH yapar; devreyi kullanmıyorsan pini boş bırakabilirsin, koda dokunmana gerek yok.
- Fan(-) ucunu doğrudan MOSFET drain'ine değil, ortak GND'ye bağlayıp MOSFET'i **12V tarafında** (high-side, P-MOSFET ile) anahtarlamak istersen devreyi buna göre uyarlaman gerekir; yukarıdaki low-side N-MOSFET devresi en basit ve yaygın çözümdür.

---

## 2) Firmware (Arduino IDE)

1. **Boards Manager** → "esp32" by Espressif, **sürüm 3.x** (ledcAttach API gerekir).
2. **Library Manager** → "Adafruit NeoPixel".
3. Kart: ESP32-S2 veya ESP32-S3 modülün (ikisi de native USB OTG'ye sahip, aynı firmware çalışır). Ayarlar:
   - **USB Mode: USB-OTG (TinyUSB)**
   - **USB CDC On Boot: Disabled**  ← şart (CDC açıkken Dynamic Lighting cihazı görmüyor)
4. `firmware/CaYaCooler/CaYaCooler.ino` → Derle/Yükle.

> Daha önce bozuk descriptor ile taktıysan, Aygıt Yöneticisi'nden cihazı kaldırıp yeniden tak (Windows USB descriptor önbelleğini temizlemek için).

---

## 3) PC Uygulaması (CaYaControl)

Gereksinim: **.NET 8 SDK** (veya üzeri). NuGet paketleri otomatik iner.

```powershell
cd pc-app
dotnet build -c Release
# Çalıştır (yönetici hakkı ister - CPU sıcaklığı için gerekli):
.\bin\Release\net8.0-windows\CaYaControl.exe
```

Tek dosya, taşınabilir sürüm:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# çıktı: pc-app\bin\Release\net8.0-windows\win-x64\publish\CaYaControl.exe
```

Hazır derlenmiş sürümler için **[Releases](../../releases)** sayfasına bakabilirsin.

### Kullanım

- Uygulama açılınca cihazı **HID üzerinden** otomatik bulur (VID 0x303A / PID 0x4343, vendor usage 0xFF00). COM portu yok. Bulamazsa **Yeniden Bağlan**.
- **CPU / GPU** sıcaklığı kartlarda; sağda **GPU seçici** (NVIDIA öncelikli, tüm GPU'lar listede). **Fan RPM** ve **güç %** cihazdan canlı gelir.
- **Işık modu**: Kapalı / Sabit Renk / Sıcaklık / Gökkuşağı / **Müzik (ses)** / **Windows Dynamic Lighting**.
  - "Windows Dynamic Lighting" dışında bir mod seçince uygulama aydınlatmayı devralır.
  - **Müzik Ayarları...**: görsel mod (spektrum/tek renk), renk şeması (gökkuşağı/frekans→renk/sabit/bas-orta-tiz/iki renk geçişi), spektrum stili (aynalı/merkezden), parlaklık, hassasiyet/AGC, bas vurgusu, tepki/sönme, gamma, **gürültü kapısı** (sessizliği es geç), **frekans aralığı** (min/max Hz), **boşta davranışı** (kapalı/sabit/nefes/sabit kal/yavaş gökkuşağı).
- **Fan**: Otomatik (sıcaklık eğrisi) veya Manuel (kaydırma çubuğu).
- **Windows ile başlat**: "en yüksek ayrıcalık"lı bir Zamanlanmış Görev kurar.
- Pencereyi kapatmak uygulamayı **tepsiye** küçültür; çıkmak için tepsi menüsü → Çıkış.
- Tüm ayarlar `cayacontrol.settings.json`'a otomatik kaydedilir.

---

## 4) HID protokol (referans)

Haberleşme vendor HID raporlarıyla (CDC/seri yok):

**PC → cihaz** — Feature raporu `0x10` (48 bayt): `[cmd][payload...]`
```
0x01 TEMP   cpu, gpu
0x02 MODE   mod (0..5)
0x03 COLOR  r, g, b
0x04 FAN    mod(0=oto/1=manuel), duty%
0x05 BRIGHT 0..255
0x06 FRAME  parçalı: [baslangic][adet][RGB...] (15 LED'lik parçalar halinde)
```

**cihaz → PC** — Input raporu `0x11` (16 bayt):
```
cpu, gpu, rpm1(LE), rpm2(LE), duty, fanmode, mode, host, fanManualDuty, brightness, staticColor(r,g,b)
```

---

## 5) Güvenlik / failsafe

- PC verisi 5 sn gelmezse fanlar otomatik **%80**'e çıkar (düşüğe değil).
- Fan eğrisi histerezisli (EMA) ve yumuşak geçişli — ani RPM sıçraması yok.
- MOSFET güç anahtarı sayesinde PC/ESP32 kapalıyken fanlar **donanımsal olarak** kesilir (bkz. [§1 "PC kapalıyken fanı kes"](#pc-kapalıyken-fanı-kes--mosfet-güç-anahtarı)).
- `LampArrayKind = Peripheral (4)` → Dynamic Lighting'de renkli ızgara ikonu. Değiştirmek için firmware'deki `k` değeri.

---

## Katkı

Issue ve PR'lar açık — özellikle farklı ESP32-S2/S3 kart varyantlarıyla test sonuçları, yeni ışık modları veya hata düzeltmeleri memnuniyetle karşılanır.

## Lisans

[MIT](LICENSE) © 2026 Çağan Turgut (CaYatur)

Bu proje şu üçüncü taraf kütüphaneleri kullanır (kendi lisanslarıyla, ayrı NuGet/Arduino paketleri olarak): [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0), [NAudio](https://github.com/naudio/NAudio) (MIT), [HidSharp](https://github.com/IntergatedCircuits/HidSharp) (Apache-2.0/MIT), [Adafruit NeoPixel](https://github.com/adafruit/Adafruit_NeoPixel) (LGPL-3.0).
