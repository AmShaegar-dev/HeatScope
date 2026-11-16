using System.Globalization;
using HeatScope.Linux.Models;

namespace HeatScope.Linux.Services;

/// <summary>
/// Датчики Linux: температуры из /sys/class/hwmon, загрузка CPU из /proc/stat,
/// память из /proc/meminfo, загрузка GPU из gpu_busy_percent (amdgpu).
/// </summary>
public sealed class LinuxHardwareSampler : IDisposable
{
    private static readonly HashSet<string> CpuHwNames = new(StringComparer.OrdinalIgnoreCase)
    { "coretemp", "k10temp", "zenpower", "cpu_thermal", "soc_thermal", "acpitz", "it87", "nct6775" };

    private static readonly HashSet<string> GpuHwNames = new(StringComparer.OrdinalIgnoreCase)
    { "amdgpu", "radeon", "nouveau", "i915", "xe", "nvidia" };

    private Thread? _thread;
    private volatile bool _running;
    private long _prevIdle, _prevTotal;

    private string _cpuName = "—";
    private string _gpuName = "—";
    private string? _gpuBusyPath;

    public event Action<HardwareSnapshot>? Updated;

    public void Start()
    {
        if (_running) return;
        DetectStatic();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HeatScope.LinuxHardware" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1500); } catch { }
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Updated?.Invoke(Sample());
            }
            catch (Exception ex)
            {
                double total = 0;
                double used = 0;
                try { used = ReadMem(out total) ?? 0; } catch { }
                Updated?.Invoke(new HardwareSnapshot
                {
                    CpuName = _cpuName,
                    GpuName = _gpuName,
                    IsRoot = Native.IsRoot,
                    Error = ex.Message,
                    RamUsedMb = used,
                    RamTotalMb = total
                });
            }
            Thread.Sleep(1000);
        }
    }

    private void DetectStatic()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0) { _cpuName = line[(idx + 1)..].Trim(); break; }
                }
            }
        }
        catch { }

        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card[0-9]*"))
            {
                string p = Path.Combine(card, "device", "gpu_busy_percent");
                if (File.Exists(p)) { _gpuBusyPath = p; break; }
            }
        }
        catch { }

        try
        {
            foreach (var hw in EnumerateHwmons())
            {
                if (GpuHwNames.Contains(hw.Name)) { _gpuName = hw.Name; break; }
            }
        }
        catch { }
        if (_gpuName == "—" && _gpuBusyPath is not null) _gpuName = "GPU";
    }

    private HardwareSnapshot Sample()
    {
        double? cpuLoad = ReadCpuLoad(out _);
        var temps = ReadTemperatures();

        double? cpuTemp = PickCpuTemp(temps);
        double? gpuTemp = PickGpuTemp(temps);
        double? gpuLoad = ReadGpuLoad();
        double usedMb = ReadMem(out double totalMb) ?? 0;

        return new HardwareSnapshot
        {
            CpuName = _cpuName,
            GpuName = _gpuName,
            CpuTemp = cpuTemp,
            GpuTemp = gpuTemp,
            CpuLoad = cpuLoad,
            GpuLoad = gpuLoad,
            RamUsedMb = usedMb,
            RamTotalMb = totalMb,
            IsRoot = Native.IsRoot,
            Error = null
        };
    }

    // ───────────── /proc/stat ─────────────

    private double? ReadCpuLoad(out double load)
    {
        load = 0;
        try
        {
            string? line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null) return null;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return null;

            long total = 0;
            for (int i = 1; i < parts.Length && i <= 8; i++)
                total += long.Parse(parts[i], CultureInfo.InvariantCulture);

            long idle = long.Parse(parts[4], CultureInfo.InvariantCulture);
            if (parts.Length > 5) idle += long.Parse(parts[5], CultureInfo.InvariantCulture);

            long dTotal = total - _prevTotal;
            long dIdle = idle - _prevIdle;
            _prevTotal = total;
            _prevIdle = idle;

            if (dTotal <= 0) return null;
            load = Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
            return Math.Round(load, 1);
        }
        catch { return null; }
    }

    // ───────────── /sys/class/hwmon ─────────────

    private sealed record Hwmon(string Name, string Path);

    private static IEnumerable<Hwmon> EnumerateHwmons()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/sys/class/hwmon", "hwmon*"); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            string name = ReadText(Path.Combine(dir, "name")) ?? "unknown";
            yield return new Hwmon(name, dir);
        }
    }

    private sealed record TempReading(string Hw, string Label, double Value);

    private static List<TempReading> ReadTemperatures()
    {
        var list = new List<TempReading>();
        foreach (var hw in EnumerateHwmons())
        {
            IEnumerable<string> inputs;
            try { inputs = Directory.EnumerateFiles(hw.Path, "temp*_input"); }
            catch { continue; }

            foreach (var input in inputs)
            {
                string baseName = Path.GetFileNameWithoutExtension(input);
                string? raw = ReadText(input);
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int milli)) continue;
                if (milli <= 0 || milli > 150000) continue;

                string? label = ReadText(Path.Combine(hw.Path, baseName + "_label"));
                list.Add(new TempReading(hw.Name, label ?? baseName, milli / 1000.0));
            }
        }
        return list;
    }

    private static double? PickCpuTemp(List<TempReading> temps)
    {
        var cpu = temps.Where(t => CpuHwNames.Contains(t.Hw)).ToList();
        if (cpu.Count == 0) return null;

        foreach (var key in new[] { "Tctl", "Tdie", "Package", "Core" })
        {
            var hit = cpu.FirstOrDefault(t => t.Label.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return Math.Round(hit.Value, 1);
        }
        return Math.Round(cpu.Max(t => t.Value), 1);
    }

    private static double? PickGpuTemp(List<TempReading> temps)
    {
        var gpu = temps.Where(t => GpuHwNames.Contains(t.Hw)).ToList();
        if (gpu.Count == 0) return null;

        var edge = gpu.FirstOrDefault(t => t.Label.Contains("edge", StringComparison.OrdinalIgnoreCase));
        if (edge is not null) return Math.Round(edge.Value, 1);
        return Math.Round(gpu[0].Value, 1);
    }

    private double? ReadGpuLoad()
    {
        if (_gpuBusyPath is null) return null;
        string? raw = ReadText(_gpuBusyPath);
        return int.TryParse(raw, out int v) ? v : null;
    }

    // ───────────── /proc/meminfo ─────────────

    private static double? ReadMem(out double totalMb)
    {
        totalMb = 0;
        try
        {
            double totalKb = 0, availKb = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availKb = ParseKb(line);
                if (totalKb > 0 && availKb > 0) break;
            }
            totalMb = totalKb / 1024.0;
            double usedKb = Math.Max(0, totalKb - availKb);
            return usedKb / 1024.0;
        }
        catch { return null; }
    }

    private static double ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && double.TryParse(parts[1], out double v) ? v : 0;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }
    }
}
