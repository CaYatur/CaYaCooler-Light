using System.IO;
using System.Text.Json;

namespace CaYaControl;

/// <summary>Kalici tum uygulama durumu (calisma dizinine JSON olarak kaydedilir).</summary>
public sealed class AppState
{
    public AudioSettings Audio { get; set; } = new();
    public int LightMode { get; set; } = 5;     // Windows DL
    public int FanMode { get; set; } = 0;        // 0=oto 1=manuel
    public int FanDuty { get; set; } = 50;
    public int Brightness { get; set; } = 60;    // %
    public int StaticColorArgb { get; set; } = unchecked((int)0xFF0050FF); // (0,80,255)
    public string? PreferredGpu { get; set; }
}

/// <summary>Ayarlari calisma dizinindeki cayacontrol.settings.json dosyasina yazar/okur.</summary>
public static class SettingsStore
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "cayacontrol.settings.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        IncludeFields = true,      // AudioSettings public field kullaniyor
        WriteIndented = true,
    };

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Opt) ?? new AppState();
        }
        catch { /* bozuk dosya -> varsayilan */ }
        return new AppState();
    }

    public static void Save(AppState state)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Opt)); }
        catch { /* yazilamazsa yoksay */ }
    }

    public static string Location => FilePath;
}
