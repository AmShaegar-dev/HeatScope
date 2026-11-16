using System.Runtime.InteropServices;

namespace HeatScope.Services;

/// <summary>P/Invoke для операций над процессами и сетевой таблицы Windows.</summary>
internal static class NativeMethods
{
    public const int PROCESS_SUSPEND_RESUME = 0x0800;
    public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>Процесс помечен как критический для системы (его завершение вызовет BSOD).</summary>
    public static bool IsBreakOnTermination(int pid)
    {
        if (pid <= 4) return true;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            int value = 0;
            int status = NtQueryInformationProcess(h, 29 /* ProcessBreakOnTermination */, ref value, sizeof(int), out _);
            return status == 0 && value != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool Suspend(int pid, out string? error)
    {
        error = null;
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
        {
            error = "Нет доступа (нужны права администратора).";
            return false;
        }
        try
        {
            int st = NtSuspendProcess(h);
            if (st != 0) error = $"NtSuspendProcess вернул 0x{st:X8}";
            return st == 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool Resume(int pid, out string? error)
    {
        error = null;
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
        {
            error = "Нет доступа (нужны права администратора).";
            return false;
        }
        try
        {
            int st = NtResumeProcess(h);
            if (st != 0) error = $"NtResumeProcess вернул 0x{st:X8}";
            return st == 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
