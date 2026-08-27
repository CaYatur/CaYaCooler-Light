using System.Drawing;
using System.Windows.Forms;

namespace CaYaControl;

/// <summary>Müzik modunun kapsamlı, canlı ayar penceresi (kaydırılabilir).</summary>
public sealed class MusicSettingsForm : Form
{
    private readonly AudioSettings _s;
    private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private int _y = 12;
    private TrackBar? _gainBar;

    public MusicSettingsForm(AudioSettings settings)
    {
        _s = settings;
        Text = "Müzik Ayarları";
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(440, 640);
        MinimumSize = new Size(456, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        Controls.Add(_panel);

        Section("Görsel");
        AddCombo("Görsel mod", new[] { "Spektrum (her LED ayrı)", "Tek Renk (frekansa göre)" },
            (int)_s.Mode, i => _s.Mode = (AudioSettings.VisMode)i);
        AddCombo("Renk şeması", new[] { "Konuma göre gökkuşağı", "Frekansa göre renk", "Sabit renk", "Bas-Orta-Tiz", "İki renk geçişi" },
            (int)_s.Scheme, i => _s.Scheme = (AudioSettings.ColorScheme)i);
        AddCombo("Spektrum stili", new[] { "Soldan sağa", "Aynalı (merkez bas)", "Merkezden dışa" },
            (int)_s.Style, i => _s.Style = (AudioSettings.SpectrumStyle)i);

        Section("Renkler");
        AddColor("Sabit renk", Col(_s.FixR, _s.FixG, _s.FixB), c => { _s.FixR = c.R; _s.FixG = c.G; _s.FixB = c.B; });
        AddColor("Bas rengi", Col(_s.BassR, _s.BassG, _s.BassB), c => { _s.BassR = c.R; _s.BassG = c.G; _s.BassB = c.B; });
        AddColor("Orta rengi", Col(_s.MidR, _s.MidG, _s.MidB), c => { _s.MidR = c.R; _s.MidG = c.G; _s.MidB = c.B; });
        AddColor("Tiz rengi", Col(_s.TrebR, _s.TrebG, _s.TrebB), c => { _s.TrebR = c.R; _s.TrebG = c.G; _s.TrebB = c.B; });
        AddColor("Geçiş: düşük frekans", Col(_s.LowR, _s.LowG, _s.LowB), c => { _s.LowR = c.R; _s.LowG = c.G; _s.LowB = c.B; });
        AddColor("Geçiş: yüksek frekans", Col(_s.HighR, _s.HighG, _s.HighB), c => { _s.HighR = c.R; _s.HighG = c.G; _s.HighB = c.B; });
        AddSlider("Ton başlangıcı (frekans→renk)", 0, 100, (int)(_s.HueStart * 100), v => _s.HueStart = v / 100f, "");
        AddSlider("Ton aralığı (-100..100)", -100, 100, (int)(_s.HueSpan * 100), v => _s.HueSpan = v / 100f, "");

        Section("Tepki / hassasiyet");
        AddSlider("Parlaklık", 0, 100, (int)(_s.Brightness * 100), v => _s.Brightness = v / 100f, "%");
        AddCheck("Otomatik kazanç (AGC)", _s.AutoGain, c => { _s.AutoGain = c; if (_gainBar != null) _gainBar.Enabled = !c; });
        _gainBar = AddSlider("Hassasiyet (gain)", 20, 800, (int)(_s.Gain * 100), v => _s.Gain = v / 100f, "");
        _gainBar.Enabled = !_s.AutoGain;
        AddSlider("Bas vurgusu", 0, 300, (int)(_s.BassBoost * 100), v => _s.BassBoost = v / 100f, "");
        AddSlider("Tepki hızı (attack)", 5, 100, (int)(_s.Attack * 100), v => _s.Attack = v / 100f, "");
        AddSlider("Sönme yumuşaklığı", 2, 100, (int)(_s.Decay * 100), v => _s.Decay = v / 100f, "");
        AddSlider("Düşük ses vurgusu (gamma)", 30, 100, (int)(_s.Gamma * 100), v => _s.Gamma = v / 100f, "");

        Section("Gürültü / frekans filtresi");
        AddSlider("Gürültü kapısı (sessizliği es geç)", 0, 40, (int)(_s.NoiseGate * 100), v => _s.NoiseGate = v / 100f, "%");
        AddSlider("Min frekans (Hz)", 20, 1000, _s.MinHz, v => _s.MinHz = v, " Hz");
        AddSlider("Max frekans (Hz)", 2000, 20000, _s.MaxHz, v => _s.MaxHz = v, " Hz");

        Section("Boşta (sessizken)");
        AddCombo("Boşta modu", new[] { "Kapalı", "Sabit renk", "Nefes", "Sabit kal", "Yavaş gökkuşağı" },
            (int)_s.Idle, i => _s.Idle = (AudioSettings.IdleMode)i);
        AddSlider("Kaç saniye sonra boşta", 1, 30, (int)_s.IdleAfterSec, v => _s.IdleAfterSec = v, " sn");
        AddColor("Boşta rengi", Col(_s.IdleR, _s.IdleG, _s.IdleB), c => { _s.IdleR = c.R; _s.IdleG = c.G; _s.IdleB = c.B; });

        var info = new Label
        {
            Text = "Ayarlar anında uygulanır ve otomatik kaydedilir.",
            ForeColor = Color.Gray, AutoSize = false,
        };
        info.SetBounds(14, _y + 4, 400, 24);
        _panel.Controls.Add(info);
    }

    private static Color Col(byte r, byte g, byte b) => Color.FromArgb(r, g, b);

    private void Section(string title)
    {
        _y += 6;
        var lbl = new Label
        {
            Text = title.ToUpperInvariant(),
            ForeColor = Color.MediumSlateBlue,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
        };
        lbl.Location = new Point(12, _y);
        _panel.Controls.Add(lbl);
        _y += 24;
    }

    private void AddCombo(string label, string[] items, int sel, Action<int> onChange)
    {
        _panel.Controls.Add(new Label { Text = label, Location = new Point(14, _y + 4), AutoSize = true, ForeColor = Color.Silver });
        var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cmb.SetBounds(190, _y, 220, 26);
        cmb.Items.AddRange(items);
        cmb.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
        cmb.SelectedIndexChanged += (_, _) => onChange(cmb.SelectedIndex);
        _panel.Controls.Add(cmb);
        _y += 36;
    }

    private void AddCheck(string label, bool val, Action<bool> onChange)
    {
        var chk = new CheckBox { Text = label, Checked = val, ForeColor = Color.Gainsboro };
        chk.SetBounds(14, _y, 400, 22);
        chk.CheckedChanged += (_, _) => onChange(chk.Checked);
        _panel.Controls.Add(chk);
        _y += 28;
    }

    private void AddColor(string label, Color current, Action<Color> apply)
    {
        _panel.Controls.Add(new Label { Text = label, Location = new Point(14, _y + 4), AutoSize = true, ForeColor = Color.Silver });
        var btn = new Button { FlatStyle = FlatStyle.Flat, BackColor = current };
        btn.SetBounds(300, _y, 110, 26);
        btn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = btn.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) { btn.BackColor = dlg.Color; apply(dlg.Color); }
        };
        _panel.Controls.Add(btn);
        _y += 34;
    }

    private TrackBar AddSlider(string label, int min, int max, int val, Action<int> onChange, string unit)
    {
        _panel.Controls.Add(new Label { Text = label, Location = new Point(14, _y), AutoSize = true, ForeColor = Color.Silver });
        var valLbl = new Label { Location = new Point(360, _y), AutoSize = true, ForeColor = Color.White };
        _panel.Controls.Add(valLbl);
        _y += 20;
        var bar = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false };
        bar.SetBounds(10, _y, 420, 30);
        bar.Value = Math.Clamp(val, min, max);
        valLbl.Text = bar.Value + unit;
        bar.Scroll += (_, _) => { onChange(bar.Value); valLbl.Text = bar.Value + unit; };
        _panel.Controls.Add(bar);
        _y += 40;
        return bar;
    }
}
