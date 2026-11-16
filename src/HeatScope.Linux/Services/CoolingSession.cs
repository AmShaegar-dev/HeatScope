using HeatScope.Linux.Models;

namespace HeatScope.Linux.Services;

/// <summary>Мягкое охлаждение: пауза самых горячих разрешённых процессов с авто-возвратом.</summary>
public sealed class CoolingSession
{
    private static readonly HashSet<string> NeverFreeze = new(StringComparer.OrdinalIgnoreCase)
    {
        "heatscope", "systemd", "init", "kthreadd", "bash", "zsh", "fish", "sh",
        "gnome-terminal", "konsole", "xterm", "alacritty", "kitty", "terminator",
        "sshd", "tmux", "screen", "code", "Xorg", "Xwayland"
    };

    private readonly List<int> _pids = new();

    public bool Active { get; private set; }
    public DateTime Until { get; private set; }
    public IReadOnlyList<int> Pids => _pids;

    public bool Start(IEnumerable<ProcessRow> candidates, TimeSpan duration, out string message, out List<ProcessRow> affected)
    {
        affected = new List<ProcessRow>();
        if (Active) { message = "Охлаждение уже включено."; return false; }

        var chosen = candidates
            .Where(r => r.Pid > 2 && r.Pid != Environment.ProcessId)
            .Where(r => !NeverFreeze.Contains(r.Name))
            .Where(r => !r.Suspended)
            .Where(r => LinuxGuard.Check(r).Level == GuardLevel.Allowed)
            .OrderByDescending(r => r.HeatScore)
            .Take(5)
            .ToList();

        if (chosen.Count == 0) { message = "Нет подходящих процессов для паузы."; return false; }

        foreach (var row in chosen)
        {
            if (LinuxGuard.Suspend(row.Pid, out _))
            {
                _pids.Add(row.Pid);
                affected.Add(row);
            }
        }

        if (_pids.Count == 0)
        {
            message = "Не удалось поставить процессы на паузу (нужен root).";
            return false;
        }

        Active = true;
        Until = DateTime.Now + duration;
        message = $"Охлаждение: {_pids.Count} процессов на паузе на {duration.TotalMinutes:0} мин.";
        return true;
    }

    public void Stop()
    {
        foreach (var pid in _pids)
            LinuxGuard.Resume(pid, out _);
        _pids.Clear();
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
}
