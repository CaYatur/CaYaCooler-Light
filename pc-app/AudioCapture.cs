using NAudio.Dsp;
using NAudio.Wave;

namespace CaYaControl;

/// <summary>
/// Sistem sesini (WASAPI loopback) yakalar, kayan pencereli FFT yapar ve
/// AudioSettings'e göre LED karesi üretir. Gürültü kapısı, frekans aralığı,
/// boşta davranışı ve çeşitli frekans-bazlı renk şemalarını destekler.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private const int FftSize = 1024;
    private const int FftM = 10;
    private const int Hop = 256;        // ~5.3 ms @48kHz

    private readonly int _ledCount;
    private readonly AudioSettings _s;

    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _ring = new float[FftSize];
    private int _writePos, _sinceHop;

    private readonly float[] _levels;
    private float _uni;
    private float _peak = 0.001f;
    private float _hueRot;
    private long _lastSoundTick = Environment.TickCount64;
    private byte[] _lastFrame;

    public event Action<byte[]>? FrameReady;

    public AudioCapture(int ledCount, AudioSettings settings)
    {
        _ledCount = ledCount;
        _s = settings;
        _levels = new float[ledCount];
        _lastFrame = new byte[ledCount * 3];
    }

    public void Start()
    {
        if (_capture != null) return;
        _capture = new WasapiLoopbackCapture();
        _sampleRate = _capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
        Array.Clear(_levels);
        _uni = 0;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        int ch = _capture.WaveFormat.Channels;
        int bps = _capture.WaveFormat.BitsPerSample / 8;
        int stride = bps * ch;
        for (int i = 0; i + stride <= e.BytesRecorded; i += stride)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
                sum += BitConverter.ToSingle(e.Buffer, i + c * bps);
            _ring[_writePos] = sum / ch;
            _writePos = (_writePos + 1) % FftSize;
            if (++_sinceHop >= Hop) { _sinceHop = 0; Process(); }
        }
    }

    private void Process()
    {
        for (int i = 0; i < FftSize; i++)
        {
            float s = _ring[(_writePos + i) % FftSize];
            float w = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            _fft[i].X = s * w; _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftM, _fft);

        int half = FftSize / 2;
        Span<float> mag = stackalloc float[half];
        int minBin = Math.Clamp(_s.MinHz * FftSize / _sampleRate, 1, half - 2);
        int maxBin = Math.Clamp(_s.MaxHz * FftSize / _sampleRate, minBin + 1, half - 1);
        int bassBins = Math.Clamp(190 * FftSize / _sampleRate, 2, half - 1);

        float total = 0, bass = 0, centNum = 0, centDen = 0;
        for (int k = minBin; k <= maxBin; k++)
        {
            float m = (float)Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
            mag[k] = m; total += m;
            if (k < bassBins) bass += m;
            centNum += k * m; centDen += m;
        }
        bass *= _s.BassBoost;
        float centroid = centDen > 0
            ? Math.Clamp(((centNum / centDen) - minBin) / Math.Max(1, maxBin - minBin), 0f, 1f)
            : 0f;

        float driveRaw = total * 0.02f + bass * 0.6f;
        _peak = Math.Max(driveRaw, _peak * 0.992f);
        float gain = _s.AutoGain ? (1f / (_peak + 1e-4f)) : _s.Gain;
        float driveNorm = Math.Clamp((float)Math.Pow(driveRaw * gain, _s.Gamma), 0f, 1f);

        long now = Environment.TickCount64;
        bool gated = driveNorm < _s.NoiseGate;
        if (!gated) _lastSoundTick = now;
        bool idle = (now - _lastSoundTick) / 1000f >= _s.IdleAfterSec;

        var rgb = new byte[_ledCount * 3];

        if (idle) { BuildIdle(rgb); }
        else if (_s.Mode == AudioSettings.VisMode.Unified) { BuildUnified(rgb, driveNorm, centroid); }
        else { BuildSpectrum(rgb, mag, minBin, maxBin); }

        Array.Copy(rgb, _lastFrame, rgb.Length);
        FrameReady?.Invoke(rgb);
    }

    // ----------------------------------------------------------- görsel modlar
    private void BuildUnified(byte[] rgb, float driveNorm, float centroid)
    {
        _uni = driveNorm > _uni ? _uni + (driveNorm - _uni) * _s.Attack
                                : _uni + (driveNorm - _uni) * _s.Decay;
        float v = Math.Clamp(_uni * _s.Brightness, 0f, 1f);
        float t = _s.Scheme == AudioSettings.ColorScheme.RainbowByPosition ? (_hueRot += 0.01f) : centroid;
        (byte r, byte g, byte b) = ColorFor(t, v);
        for (int i = 0; i < _ledCount; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
    }

    private void BuildSpectrum(byte[] rgb, Span<float> mag, int minBin, int maxBin)
    {
        int half = (_ledCount - 1);
        for (int led = 0; led < _ledCount; led++)
        {
            // çıkış konumu -> band fraksiyonu (simetri stiline göre)
            float f = _s.Style switch
            {
                AudioSettings.SpectrumStyle.Mirror => half > 0 ? Math.Abs(led - half / 2f) / (half / 2f) : 0,
                AudioSettings.SpectrumStyle.CenterOut => half > 0 ? 1f - Math.Abs(led - half / 2f) / (half / 2f) : 0,
                _ => half > 0 ? (float)led / half : 0
            };
            f = Math.Clamp(f, 0f, 1f);

            int b0 = (int)(minBin * Math.Pow((double)maxBin / minBin, f));
            int b1 = Math.Max(b0 + 1, (int)(minBin * Math.Pow((double)maxBin / minBin, Math.Min(1f, f + 1f / _ledCount))));
            float peak = 0;
            for (int b = b0; b <= b1 && b <= maxBin; b++) peak = Math.Max(peak, mag[b]);
            if (b0 < (190 * FftSize / _sampleRate)) peak *= _s.BassBoost;

            float gain = _s.AutoGain ? (1f / (_peak + 1e-4f)) : _s.Gain;
            float target = Math.Clamp((float)Math.Pow(peak * gain, _s.Gamma), 0f, 1f);
            _levels[led] = target > _levels[led]
                ? _levels[led] + (target - _levels[led]) * _s.Attack
                : _levels[led] + (target - _levels[led]) * _s.Decay;
            float v = Math.Clamp(_levels[led] * _s.Brightness, 0f, 1f);

            float ct = _s.Scheme == AudioSettings.ColorScheme.RainbowByPosition ? (float)led / _ledCount : f;
            (byte cr, byte cg, byte cb) = ColorFor(ct, v);
            rgb[led * 3] = cr; rgb[led * 3 + 1] = cg; rgb[led * 3 + 2] = cb;
        }
    }

    private void BuildIdle(byte[] rgb)
    {
        float br = _s.Brightness;
        switch (_s.Idle)
        {
            case AudioSettings.IdleMode.Off:
                break; // siyah
            case AudioSettings.IdleMode.KeepLast:
                Array.Copy(_lastFrame, rgb, rgb.Length);
                break;
            case AudioSettings.IdleMode.Breathing:
            {
                float p = (float)((Math.Sin(Environment.TickCount64 / 1200.0) + 1) / 2);
                float v = (0.15f + 0.85f * p) * br;
                Fill(rgb, Scale(_s.IdleR, v), Scale(_s.IdleG, v), Scale(_s.IdleB, v));
                break;
            }
            case AudioSettings.IdleMode.SlowRainbow:
                _hueRot += 0.0015f;
                for (int i = 0; i < _ledCount; i++)
                {
                    (byte r, byte g, byte b) = HsvToRgb((float)i / _ledCount + _hueRot, 1f, 0.5f * br);
                    rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                }
                break;
            default: // DimColor
                Fill(rgb, Scale(_s.IdleR, br), Scale(_s.IdleG, br), Scale(_s.IdleB, br));
                break;
        }
    }

    // ----------------------------------------------------------- renk yardımcı
    private (byte, byte, byte) ColorFor(float t, float v)
    {
        t = Math.Clamp(t, 0f, 1f);
        switch (_s.Scheme)
        {
            case AudioSettings.ColorScheme.FrequencyToHue:
                return HsvToRgb(_s.HueStart + _s.HueSpan * t, 1f, v);
            case AudioSettings.ColorScheme.FixedColor:
                return (Scale(_s.FixR, v), Scale(_s.FixG, v), Scale(_s.FixB, v));
            case AudioSettings.ColorScheme.BassMidTreble:
            {
                (float r, float g, float b) = Blend3(
                    _s.BassR, _s.BassG, _s.BassB, _s.MidR, _s.MidG, _s.MidB, _s.TrebR, _s.TrebG, _s.TrebB, t);
                return (Scale(r, v), Scale(g, v), Scale(b, v));
            }
            case AudioSettings.ColorScheme.TwoColorGradient:
            {
                float r = _s.LowR + (_s.HighR - _s.LowR) * t;
                float g = _s.LowG + (_s.HighG - _s.LowG) * t;
                float b = _s.LowB + (_s.HighB - _s.LowB) * t;
                return (Scale(r, v), Scale(g, v), Scale(b, v));
            }
            default: // RainbowByPosition
                return HsvToRgb(t, 1f, v);
        }
    }

    private static (float, float, float) Blend3(
        float ar, float ag, float ab, float br, float bg, float bb, float cr, float cg, float cb, float t)
    {
        if (t < 0.5f) { float u = t * 2; return (ar + (br - ar) * u, ag + (bg - ag) * u, ab + (bb - ab) * u); }
        float w = (t - 0.5f) * 2; return (br + (cr - br) * w, bg + (cg - bg) * w, bb + (cb - bb) * w);
    }

    private void Fill(byte[] rgb, byte r, byte g, byte b)
    {
        for (int i = 0; i < _ledCount; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
    }

    private static byte Scale(float c, float v) => (byte)Math.Clamp(c * v, 0, 255);

    private static (byte, byte, byte) HsvToRgb(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f * 6f;
        int i = (int)h; float f = h - i;
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public void Dispose() => Stop();
}
