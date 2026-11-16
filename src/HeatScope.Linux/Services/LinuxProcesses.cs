using System.Globalization;
using HeatScope.Linux.Models;

namespace HeatScope.Linux.Services;

public sealed class ProcInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string? CmdLine { get; init; }
    public string? User { get; init; }
    public int ParentPid { get; init; }
    public int Pgid { get; init; }
    public int Threads { get; init; }
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double RamMb { get; init; }
    public DateTime? StartTime { get; init; }
    public bool IsCritical { get; init; }
    public bool Stopped { get; init; }
    public string State { get; init; } = "";
}

/// <summary>Процессы Linux через /proc: CPU, GPU (drm fdinfo), память, потоки.</summary>
public sealed class LinuxProcessSampler
{
    private sealed class Prev
    {
        public double Ticks;
        public DateTime TimeUtc;
        public double GpuNs;
        public DateTime GpuTimeUtc;
        public string? Path;
        public string? User;
    }

    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);
    private readonly int _clkTck = Native.ClockTicks;
    private readonly int _pageSize = Native.PageSize;
    private readonly Dictionary<int, Prev> _prev = new();
    private readonly Dictionary<uint, string> _users = new();
    private readonly long _bootTime = ReadBootTime();
    private readonly bool _gpuEnabled = Environment.GetEnvironmentVariable("HEATSCOPE_NO_GPU") != "1";
    private readonly Dictionary<int, double> _gpuCache = new();
    private int _sampleIndex;

    public List<ProcInfo> Sample()
    {
        var result = new List<ProcInfo>(256);
        var seen = new HashSet<int>();
        var now = DateTime.UtcNow;
        _sampleIndex++;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc", "*"); }
        catch { return result; }

        foreach (var dir in dirs)
        {
            string name = System.IO.Path.GetFileName(dir);
            if (!int.TryParse(name, out int pid) || pid <= 0) continue;

            try
            {
                if (!TryReadStat(pid, out var st)) continue;
                seen.Add(pid);

                double cpuPct = 0;
                if (!_prev.TryGetValue(pid, out var prev))
                {
                    prev = new Prev { TimeUtc = now, GpuTimeUtc = now };
                    _prev[pid] = prev;
                }
                else
                {
                    double elapsed = (now - prev.TimeUtc).TotalSeconds;
                    if (elapsed > 0.05 && st.Ticks >= prev.Ticks)
                        cpuPct = Math.Clamp((st.Ticks - prev.Ticks) / _clkTck / elapsed / _cores * 100.0, 0, 100);
                }
                prev.Ticks = st.Ticks;
                prev.TimeUtc = now;

                prev.Path ??= ReadLink($"/proc/{pid}/exe");
                prev.User ??= ReadUser(pid);

                double rssMb = ReadRssMb(pid);
                string? cmd = ReadCmdLine(pid);
                int threads = st.Threads;

                double gpuPct = 0;
                if (_gpuEnabled)
                {
                    if (_sampleIndex % 3 == 0)
                    {
                        double ns = ReadGpuNs(pid);
                        if (prev.GpuNs > 0 && ns >= prev.GpuNs)
                        {
                            double elapsed = (now - prev.GpuTimeUtc).TotalSeconds + 1e-6;
                            gpuPct = Math.Clamp((ns - prev.GpuNs) / 1e9 / elapsed * 100.0, 0, 100);
                            _gpuCache[pid] = gpuPct;
                        }
                        prev.GpuNs = ns;
                        prev.GpuTimeUtc = now;
                    }
                    else
                    {
                        gpuPct = _gpuCache.TryGetValue(pid, out var cached) ? cached : 0;
                    }
                }

                bool kernelThread = string.IsNullOrEmpty(cmd) && prev.Path is null;
                bool critical = pid <= 2 || kernelThread || pid == Environment.ProcessId;

                result.Add(new ProcInfo
                {
                    Pid = pid,
                    Name = st.Comm,
                    Path = prev.Path,
                    CmdLine = string.IsNullOrWhiteSpace(cmd) ? null : cmd,
                    User = prev.User,
                    ParentPid = st.ParentPid,
                    Pgid = st.Pgid,
                    Threads = threads,
                    CpuPercent = Math.Round(cpuPct, 1),
                    GpuPercent = Math.Round(gpuPct, 1),
                    RamMb = Math.Round(rssMb, 0),
                    StartTime = st.StartTime,
                    IsCritical = critical,
                    Stopped = st.State is 'T' or 't',
                    State = st.State.ToString()
                });
            }
            catch
            {
                // процесс завершился или нет доступа — пропускаем
            }
        }

        if (_prev.Count > seen.Count + 64)
        {
            foreach (var key in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
                _prev.Remove(key);
        }

        return result;
    }

    // ───────────── /proc/[pid]/stat ─────────────

    private sealed class StatInfo
    {
        public string Comm = "";
        public char State;
        public int ParentPid;
        public int Pgid;
        public double Ticks;
        public int Threads;
        public DateTime? StartTime;
    }

    private bool TryReadStat(int pid, out StatInfo info)
    {
        info = new StatInfo();
        string text;
        try { text = File.ReadAllText($"/proc/{pid}/stat"); }
        catch { return false; }

        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        info.Comm = text[(open + 1)..close];

        var rest = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 22) return false;

        info.State = rest[0][0];
        info.ParentPid = ParseInt(rest[1]);
        info.Pgid = ParseInt(rest[2]);

        double utime = ParseDouble(rest[11]);
        double stime = ParseDouble(rest[12]);
        info.Ticks = utime + stime;

        info.Threads = ParseInt(rest[17]);

        double startTicks = ParseDouble(rest[19]);
        if (_bootTime > 0)
            info.StartTime = DateTimeOffset.FromUnixTimeSeconds(_bootTime + (long)(startTicks / _clkTck)).LocalDateTime;

        return true;
    }

    private static int ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
    private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    // ───────────── прочее ─────────────

    private static long ReadBootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
                if (line.StartsWith("btime ", StringComparison.Ordinal))
                    return long.TryParse(line[6..].Trim(), out long v) ? v : 0;
        }
        catch { }
        return 0;
    }

    private static double ReadRssMb(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[1], out double kb)) return kb / 1024.0;
                }
            }
        }
        catch { }
        return 0;
    }

    private static string? ReadCmdLine(int pid)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
            if (bytes.Length == 0) return null;
            return System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
        }
        catch { return null; }
    }

    private string? ReadUser(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!uint.TryParse(parts[1], out uint uid)) continue;

                if (_users.TryGetValue(uid, out var cached)) return cached;
                string name = LookupUser(uid);
                _users[uid] = name;
                return name;
            }
        }
        catch { }
        return null;
    }

    private static string LookupUser(uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length > 2 && uint.TryParse(parts[2], out uint u) && u == uid)
                    return parts[0];
            }
        }
        catch { }
        return uid.ToString();
    }

    private static double ReadGpuNs(int pid)
    {
        double total = 0;
        try
        {
            string dir = $"/proc/{pid}/fdinfo";
            if (!Directory.Exists(dir)) return 0;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (!line.StartsWith("drm-engine-", StringComparison.Ordinal)) continue;
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string val = line[(colon + 1)..].Replace("ns", "", StringComparison.Ordinal).Trim();
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double ns))
                        total += ns;
                }
            }
        }
        catch { }
        return total;
    }

    private static string? ReadLink(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            if (string.IsNullOrEmpty(target)) return null;
            if (target.EndsWith(" (deleted)", StringComparison.Ordinal))
                target = target[..^10];
            return target;
        }
        catch { return null; }
    }
}
