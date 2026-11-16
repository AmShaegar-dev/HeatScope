using System.Diagnostics;
using HeatScope.Models;

namespace HeatScope.Services;

public enum GuardLevel
{
    Allowed,
    Warning,
    Blocked
}

public sealed record GuardVerdict(GuardLevel Level, string Reason);

/// <summary>
/// Защита критических процессов. Блокирует завершение/заморозку того,
/// что уронит Windows (BSOD) или саму программу.
/// </summary>
public static class ProcessGuard
{
    // Завершение этих процессов гарантированно роняет систему или ядро оболочки.
    private static readonly HashSet<string> HardBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "idle", "registry", "memory compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe",
        "msmpeng.exe", "securityhealthservice.exe", "securityhealthsystray.exe",
        "audiodg.exe", "logonui.exe", "wudfhost.exe", "sppsvc.exe",
        "wininit", "smss", "csrss"
    };

    // Завершить можно, но это ломает работу системы/оболочки — требуем подтверждение.
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "explorer.exe", "sihost.exe", "ctfmon.exe", "spoolsv.exe",
        "taskhostw.exe", "runtimebroker.exe", "searchhost.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "textinputhost.exe", "applicationframehost.exe", "dllhost.exe",
        "officeclicktorun.exe", "msiexec.exe", "trustedinstaller.exe",
        "tiworker.exe", "searchindexer.exe", "wmiprvse.exe", "winmgmt.exe"
    };

    public static GuardVerdict Check(ProcessRow? row)
    {
        if (row is null) return new GuardVerdict(GuardLevel.Blocked, "Процесс не выбран.");
        if (row.Pid <= 4)
            return new GuardVerdict(GuardLevel.Blocked, "Системный процесс ядра — завершение вызовет BSOD.");
        if (row.Pid == Environment.ProcessId)
            return new GuardVerdict(GuardLevel.Blocked, "Это сама HeatScope.");
        if (row.IsCritical || NativeMethods.IsBreakOnTermination(row.Pid))
            return new GuardVerdict(GuardLevel.Blocked, "Windows помечает процесс как критический (BreakOnTermination).");
        if (HardBlocked.Contains(row.Name) || HardBlocked.Contains(row.Name + ".exe"))
            return new GuardVerdict(GuardLevel.Blocked, "Критический компонент Windows — защищён.");
        if (Dangerous.Contains(row.Name) || Dangerous.Contains(row.Name + ".exe"))
            return new GuardVerdict(GuardLevel.Warning, "Системный/оболочечный процесс: система может зависнуть или перезапустить его.");
        return new GuardVerdict(GuardLevel.Allowed, "");
    }

    public static bool Kill(ProcessRow row, bool entireTree, out string result)
    {
        var verdict = Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            result = "Запрещено: " + verdict.Reason;
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(row.Pid);
            p.Kill(entireTree);
            result = entireTree
                ? $"Процесс {row.Name} (PID {row.Pid}) и его дочерние завершены."
                : $"Процесс {row.Name} (PID {row.Pid}) завершён.";
            return true;
        }
        catch (Exception ex)
        {
            result = $"Не удалось завершить {row.Name}: {ex.Message}";
            return false;
        }
    }
}
