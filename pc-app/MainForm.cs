using System.Drawing;
using System.Windows.Forms;

namespace CaYaControl;

public sealed class MainForm : Form
{
    private const int LedCount = 30;
    private static readonly Icon AppIcon = LoadAppIcon();

    // Modlar (firmware ile ayni sirada)
    private static readonly string[] ModeNames =
    {
        "Kapalı", "Sabit Renk", "Sıcaklık (termometre)", "Gökkuşağı",
        "Müzik (ses)", "Windows Dynamic Lighting"
    };
    private const int ModeStatic = 1, ModeAudio = 4;

    private readonly HardwareMonitor _hw = new();
    private readonly HidLink _serial = new();
    private readonly AppState _state = SettingsStore.Load();
    private readonly AudioSettings _audioSettings;
    private readonly AudioCapture _audio;
    private MusicSettingsForm? _musicForm;
    private volatile byte[]? _latestFrame;

    private Color _staticColor;
    private bool _initializing = true;
    private volatile bool _connecting;
    private bool _applyingFromDevice;   // cihazdan okunan degeri uygularken geri gondermeyi engelle
    private bool _syncPending;          // baglandiktan sonra ilk durumu cihazdan oku

    // --- ESP = otorite: komutu gonder, cihaz echo'su ile dogrula, olmazsa geri al ---
    private int _expectMode = -1;       // cihaz onayi beklenen mod (-1 = bekleyen yok)
    private int _lastDeviceMode = -1;   // cihazin son bildirdigi gercek mod (otorite kaynak)
    private DateTime _modeSentAt;       // mod komutunun gonderildigi an
    private const int CmdConfirmMs = 2000;  // bu sure icinde echo gelmezse komut basarisiz say

    // --- Komut kanali sagligi: PC sicaklik gonderiyorsa cihaz onu echo etmeli ---
    private int _lastSentCpu = -1;      // OnPoll'da cihaza gonderilen son CPU (>0 ise echo bekleriz)
    private int _noEchoCount;           // cihaz PC verisini echo etmiyor sayaci
    private bool _cmdChannelOk = true;  // false: PC->cihaz komut kanali yanit vermiyor

    // UI
    private readonly NotifyIcon _tray;
    private readonly Label _lblConn = new();
    private readonly Button _btnConnect = new();
    private readonly Label _lblCpu = new(), _lblGpu = new();
    private readonly ComboBox _cmbGpu = new();
    private readonly Label _lblRpm1 = new(), _lblRpm2 = new(), _lblDuty = new();
    private readonly ComboBox _cmbMode = new();
    private readonly Button _btnColor = new();
    private readonly TrackBar _trkBright = new();
    private readonly Label _lblBrightVal = new();
    private readonly Button _btnMusic = new();
    private readonly RadioButton _rbAuto = new(), _rbManual = new();
    private readonly TrackBar _trkDuty = new();
    private readonly Label _lblDutyVal = new();
    private readonly CheckBox _chkAutostart = new();
    private readonly Button _btnSensors = new();
    private readonly Label _lblStatus = new();

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _frame = new() { Interval = 25 };
    private readonly System.Windows.Forms.Timer _reconnect = new() { Interval = 3000 };

    public MainForm()
    {
        _audioSettings = _state.Audio;
        _staticColor = Color.FromArgb(_state.StaticColorArgb);
        _audio = new AudioCapture(LedCount, _audioSettings);
        Text = "CaYaControl";
        Icon = AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(380, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);

        BuildUi();

        _tray = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "CaYaControl",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _serial.StatusReceived += OnStatus;
        _serial.Log += msg => { if (!IsDisposed) BeginInvoke(() => _lblStatus.Text = msg); };
        _audio.FrameReady += f => _latestFrame = f;

        _poll.Tick += OnPoll;
        _frame.Tick += OnFrameTick;
        _reconnect.Tick += (_, _) => { if (!_serial.IsConnected) TryConnect(); };

        Load += (_, _) =>
        {
            _chkAutostart.Checked = Autostart.IsEnabled();

            // GPU listesi
            _cmbGpu.Items.Add("Otomatik (NVIDIA öncelikli)");
            foreach (string g in _hw.GpuNames()) _cmbGpu.Items.Add(g);
            int gi = _state.PreferredGpu == null ? 0 : _cmbGpu.Items.IndexOf(_state.PreferredGpu);
            _cmbGpu.SelectedIndex = gi < 0 ? 0 : gi;

            // Kaydedilmis durumu uygula
            _cmbMode.SelectedIndex = Math.Clamp(_state.LightMode, 0, ModeNames.Length - 1);
            if (_state.FanMode == 1) _rbManual.Checked = true; else _rbAuto.Checked = true;
            _trkDuty.Value = Math.Clamp(_state.FanDuty, 0, 100);
            _trkBright.Value = Math.Clamp(_state.Brightness, 0, 100);
            _lblDutyVal.Text = $"{_trkDuty.Value}%";
            _lblBrightVal.Text = $"{_trkBright.Value}%";
            _trkDuty.Enabled = _rbManual.Checked;

            _initializing = false;
            TryConnect();
            _poll.Start(); _frame.Start(); _reconnect.Start();
        };
    }

