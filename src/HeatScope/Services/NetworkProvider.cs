using System.Net;
using System.Runtime.InteropServices;

namespace HeatScope.Services;

public sealed record NetConnection(string Proto, string Local, string Remote, string State);

/// <summary>Сетевые соединения процесса через IP Helper API (без внешних утилит).</summary>
public static class NetworkProvider
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    private static readonly Dictionary<int, string> States = new()
    {
        [1] = "CLOSED", [2] = "LISTEN", [3] = "SYN_SENT", [4] = "SYN_RCVD",
        [5] = "ESTABLISHED", [6] = "FIN_WAIT1", [7] = "FIN_WAIT2", [8] = "CLOSE_WAIT",
        [9] = "CLOSING", [10] = "LAST_ACK", [11] = "TIME_WAIT", [12] = "DELETE_TCB"
    };

    public static List<NetConnection> GetForPid(int pid)
    {
        var list = new List<NetConnection>();
        ReadTable(AF_INET, pid, list);
        ReadTable(AF_INET6, pid, list);
        return list;
    }

    private static void ReadTable(int af, int pid, List<NetConnection> list)
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return;

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(ptr, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) return;

            int count = Marshal.ReadInt32(ptr);
            int rowSize = af == AF_INET ? 24 : 56;
            IntPtr row = IntPtr.Add(ptr, 4);

            for (int i = 0; i < count; i++, row = IntPtr.Add(row, rowSize))
            {
                int owningPid;
                string local, remote;
                int state;

                if (af == AF_INET)
                {
                    uint localAddr = (uint)Marshal.ReadInt32(row, 4);
                    uint localPort = (uint)Marshal.ReadInt32(row, 8);
                    uint remoteAddr = (uint)Marshal.ReadInt32(row, 12);
                    uint remotePort = (uint)Marshal.ReadInt32(row, 16);
                    state = Marshal.ReadInt32(row, 0);
                    owningPid = Marshal.ReadInt32(row, 20);

                    local = $"{new IPAddress(BitConverter.GetBytes(localAddr))}:{SwapPort(localPort)}";
                    remote = $"{new IPAddress(BitConverter.GetBytes(remoteAddr))}:{SwapPort(remotePort)}";
                }
                else
                {
                    byte[] la = new byte[16], ra = new byte[16];
                    Marshal.Copy(row, la, 0, 16);
                    Marshal.Copy(IntPtr.Add(row, 24), ra, 0, 16);
                    uint localPort = (uint)Marshal.ReadInt32(row, 20);
                    uint remotePort = (uint)Marshal.ReadInt32(row, 44);
                    int localScope = Marshal.ReadInt32(row, 16);
                    state = Marshal.ReadInt32(row, 48);
                    owningPid = Marshal.ReadInt32(row, 52);

                    local = $"[{new IPAddress(la, localScope)}]:{SwapPort(localPort)}";
                    remote = $"[{new IPAddress(ra)}]:{SwapPort(remotePort)}";
                }

                if (owningPid != pid) continue;
                list.Add(new NetConnection(af == AF_INET ? "TCP" : "TCP6", local, remote,
                    States.TryGetValue(state, out var s) ? s : state.ToString()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static int SwapPort(uint value)
    {
        return (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
    }
}
