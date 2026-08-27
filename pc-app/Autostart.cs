using System.Diagnostics;

namespace CaYaControl;

/// <summary>
/// Yonetici hakkiyla calisan uygulamalar Run anahtarindan UAC nedeniyle sessizce
/// baslayamaz; bu yuzden "en yuksek ayricalik" ile bir Zamanlanmis Gorev kullaniyoruz.
/// </summary>
public static class Autostart
{
    private const string TaskName = "CaYaControl";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void Set(bool enable)
    {
        try
        {
            string exe = Application.ExecutablePath;
            string args = enable
                ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F"
                : $"/Delete /TN \"{TaskName}\" /F";
            var psi = new ProcessStartInfo("schtasks", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(3000);
        }
        catch { /* yoksay */ }
    }
}