    // --------------------------------------------------------------- UI kurma
    private void BuildUi()
    {
        int y = 12;
        _lblConn.SetBounds(12, y, 356, 22);
        _lblConn.Text = "Bağlanıyor...";
        _lblConn.ForeColor = Color.Khaki;
        Controls.Add(_lblConn);
        y += 28;

        // Yeniden baglan (HID)
        _btnConnect.SetBounds(268, y - 1, 100, 28);
        _btnConnect.Text = "Yeniden Bağlan";
        _btnConnect.FlatStyle = FlatStyle.Flat;
        _btnConnect.Click += (_, _) => TryConnect();
        Controls.Add(_btnConnect);
        y += 36;

        // Sicaklik kartlari
        AddCard(12, y, "CPU", _lblCpu);
        AddCard(196, y, "GPU", _lblGpu);
        y += 80;

        // GPU secici (tum GPU'lar)
        Controls.Add(new Label { Text = "GPU:", Location = new Point(12, y + 4), AutoSize = true, ForeColor = Color.Silver });
        _cmbGpu.SetBounds(52, y, 316, 26);
        _cmbGpu.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbGpu.SelectedIndexChanged += (_, _) =>
        {
            _hw.PreferredGpuName = _cmbGpu.SelectedIndex <= 0 ? null : (string)_cmbGpu.SelectedItem!;
            SaveState();
        };
        Controls.Add(_cmbGpu);
        y += 36;

        // Fan bilgisi
        var grpFan = new GroupBox { Text = "Fanlar", ForeColor = Color.Gainsboro };
        grpFan.SetBounds(12, y, 356, 132);
        Controls.Add(grpFan);

        _lblRpm1.SetBounds(12, 24, 160, 20); _lblRpm1.Text = "Fan 1: -- RPM"; grpFan.Controls.Add(_lblRpm1);
        _lblRpm2.SetBounds(180, 24, 160, 20); _lblRpm2.Text = "Fan 2: -- RPM"; grpFan.Controls.Add(_lblRpm2);
        _lblDuty.SetBounds(12, 48, 200, 20); _lblDuty.Text = "Güç: -- %"; grpFan.Controls.Add(_lblDuty);

        _rbAuto.SetBounds(12, 74, 90, 22); _rbAuto.Text = "Otomatik"; _rbAuto.Checked = true;
        _rbManual.SetBounds(108, 74, 70, 22); _rbManual.Text = "Manuel";
        grpFan.Controls.Add(_rbAuto); grpFan.Controls.Add(_rbManual);
        _rbAuto.CheckedChanged += (_, _) => OnFanChanged();
        _rbManual.CheckedChanged += (_, _) => OnFanChanged();

        _trkDuty.SetBounds(12, 100, 260, 28);
        _trkDuty.AutoSize = false;
        _trkDuty.Minimum = 0; _trkDuty.Maximum = 100; _trkDuty.TickFrequency = 10; _trkDuty.Value = 50;
        _trkDuty.Scroll += (_, _) => OnFanChanged();
        grpFan.Controls.Add(_trkDuty);
        _lblDutyVal.SetBounds(280, 104, 60, 20); _lblDutyVal.Text = "50%"; grpFan.Controls.Add(_lblDutyVal);
        y += 144;

        // Isik modu
        var grpLight = new GroupBox { Text = "Işık", ForeColor = Color.Gainsboro };
        grpLight.SetBounds(12, y, 356, 134);
        Controls.Add(grpLight);

        _cmbMode.SetBounds(12, 26, 200, 26);
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Items.AddRange(ModeNames);
        _cmbMode.SelectedIndex = 5; // Windows DL
        _cmbMode.SelectedIndexChanged += (_, _) => OnModeChanged();
        grpLight.Controls.Add(_cmbMode);

        _btnColor.SetBounds(218, 25, 126, 28);
        _btnColor.Text = "Renk seç";
        _btnColor.BackColor = _staticColor;
        _btnColor.FlatStyle = FlatStyle.Flat;
        _btnColor.Click += (_, _) => PickColor();
        grpLight.Controls.Add(_btnColor);

        // Global parlaklik
        grpLight.Controls.Add(new Label { Text = "Parlaklık", Location = new Point(12, 64), AutoSize = true, ForeColor = Color.Silver });
        _trkBright.SetBounds(74, 60, 184, 30);
        _trkBright.AutoSize = false;
        _trkBright.Minimum = 0; _trkBright.Maximum = 100; _trkBright.TickStyle = TickStyle.None; _trkBright.Value = 60;
        _trkBright.Scroll += (_, _) => OnBrightChanged();
        grpLight.Controls.Add(_trkBright);
        _lblBrightVal.SetBounds(264, 64, 60, 20); _lblBrightVal.Text = "60%"; grpLight.Controls.Add(_lblBrightVal);

        // Muzik ayarlari
        _btnMusic.SetBounds(12, 98, 170, 28);
        _btnMusic.Text = "Müzik Ayarları...";
        _btnMusic.FlatStyle = FlatStyle.Flat;
        _btnMusic.Click += (_, _) => ShowMusicSettings();
        grpLight.Controls.Add(_btnMusic);
        y += 146;

        _chkAutostart.SetBounds(12, y, 230, 22);
        _chkAutostart.Text = "Windows ile başlat";
        _chkAutostart.CheckedChanged += (_, _) => { if (!_initializing) Autostart.Set(_chkAutostart.Checked); };
        Controls.Add(_chkAutostart);

        _btnSensors.SetBounds(250, y - 3, 118, 26);
        _btnSensors.Text = "Sensörler...";
        _btnSensors.FlatStyle = FlatStyle.Flat;
        _btnSensors.Click += (_, _) => ShowSensors();
        Controls.Add(_btnSensors);
        y += 30;

        _lblStatus.SetBounds(12, y, 356, 20);
        _lblStatus.ForeColor = Color.Gray;
        Controls.Add(_lblStatus);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "CaYaControl", "Tepside çalışmaya devam ediyor.", ToolTipIcon.Info);
            }
        };
    }

    private void AddCard(int x, int y, string title, Label valueLabel)
    {
        var card = new Panel { BackColor = Color.FromArgb(45, 45, 52) };
        card.SetBounds(x, y, 172, 74);
        var t = new Label { Text = title, ForeColor = Color.Gray, AutoSize = true, Location = new Point(10, 8) };
        valueLabel.Text = "-- °C";
        valueLabel.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        valueLabel.ForeColor = Color.White;
        valueLabel.SetBounds(8, 26, 156, 40);
        card.Controls.Add(t); card.Controls.Add(valueLabel);
        Controls.Add(card);
    }

    /// <summary>Gomulu appicon.ico'yu yukler; bulunamazsa sistem ikonuna duser.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("CaYaControl.appicon.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Göster", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitApp());
        return menu;
    }

    // --------------------------------------------------------------- olaylar
    private void TryConnect()
    {
        if (_connecting) return;
        _connecting = true;
        _lblConn.Text = "Bağlanıyor...";
        _lblConn.ForeColor = Color.Khaki;
        Task.Run(() =>
        {
            bool ok = _serial.Connect();
            _connecting = false;
            BeginInvoke(() => ApplyConnState(ok));
        });
    }

    private void ApplyConnState(bool ok)
    {
        if (ok)
        {
            _lblConn.Text = $"Bağlı: {_serial.DeviceName}";
            _lblConn.ForeColor = Color.LightGreen;
            _syncPending = true;   // ayarlari cihazdan oku ve goster (zorla yazma)
        }
        else
        {
            _lblConn.Text = "Cihaz bulunamadı (CaYaCooler bağlı/yüklü mü?)";
            _lblConn.ForeColor = Color.IndianRed;
        }
    }

    /// <summary>Cihazda kayitli ayarlari UI'a uygular (geri gondermeden).</summary>
    private void ApplyDeviceState(DeviceStatus st)
    {
        _applyingFromDevice = true;
        _cmbMode.SelectedIndex = Math.Clamp(st.Mode, 0, ModeNames.Length - 1);
        if (st.FanMode == 1) _rbManual.Checked = true; else _rbAuto.Checked = true;
        _trkDuty.Value = Math.Clamp(st.ManualDuty, 0, 100);
        _trkBright.Value = Math.Clamp(st.Brightness * 100 / 255, 0, 100);
        _trkDuty.Enabled = _rbManual.Checked;
        _lblDutyVal.Text = $"{_trkDuty.Value}%";
        _lblBrightVal.Text = $"{_trkBright.Value}%";
        _staticColor = Color.FromArgb(st.StaticR, st.StaticG, st.StaticB);
        _btnColor.BackColor = _staticColor;
        _applyingFromDevice = false;
    }

    private void OnPoll(object? s, EventArgs e)
    {
        var (cpu, gpu) = _hw.Read();
        _lblCpu.Text = cpu >= 0 ? $"{cpu} °C" : "-- °C";
        _lblGpu.Text = gpu >= 0 ? $"{gpu} °C" : "-- °C";
        _lblCpu.ForeColor = TempColor(cpu);
        _lblGpu.ForeColor = TempColor(gpu);
        if (_serial.IsConnected) { _serial.SendTemp(cpu, gpu); _lastSentCpu = cpu; }
        else { _lastSentCpu = -1; _noEchoCount = 0; _cmdChannelOk = true; }
    }

    private void OnFrameTick(object? s, EventArgs e)
    {
        if (_cmbMode.SelectedIndex == ModeAudio && _serial.IsConnected)
        {
            var f = _latestFrame;
            if (f != null) _serial.SendFrame(f);
        }
    }

    private void OnStatus(DeviceStatus st)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_syncPending) { _syncPending = false; ApplyDeviceState(st); }
            _lastDeviceMode = st.Mode;   // cihazin bildirdigi gercek mod = otorite kaynak

            _lblRpm1.Text = $"Fan 1: {st.Rpm1} RPM";
            _lblRpm2.Text = $"Fan 2: {st.Rpm2} RPM";
            _lblDuty.Text = $"Güç: %{st.Duty}" + (st.FanMode == 0 ? " (oto)" : " (manuel)");

            // Komut kanali sagligi: PC sicaklik gonderdiyse cihaz onu echo etmeli (st.Cpu).
            // Birkac turdur hic echo yoksa PC->cihaz komut kanali calismiyor demektir.
            if (_lastSentCpu > 0)
            {
                if (st.Cpu == 0) { if (_noEchoCount < 99) _noEchoCount++; }
                else _noEchoCount = 0;
                _cmdChannelOk = _noEchoCount < 4;
            }

            // Bekleyen mod komutunu cihaz echo'su ile dogrula (ESP = otorite)
            if (_expectMode >= 0)
            {
                if (st.Mode == _expectMode)
                {
                    _lblStatus.Text = $"Aktif: {ModeNames[_expectMode]} (uygulama kontrolünde)";
                    _expectMode = -1;
                    SaveState();   // cihaz onayladi -> ancak simdi kalici kaydet
                }
                else if ((DateTime.UtcNow - _modeSentAt).TotalMilliseconds > CmdConfirmMs)
                {
                    int want = _expectMode;
                    RevertModeToDevice();   // _expectMode'u da temizler
                    _lblStatus.Text = $"Cihaz “{ModeNames[want]}” moduna geçmedi — geri alındı.";
                }
                else
                {
                    _lblStatus.Text = $"“{ModeNames[_expectMode]}” uygulanıyor…";
                }
                return;
            }

            if (!_cmdChannelOk)
            {
                _lblStatus.Text = "⚠ Komut kanalı yanıt vermiyor — cihaz PC komutlarını almıyor (firmware/USB?).";
                return;
            }

            // Etiket gerçek aktif moda göre (cihazın bildirdiği moda göre)
            int devMode = st.Mode;
            if (devMode == 5) // Windows Dynamic Lighting
                _lblStatus.Text = st.Host == 1
                    ? "Windows Dynamic Lighting kontrol ediyor"
                    : "Windows DL boşta — Windows'ta efekt seç";
            else
                _lblStatus.Text = $"Aktif: {ModeNames[Math.Clamp(devMode, 0, ModeNames.Length - 1)]} (uygulama kontrolünde)";
        });
    }

    private void OnModeChanged()
    {
        int mode = _cmbMode.SelectedIndex;
        _btnColor.Enabled = mode == ModeStatic;
        if (mode == ModeAudio) _audio.Start(); else _audio.Stop();

        // Cihazdan/otoriteden gelen degisiklik: geri gonderme, dogrulama baslatma
        if (_applyingFromDevice) return;

        if (!_serial.IsConnected)
        {
            _lblStatus.Text = "Cihaz bağlı değil — değişiklik uygulanamadı.";
            RevertModeToDevice();
            return;
        }

        // Komutu gonder. SetFeature istisna atarsa hemen geri al.
        bool sent = _serial.SendMode(mode);
        if (sent && mode == ModeStatic)
            _serial.SendColor(_staticColor.R, _staticColor.G, _staticColor.B);

        if (!sent)
        {
            _lblStatus.Text = $"“{ModeNames[mode]}” gönderilemedi — geri alınıyor.";
            RevertModeToDevice();
            return;
        }

        // Cihaz onayini bekle: status echo'su bu modu bildirene kadar "uygulanıyor".
        // CmdConfirmMs icinde echo gelmezse OnStatus geri alir (ESP = otorite).
        _expectMode = mode;
        _modeSentAt = DateTime.UtcNow;
        _lblStatus.Text = $"“{ModeNames[mode]}” uygulanıyor…";
    }

    /// <summary>Arayuzu cihazin son bildirdigi gercek moda geri dondurur (komut reddedildiyse).</summary>
    private void RevertModeToDevice()
    {
        _expectMode = -1;
        if (_lastDeviceMode < 0) return;
        int dev = Math.Clamp(_lastDeviceMode, 0, ModeNames.Length - 1);
        if (_cmbMode.SelectedIndex == dev) return;
        _applyingFromDevice = true;
        _cmbMode.SelectedIndex = dev;   // OnModeChanged tekrar tetiklenir ama _applyingFromDevice ile yutulur
        _applyingFromDevice = false;
    }

    private void OnFanChanged()
    {
        int fanMode = _rbManual.Checked ? 1 : 0;
        _trkDuty.Enabled = _rbManual.Checked;
        _lblDutyVal.Text = $"{_trkDuty.Value}%";
        if (!_applyingFromDevice && _serial.IsConnected) _serial.SendFan(fanMode, _trkDuty.Value);
        if (!_applyingFromDevice) SaveState();
    }

    private void OnBrightChanged()
    {
        _lblBrightVal.Text = $"{_trkBright.Value}%";
        if (!_applyingFromDevice && _serial.IsConnected)
            _serial.SendBright(_trkBright.Value * 255 / 100);
        if (!_applyingFromDevice) SaveState();
    }

    private void SaveState()
    {
        if (_initializing) return;
        _state.LightMode = _cmbMode.SelectedIndex;
        _state.FanMode = _rbManual.Checked ? 1 : 0;
        _state.FanDuty = _trkDuty.Value;
        _state.Brightness = _trkBright.Value;
        _state.StaticColorArgb = _staticColor.ToArgb();
        _state.PreferredGpu = _cmbGpu.SelectedIndex <= 0 ? null : _cmbGpu.SelectedItem as string;
        SettingsStore.Save(_state);   // _state.Audio = _audioSettings (ayni referans)
    }

    private void ShowMusicSettings()
    {
        if (_musicForm == null || _musicForm.IsDisposed)
        {
            _musicForm = new MusicSettingsForm(_audioSettings);
            _musicForm.FormClosed += (_, _) => SaveState();
        }
        _musicForm.Show(this);
        _musicForm.BringToFront();
    }

    private void ShowSensors()
    {
        _hw.Read(); // taze sensör seçimi
        string text = _hw.DumpSensors();
        var f = new Form
        {
            Text = "Sensör Teşhisi — HWiNFO/BIOS ile karşılaştır",
            ClientSize = new Size(540, 480),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(32, 32, 36),
        };
        var tb = new TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
            Text = text,
        };
        f.Controls.Add(tb);
        f.Show(this);
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _staticColor, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _staticColor = dlg.Color;
            _btnColor.BackColor = _staticColor;
            if (_serial.IsConnected) _serial.SendColor(_staticColor.R, _staticColor.G, _staticColor.B);
            SaveState();
        }
    }

    private static Color TempColor(int t) =>
        t < 0 ? Color.Gray : t < 55 ? Color.LightGreen : t < 75 ? Color.Khaki : Color.IndianRed;

    private void ShowWindow()
    {
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    private void ExitApp()
    {
        SaveState();
        _poll.Stop(); _frame.Stop(); _reconnect.Stop();
        _audio.Dispose();
        _serial.Dispose();
        _hw.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
