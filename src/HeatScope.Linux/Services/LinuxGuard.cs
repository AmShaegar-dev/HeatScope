using System.Runtime.InteropServices;
using HeatScope.Linux.Models;

namespace HeatScope.Linux.Services;

public enum GuardLevel { Allowed, Warning, Blocked }

public sealed record GuardVerdict(GuardLevel Level, string Reason);

/// <summary>Защита критических процессов Linux и управление сигналами.</summary>
public static class LinuxGuard
{
    private static readonly HashSet<string> HardBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "init", "kthreadd", "kworker", "ksoftirqd", "migration", "rcu_gp",
        "rcu_par_gp", "watchdog", "kauditd", "kcompactd", "kswapd", "oom_reaper",
        "systemd-journald", "systemd-udevd", "kdevtmpfs", "netns", "kthread"
    };

    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-shell", "plasmashell", "kwin_x11", "kwin_wayland", "Xorg", "Xwayland",
        "dbus-daemon", "dbus-broker", "systemd-logind", "NetworkManager", "pipewire",
        "wireplumber", "pulseaudio", "sshd", "gdm", "sddm", "lightdm", "gnome-session-binary"
    };

    public static GuardVerdict Check(ProcessRow? row)
    {
        if (row is null) return new GuardVerdict(GuardLevel.Blocked, "Процесс не выбран.");
        if (row.Pid <= 2) return new GuardVerdict(GuardLevel.Blocked, "PID 1/2 — ядро или init, завершение уронит систему.");
        if (row.Pid == Environment.ProcessId) return new GuardVerdict(GuardLevel.Blocked, "Это сама HeatScope.");
        if (row.IsCritical) return new GuardVerdict(GuardLevel.Blocked, "Критический процесс или поток ядра.");
        if (HardBlocked.Contains(row.Name)) return new GuardVerdict(GuardLevel.Blocked, "Критический системный компонент — защищён.");
        if (Dangerous.Contains(row.Name)) return new GuardVerdict(GuardLevel.Warning, "Системный/оболочечный процесс: рабочий стол или сессия могут зависнуть.");
        return new GuardVerdict(GuardLevel.Allowed, "");
    }

    public static bool Suspend(int pid, out string? error) => Signal(pid, Native.SIGSTOP, out error);
    public static bool Resume(int pid, out string? error) => Signal(pid, Native.SIGCONT, out error);

    public static bool Signal(int pid, int sig, out string? error)
    {
        error = null;
        try
        {
            if (Native.kill(pid, sig) == 0) return true;
            int errno = Marshal.GetLastWin32Error();
            error = errno == 1 ? "Нет прав (запустите от root)." : $"errno {errno}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Kill(ProcessRow row, bool entireTree, bool force, out string result)
    {
        var verdict = Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            result = "Запрещено: " + verdict.Reason;
            return false;
        }

        int sig = force ? Native.SIGKILL : Native.SIGTERM;
        int target = entireTree && row.Pgid > 0 && row.Pgid != Environment.ProcessId ? -row.Pgid : row.Pid;

        if (!Signal(target, sig, out var error))
        {
            result = $"Не удалось отправить сигнал {row.Name} (PID {row.Pid}): {error}";
            return false;
        }

        result = force
            ? $"Отправлен SIGKILL: {row.Name} (PID {row.Pid})" + (entireTree ? " и группа процессов." : ".")
            : $"Отправлен SIGTERM: {row.Name} (PID {row.Pid})" + (entireTree ? " и группа процессов." : ".");

        if (!force)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (Directory.Exists($"/proc/{row.Pid}"))
                    Signal(target, Native.SIGKILL, out _);
            });
        }

        return true;
    }
}
