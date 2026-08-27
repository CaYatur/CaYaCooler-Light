using System.Text;
using LibreHardwareMonitor.Hardware;

namespace CaYaControl;

/// <summary>CPU ve GPU sicakligini LibreHardwareMonitor ile okur.</summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    /// <summary>En son hangi sensorun secildigi (teshis icin).</summary>
    public string LastCpuSensor { get; private set; } = "-";
    public string LastGpuSensor { get; private set; } = "-";

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
        };
        _computer.Open();
    }

    /// <summary>Kullanicinin sectigi GPU adi (null = otomatik, NVIDIA oncelikli).</summary>
    public string? PreferredGpuName { get; set; }

    /// <summary>(cpu, gpu) sicakligi (C). Okunamazsa -1 doner.</summary>
    public (int cpu, int gpu) Read()
    {
        int cpu = -1, gpu = -1;
        try
        {
            _computer.Accept(_visitor);
            IHardware? chosenGpu = null;
            int chosenScore = -1;
            foreach (IHardware hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    var (val, name) = PickBest(hw, ScoreCpu);
                    if (val is not null) { cpu = (int)Math.Round(val.Value); LastCpuSensor = name; }
                }
                else if (IsGpu(hw.HardwareType))
                {
                    if (PreferredGpuName != null)
                    {
                        if (hw.Name == PreferredGpuName) chosenGpu = hw;
                    }
                    else
                    {
                        int s = GpuTypeScore(hw.HardwareType);  // NVIDIA > AMD > Intel
                        if (s > chosenScore) { chosenScore = s; chosenGpu = hw; }
                    }
                }
            }
            if (chosenGpu != null)
            {
                var (val, name) = PickBest(chosenGpu, ScoreGpu);
                if (val is not null) { gpu = (int)Math.Round(val.Value); LastGpuSensor = $"{chosenGpu.Name}: {name}"; }
            }
        }
        catch { /* surucu/erisim hatasi -> -1 birak */ }
        return (cpu, gpu);
    }

    /// <summary>Tum GPU adlari (UI listesi icin).</summary>
    public List<string> GpuNames()
    {
        var list = new List<string>();
        foreach (IHardware hw in _computer.Hardware)
            if (IsGpu(hw.HardwareType)) list.Add(hw.Name);
        return list;
    }

    private static bool IsGpu(HardwareType t) =>
        t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static int GpuTypeScore(HardwareType t) => t switch
    {
        HardwareType.GpuNvidia => 3,   // ayrik (discrete) -> oncelik
        HardwareType.GpuAmd => 2,
        HardwareType.GpuIntel => 1,
        _ => 0
    };

    // Sensor adina gore oncelik puani. Yuksek = daha guvenilir "ana" sicaklik.
    private static int ScoreCpu(string n) =>
        n.Contains("tctl") ? 100 :
        n.Contains("cpu package") ? 95 :
        n.Contains("package") ? 90 :
        n.Contains("tdie") ? 80 :          // CCD'ye ait olabilir, ana sensorden sonra
        n.Contains("core (") ? 70 :
        n.Contains("core max") ? 60 :
        n.Contains("core average") ? 50 :
        n.Contains("core") ? 40 : 10;

    private static int ScoreGpu(string n) =>
        n.Contains("gpu core") ? 100 :
        n.Contains("core") ? 80 :
        n.Contains("hot") ? 40 : 10;

    private static (float? value, string name) PickBest(IHardware hw, Func<string, int> score)
    {
        float? best = null; string bestName = "-"; int bestScore = -1;
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is null) continue;
            int sc = score(s.Name.ToLowerInvariant());
            if (sc > bestScore) { bestScore = sc; best = s.Value; bestName = s.Name; }
        }
        return (best, bestName);
    }

    /// <summary>Tum donanim ve sicaklik sensorlerini metin olarak dokumler (teshis).</summary>
    public string DumpSensors()
    {
        var sb = new StringBuilder();
        try
        {
            _computer.Accept(_visitor);
            foreach (IHardware hw in _computer.Hardware)
            {
                sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                bool any = false;
                foreach (ISensor s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    any = true;
                    string v = s.Value is null ? "(deger yok - yonetici hakki?)" : $"{s.Value:0.#} C";
                    sb.AppendLine($"    {s.Name,-28} {v}");
                }
                if (!any) sb.AppendLine("    (sicaklik sensoru yok)");
                sb.AppendLine();
            }
            sb.AppendLine($"Secilen CPU sensoru : {LastCpuSensor}");
            sb.AppendLine($"Secilen GPU sensoru : {LastGpuSensor}");
        }
        catch (Exception ex) { sb.AppendLine("Hata: " + ex.Message); }
        return sb.ToString();
    }

    public void Dispose() => _computer.Close();

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
