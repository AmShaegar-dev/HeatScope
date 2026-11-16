using System.Diagnostics;
using System.Text.RegularExpressions;
using HeatScope.Models;

namespace HeatScope.Services;

/// <summary>
/// Мягкий режим охлаждения: ставит на паузу (не убивает) самые «горячие»
/// разрешённые процессы, ограничивает максимальное состояние CPU и всё
/// автоматически возвращает через заданное время.
/// </summary>
public sealed class CoolingSession : IDisposable
{
    private static readonly HashSet<string> NeverFreeze = new(StringComparer.OrdinalIgnoreCase)
    {
        "opencode", "opencode-cli", "opencode-pty", "heatscope",
        "explorer", "powershell", "pwsh", "cmd", "conhost",
        "windowsterminal", "openconsole", "bash", "wsl", "wslhost",
        "taskmgr", "sihost", "ctfmon", "dwm"
    };

    private readonly List<int> _pids = new();
    private int _prevMaxAc = 100;
    private int _prevMaxDc = 100;
    private bool _powerChanged;

    public bool Active { get; private set; }
    public DateTime Until { get; private set; }
    public IReadOnlyList<int> Pids => _pids;
    public int CpuLimitPercent { get; private set; }

    public bool Start(IEnumerable<ProcessRow> candidates, TimeSpan duration, out string message, out List<ProcessRow> affected)
    {
        affected = new List<ProcessRow>();
        if (Active)
        {
            message = "Охлаждение уже включено.";
            return false;
        }

        var chosen = candidates
            .Where(r => r.Pid > 4 && r.Pid != Environment.ProcessId)
            .Where(r => !NeverFreeze.Contains(r.Name))
            .Where(r => !r.Suspended)
            .Where(r => ProcessGuard.Check(r).Level == GuardLevel.Allowed)
            .OrderByDescending(r => r.HeatScore)
            .Take(5)
            .ToList();

        if (chosen.Count == 0)
        {
            message = "Нет подходящих процессов для паузы.";
            return false;
        }

        foreach (var row in chosen)
        {
            if (NativeMethods.Suspend(row.Pid, out _))
            {
                _pids.Add(row.Pid);
                affected.Add(row);
            }
        }

        if (_pids.Count == 0)
        {
            message = "Не удалось поставить процессы на паузу (нужны права администратора).";
            return false;
        }

        _powerChanged = TryLimitCpu(out _prevMaxAc, out _prevMaxDc);

        Active = true;
        Until = DateTime.Now + duration;
        message = $"Охлаждение: {_pids.Count} процессов на паузе на {duration.TotalMinutes:0} мин" +
                  (_powerChanged ? ", максимальное состояние CPU ограничено 99%." : ".");
        return true;
    }

    public void Stop()
    {
        foreach (var pid in _pids)
            NativeMethods.Resume(pid, out _);

        _pids.Clear();

        if (_powerChanged)
            RestoreCpu(_prevMaxAc, _prevMaxDc);
        _powerChanged = false;
        CpuLimitPercent = 0;

        Active = false;
    }

    public string RemainingText
    {
        get
        {
            if (!Active) return "";
            var left = Until - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{left.Minutes}:{left.Seconds:00}";
        }
    }

    public void Dispose() => Stop();

    // ───────────── питание процессора ─────────────

    private bool TryLimitCpu(out int prevAc, out int prevDc)
    {
        prevAc = prevDc = 100;
        try
        {
            var (ac, dc) = ReadMaxState();
            prevAc = ac;
            prevDc = dc;

            RunPowerCfg("/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99", 5000);
            RunPowerCfg("/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99", 5000);
            RunPowerCfg("/setactive SCHEME_CURRENT", 5000);

            var (acAfter, _) = ReadMaxState();
            if (acAfter <= 0 || acAfter > 100) return false;
            CpuLimitPercent = 99;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RestoreCpu(int ac, int dc)
    {
        try
        {
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {ac}", 5000);
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {dc}", 5000);
            RunPowerCfg("/setactive SCHEME_CURRENT", 5000);
        }
        catch { }
    }

    private static (int ac, int dc) ReadMaxState()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (100, 100);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var matches = Regex.Matches(output, @"0x([0-9A-Fa-f]{1,8})");
            if (matches.Count < 2) return (100, 100);
            int ac = Convert.ToInt32(matches[^2].Groups[1].Value, 16);
            int dc = Convert.ToInt32(matches[^1].Groups[1].Value, 16);
            return (ac is > 0 and <= 100 ? ac : 100, dc is > 0 and <= 100 ? dc : 100);
        }
        catch
        {
            return (100, 100);
        }
    }

    private static void RunPowerCfg(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(timeoutMs);
        }
        catch { }
    }
}
