using System.Runtime.InteropServices;

namespace HeatScope.Linux.Services;

internal static class Native
{
    public const int SIGKILL = 9;
    public const int SIGCONT = 18;
    public const int SIGSTOP = 19;
    public const int SIGTERM = 15;

    private const int SC_CLK_TCK = 2;
    private const int SC_PAGESIZE = 30;

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    [DllImport("libc")]
    private static extern long sysconf(int name);

    [DllImport("libc")]
    public static extern uint geteuid();

    public static int ClockTicks
    {
        get
        {
            try
            {
                long v = sysconf(SC_CLK_TCK);
                return v > 0 ? (int)v : 100;
            }
            catch { return 100; }
        }
    }

    public static bool IsRoot => geteuid() == 0;

    public static int PageSize
    {
        get
        {
            try
            {
                long v = sysconf(SC_PAGESIZE);
                return v > 0 ? (int)v : 4096;
            }
            catch { return 4096; }
        }
    }
}
