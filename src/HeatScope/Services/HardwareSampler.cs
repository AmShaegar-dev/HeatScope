using System.Runtime.InteropServices;
using System.Security.Principal;
using HeatScope.Models;
using LibreHardwareMonitor.Hardware;

namespace HeatScope.Services;

/// <summary>
/// Опрос датчиков температуры/нагрузки через LibreHardwareMonitor.
/// Поддерживает AMD/Intel CPU и AMD/NVIDIA/Intel GPU.
/// Требует драйвер PawnIO и права администратора.
/// </summary>
public sealed class HardwareSampler : IDisposable
{
    private readonly object _gate = new();
    private Computer? _computer;
    private Thread? _thread;
    private volatile bool _running;

    public string? LastError { get; private set; }
    public bool IsElevated { get; } = CheckElevated();

    public static bool DriverInstalled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public event Action<HardwareSnapshot>? SnapshotReady;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HeatScope.Hardware" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1500); } catch { }
        CloseComputer();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                var snap = Sample();
                SnapshotReady?.Invoke(snap);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SnapshotReady?.Invoke(new HardwareSnapshot
                {
                    DriverAvailable = DriverInstalled,
                    IsElevated = IsElevated,
                    Error = ex.Message,
                    RamUsedMb = GetUsedMb(),
                    RamTotalMb = GetTotalMb()
                });
            }
            Thread.Sleep(1000);
        }
    }

    private void EnsureComputer()
    {
        if (_computer is not null) return;
        var c = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false
        };
        c.Open();
        _computer = c;
    }

    private HardwareSnapshot Sample()
    {
        EnsureComputer();
        var computer = _computer!;

        UpdateRecursive(computer.Hardware);

        IHardware? cpu = null;
        IHardware? gpu = null;
        var allTemps = new List<SensorReading>();

        foreach (var hw in computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu && cpu is null) cpu = hw;
            if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel && gpu is null) gpu = hw;

            CollectTemps(hw, allTemps);
        }

        double? cpuTemp = PickTemp(cpu,
            "Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Package", "Core Average", "Core Max", "CPU Core");
        double? gpuTemp = PickTemp(gpu, "GPU Core", "GPU Temp", "Core");
        double? gpuHot = PickTemp(gpu, "GPU Hot Spot", "Hotspot");
        double? cpuLoad = PickSensor(cpu, SensorType.Load, "CPU Total", "Total");
        double? gpuLoad = PickSensor(gpu, SensorType.Load, "GPU Core", "Core");
        double? cpuPower = PickSensor(cpu, SensorType.Power, "Package", "CPU Package");
        double? gpuPower = PickSensor(gpu, SensorType.Power, "GPU Package", "Package");

        return new HardwareSnapshot
        {
            CpuName = cpu?.Name ?? "—",
            GpuName = gpu?.Name ?? "—",
            CpuTemp = cpuTemp,
            GpuTemp = gpuTemp,
            GpuHotspot = gpuHot,
            CpuLoad = cpuLoad,
            GpuLoad = gpuLoad,
            CpuPower = cpuPower,
            GpuPower = gpuPower,
            RamUsedMb = GetUsedMb(),
            RamTotalMb = GetTotalMb(),
            DriverAvailable = DriverInstalled,
            IsElevated = IsElevated,
            AllTemps = allTemps,
            Error = null
        };
    }

    private static void UpdateRecursive(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            try { hw.Update(); } catch { }
            if (hw.SubHardware.Length > 0) UpdateRecursive(hw.SubHardware);
        }
    }

    private static void CollectTemps(IHardware hw, List<SensorReading> list)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature && s.Value is { } v && v > 0 && v < 150)
            {
                list.Add(new SensorReading
                {
                    Hardware = hw.Name,
                    Name = s.Name,
                    Type = "Temperature",
                    Value = Math.Round(v, 1),
                    Unit = "°C"
                });
            }
        }
        foreach (var sub in hw.SubHardware) CollectTemps(sub, list);
    }

    private static double? PickTemp(IHardware? hw, params string[] preferred)
    {
        if (hw is null) return null;
        var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        if (temps.Count == 0) return null;
        foreach (var name in preferred)
        {
            var hit = temps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? temps.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is { } v) return Math.Round(v, 1);
        }
        return Math.Round(temps[0].Value!.Value, 1);
    }

    private static double? PickSensor(IHardware? hw, SensorType type, params string[] preferred)
    {
        if (hw is null) return null;
        var list = hw.Sensors.Where(s => s.SensorType == type && s.Value is not null).ToList();
        if (list.Count == 0) return null;
        foreach (var name in preferred)
        {
            var hit = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is { } v) return Math.Round(v, 1);
        }
        return Math.Round(list[0].Value!.Value, 1);
    }

    private void CloseComputer()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    public void Dispose() => Stop();

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private static (double usedMb, double totalMb) GetMemory()
    {
        try
        {
            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m))
            {
                double total = m.ullTotalPhys / 1024.0 / 1024.0;
                double used = (m.ullTotalPhys - m.ullAvailPhys) / 1024.0 / 1024.0;
                return (used, total);
            }
        }
        catch { }
        return (0, 0);
    }

    private static double GetUsedMb() => GetMemory().usedMb;
    private static double GetTotalMb() => GetMemory().totalMb;
}
