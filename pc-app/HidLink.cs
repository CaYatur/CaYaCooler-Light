using HidSharp;
using HidSharp.Reports;

namespace CaYaControl;

/// <summary>
/// Cihaz ile VENDOR HID raporlari uzerinden haberlesir (CDC/seri YOK).
/// Komutlar Feature raporu (0x10) ile gider, durum Input raporu (0x11) ile gelir.
/// </summary>
public sealed class HidLink : IDisposable
{
    private const int VID = 0x303A, PID = 0x4343;
    private const uint VENDOR_PAGE = 0xFF00;
    private const byte CMD_ID = 0x10, STATUS_ID = 0x11;
    private const int VENDOR_STATUS_SIZE = 16;   // firmware Input (durum) raporu = 16 bayt

    // Komut kodlari (firmware ile ayni)
    private const byte CMD_TEMP = 0x01, CMD_MODE = 0x02, CMD_COLOR = 0x03,
                       CMD_FAN = 0x04, CMD_BRIGHT = 0x05, CMD_FRAME = 0x06;

    private HidDevice? _dev;
    private HidStream? _stream;
    private Thread? _reader;
    private volatile bool _running;
    private readonly object _lock = new();
    private int _featLen = 49;   // VENDOR_CMD_SIZE(48) + rapor ID; gercegi Connect'te cihazdan okunur

    public bool IsConnected => _stream != null;
    public string? DeviceName { get; private set; }

    public event Action<DeviceStatus>? StatusReceived;
    public event Action<string>? Log;

    /// <summary>VID/PID + vendor usage page'e gore cihazi bulur ve acar.</summary>
    public bool Connect()
    {
        if (IsConnected) Disconnect();
        try
        {
            HidDevice? found = null;
            foreach (HidDevice d in DeviceList.Local.GetHidDevices(VID, PID))
            {
                if (IsVendorCollection(d)) { found = d; break; }
            }
            if (found == null)
            {
                Log?.Invoke("Cihaz bulunamadı (CaYaCooler bağlı mı? Firmware yüklü mü?).");
                return false;
            }

            HidStream stream = found.Open();
            stream.ReadTimeout = 1500;
            lock (_lock)
            {
                _dev = found;
                _stream = stream;
                _featLen = found.GetMaxFeatureReportLength();
            }
            DeviceName = SafeName(found);
            StartReader();
            Log?.Invoke($"Bağlandı: {DeviceName}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("Bağlanılamadı: " + ex.Message);
            return false;
        }
    }

    private static bool IsVendorCollection(HidDevice d)
    {
        try
        {
            var rd = d.GetReportDescriptor();
            foreach (var di in rd.DeviceItems)
                foreach (uint usage in di.Usages.GetAllValues())
                    if ((usage >> 16) == VENDOR_PAGE) return true;
        }
        catch { }
        // yedek: yalniz vendor TLC'de Input (durum) raporu var; LampArray collection'da Input yok.
        try { if (d.GetMaxInputReportLength() >= VENDOR_STATUS_SIZE + 1) return true; } catch { }
        return false;
    }

    private static string SafeName(HidDevice d)
    {
        try { return d.GetProductName(); } catch { return d.DevicePath; }
    }

    private void StartReader()
    {
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true };
        _reader.Start();
    }

    private void ReadLoop()
    {
        int len;
        try { len = _dev!.GetMaxInputReportLength(); } catch { len = 17; }
        if (len < 11) len = 17;
        var buf = new byte[len];
        while (_running && _stream != null)
        {
            try
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n > 0 && buf[0] == STATUS_ID)
                    StatusReceived?.Invoke(DeviceStatus.Parse(buf));
            }
            catch (TimeoutException) { /* devam */ }
            catch
            {
                _running = false;
                lock (_lock) { try { _stream?.Close(); } catch { } _stream = null; _dev = null; }
                Log?.Invoke("Bağlantı koptu.");
                break;
            }
        }
    }

    /// <summary>Komutu cihaza yazar. SetFeature basarisizsa (istisna) false doner ve loglar.
    /// NOT: Windows komutu kabul edip cihaz isleyemezse istisna olusmayabilir; gercek
    /// dogrulama cihazin durum echo'su ile yapilir (bkz. MainForm mod dogrulama).</summary>
    private bool SendCmd(byte cmd, params byte[] payload)
    {
        lock (_lock)
        {
            if (_stream == null) return false;
            var buf = new byte[_featLen];
            buf[0] = CMD_ID;     // rapor ID
            buf[1] = cmd;        // firmware: d[0] = komut
            int n = Math.Min(payload.Length, buf.Length - 2);
            Array.Copy(payload, 0, buf, 2, n);   // firmware: d[1..] = payload
            try { _stream.SetFeature(buf); return true; }
            catch (Exception ex) { Log?.Invoke($"Komut gönderilemedi (0x{cmd:X2}): {ex.Message}"); return false; }
        }
    }

    public bool SendTemp(int cpu, int gpu) =>
        SendCmd(CMD_TEMP, (byte)Math.Clamp(cpu, 0, 255), (byte)Math.Clamp(gpu, 0, 255));
    public bool SendMode(int mode) => SendCmd(CMD_MODE, (byte)mode);
    public bool SendColor(int r, int g, int b) => SendCmd(CMD_COLOR, (byte)r, (byte)g, (byte)b);
    public bool SendFan(int mode, int dutyPct) => SendCmd(CMD_FAN, (byte)mode, (byte)Math.Clamp(dutyPct, 0, 100));
    public bool SendBright(int v) => SendCmd(CMD_BRIGHT, (byte)Math.Clamp(v, 0, 255));
    /// <summary>Kareyi 48-baytlik komut sinirina sigacak parcalar halinde gonderir (15 LED/parca).
    /// d[1]=baslangic LED, d[2]=adet, d[3..]=RGB. Firmware son parcada render eder.</summary>
    public bool SendFrame(byte[] rgb)
    {
        const int ledsPerChunk = 15;
        int leds = rgb.Length / 3;
        bool ok = true;
        for (int start = 0; start < leds; start += ledsPerChunk)
        {
            int count = Math.Min(ledsPerChunk, leds - start);
            var payload = new byte[2 + count * 3];
            payload[0] = (byte)start;
            payload[1] = (byte)count;
            Array.Copy(rgb, start * 3, payload, 2, count * 3);
            ok &= SendCmd(CMD_FRAME, payload);
        }
        return ok;
    }

    public void Disconnect()
    {
        _running = false;
        lock (_lock)
        {
            try { _stream?.Close(); } catch { }
            _stream = null; _dev = null;
        }
    }

    public void Dispose() => Disconnect();
}

/// <summary>Cihazdan gelen durum Input raporunun ayristirilmis hali.</summary>
public sealed class DeviceStatus
{
    public int Cpu, Gpu, Rpm1, Rpm2, Duty, FanMode, Mode, Host;
    // Cihazda kayitli ayarlar (uygulama bunlari okuyup gosterir)
    public int ManualDuty, Brightness, StaticR, StaticG, StaticB;

    // buf[0] = rapor ID (0x11), veri buf[1]'den baslar
    public static DeviceStatus Parse(byte[] b) => new()
    {
        Cpu = b[1],
        Gpu = b[2],
        Rpm1 = b[3] | (b[4] << 8),
        Rpm2 = b[5] | (b[6] << 8),
        Duty = b[7],
        FanMode = b[8],
        Mode = b[9],
        Host = b[10],
        ManualDuty = b[11],
        Brightness = b[12],
        StaticR = b[13],
        StaticG = b[14],
        StaticB = b[15],
    };
}
