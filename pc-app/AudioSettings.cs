namespace CaYaControl;

/// <summary>Müzik (ses reaktif) modunun tüm ayarları. Canlı değiştirilebilir + kaydedilir.</summary>
public sealed class AudioSettings
{
    public enum VisMode { Spectrum, Unified }          // her LED ayrı / tek renk
    public enum ColorScheme { RainbowByPosition, FrequencyToHue, FixedColor, BassMidTreble, TwoColorGradient }
    public enum SpectrumStyle { LeftToRight, Mirror, CenterOut }
    public enum IdleMode { Off, DimColor, Breathing, KeepLast, SlowRainbow }

    public VisMode Mode = VisMode.Spectrum;
    public ColorScheme Scheme = ColorScheme.RainbowByPosition;
    public SpectrumStyle Style = SpectrumStyle.LeftToRight;

    // Genel
    public float Brightness = 1.0f;   // 0..1  ana parlaklık
    public float Gain = 1.5f;         // 0.2..8 hassasiyet (AutoGain kapalıyken)
    public bool  AutoGain = true;     // otomatik kazanç
    public float BassBoost = 1.6f;    // 0..3 bas vurgusu
    public float Attack = 0.85f;      // 0..1 yükselme hızı (1 = anlık)
    public float Decay = 0.22f;       // 0..1 düşme hızı
    public float Gamma = 0.6f;        // <1 düşük sesleri öne çıkarır

    // Gürültü / frekans filtresi
    public float NoiseGate = 0.04f;   // bu seviyenin altı "sessizlik" sayılır (es geçilir)
    public int   MinHz = 30;          // bu frekansın altını dikkate alma
    public int   MaxHz = 16000;       // bu frekansın üstünü dikkate alma

    // Boşta (sessizken) davranış
    public IdleMode Idle = IdleMode.DimColor;
    public float IdleAfterSec = 3.0f;
    public byte IdleR = 0, IdleG = 0, IdleB = 40;

    // Renk paletleri
    public byte FixR = 0, FixG = 128, FixB = 255;     // FixedColor
    public byte BassR = 255, BassG = 0, BassB = 0;    // BassMidTreble: bas
    public byte MidR = 0, MidG = 255, MidB = 0;       //                orta
    public byte TrebR = 0, TrebG = 80, TrebB = 255;   //                tiz
    public byte LowR = 0, LowG = 0, LowB = 255;       // TwoColorGradient: düşük frekans
    public byte HighR = 255, HighG = 0, HighB = 0;    //                  yüksek frekans
    public float HueStart = 0.66f;    // FrequencyToHue: başlangıç ton (mavi)
    public float HueSpan = -0.66f;    // ton aralığı (negatif: tize doğru kırmızı)
}
