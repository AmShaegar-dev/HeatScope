using System.Diagnostics;

namespace HeatScope.Services;

public sealed class ProcessStat
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double RamMb { get; init; }
    public double PrivateMb { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public DateTime? StartTime { get; init; }
    public int ParentPid { get; init; }
    public int SessionId { get; init; }
    public string? Path { get; init; }
    public string? Company { get; init; }
    public string? Description { get; init; }
    public string? WindowTitle { get; init; }
    public bool IsCritical { get; init; }
}

/// <summary>Сбор статистики по всем процессам: CPU, GPU, RAM, потоки, издатель.</summary>
public sealed class ProcessSampler : IDisposable
{
    private sealed class Prev
    {
        public TimeSpan Cpu;
        public DateTime TimeUtc;
        public string? Path;
        public string? Company;
        public string? Description;
    }

    private readonly Dictionary<int, Prev> _prev = new();
    private readonly Dictionary<int, bool> _criticalCache = new();
    private readonly GpuUsageProvider _gpu = new();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);

    public List<ProcessStat> Sample()
    {
        var gpu = _gpu.Read();
        var result = new List<ProcessStat>(256);
        var now = DateTime.UtcNow;
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int pid = p.Id;
                seen.Add(pid);
                double cpuPct = 0;

                TimeSpan totalCpu;
                try { totalCpu = p.TotalProcessorTime; }
                catch { totalCpu = TimeSpan.Zero; }

                if (_prev.TryGetValue(pid, out var prev))
                {
                    double elapsed = (now - prev.TimeUtc).TotalSeconds;
                    if (elapsed > 0.05 && totalCpu >= prev.Cpu)
                    {
                        double used = (totalCpu - prev.Cpu).TotalSeconds;
                        cpuPct = Math.Clamp(used / elapsed / _cores * 100.0, 0, 100);
                    }
                }

                if (!_prev.TryGetValue(pid, out prev))
                {
                    prev = new Prev();
                    _prev[pid] = prev;
                }
                prev.Cpu = totalCpu;
                prev.TimeUtc = now;

                string? path = prev.Path;
                string? company = prev.Company;
                string? description = prev.Description;
                if (path is null)
                {
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path is not null)
                    {
                        prev.Path = path;
                        try
                        {
                            var vi = FileVersionInfo.GetVersionInfo(path);
                            company = vi.CompanyName;
                            description = vi.FileDescription ?? vi.ProductName;
                        }
                        catch { }
                        prev.Company = company;
                        prev.Description = description;
                    }
                }

                if (!_criticalCache.TryGetValue(pid, out bool critical))
                {
                    critical = NativeMethods.IsBreakOnTermination(pid);
                    _criticalCache[pid] = critical;
                }

                double ram = 0;
                try { ram = p.WorkingSet64 / 1024.0 / 1024.0; } catch { }
                double priv = 0;
                try { priv = p.PrivateMemorySize64 / 1024.0 / 1024.0; } catch { }
                int threads = 0;
                try { threads = p.Threads.Count; } catch { }
                int handles = 0;
                try { handles = p.HandleCount; } catch { }
                DateTime? start = null;
                try { start = p.StartTime; } catch { }
                int session = 0;
                try { session = p.SessionId; } catch { }
                string? title = null;
                try { title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { }

                gpu.TryGetValue(pid, out double gpuPct);
                result.Add(new ProcessStat
                {
                    Pid = pid,
                    Name = p.ProcessName,
                    CpuPercent = Math.Round(cpuPct, 1),
                    GpuPercent = Math.Round(gpuPct, 1),
                    RamMb = Math.Round(ram, 0),
                    PrivateMb = Math.Round(priv, 0),
                    Threads = threads,
                    Handles = handles,
                    StartTime = start,
                    SessionId = session,
                    Path = path,
                    Company = company,
                    Description = description,
                    WindowTitle = title,
                    IsCritical = critical
                });
            }
            catch
            {
                // процесс мог завершиться между вызовами — пропускаем
            }
            finally
            {
                p.Dispose();
            }
        }

        // очистка кэшей от исчезнувших процессов
        if (_prev.Count > seen.Count + 32)
        {
            foreach (var key in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(key);
            foreach (var key in _criticalCache.Keys.Where(k => !seen.Contains(k)).ToList()) _criticalCache.Remove(key);
        }

        return result;
    }

    public void Dispose() => _gpu.Dispose();
}
