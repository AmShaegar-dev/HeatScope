using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HeatScope.Models;

namespace HeatScope.Services;

/// <summary>Собирает подробную информацию о процессе: чем он является и что делает.</summary>
public static class ProcessDetails
{
    public static string Build(ProcessRow row, HardwareSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var (cmdLine, wmiPath, parentPid) = QueryWmi(row.Pid);
        var services = QueryServices(row.Pid);

        sb.AppendLine($"═══ {row.Name}   PID {row.Pid} ═══");
        sb.AppendLine();
        sb.AppendLine($"Издатель:   {row.Company ?? "неизвестен"}");
        sb.AppendLine($"Описание:   {row.Description ?? "—"}");
        sb.AppendLine($"Путь:       {wmiPath ?? row.Path ?? "нет доступа"}");
        sb.AppendLine($"Запущен:    {(row.StartTime is { } st ? st.ToString("dd.MM.yyyy HH:mm:ss") : "—")}");
        sb.AppendLine($"Родитель:   {(row.ParentPid > 0 ? $"{row.ParentName ?? "?"} (PID {row.ParentPid})" : "—")}");
        sb.AppendLine($"Сессия:     {row.SessionId}");
        sb.AppendLine($"Потоки:     {row.Threads}    Дескрипторы: {row.Handles}");
        sb.AppendLine($"Память:     {row.RamMb:0} МБ (рабочий набор), {row.PrivateMb:0} МБ (приватная)");
        sb.AppendLine();
        sb.AppendLine($"Загрузка:   CPU {row.CpuPercent:0.0}%   GPU {row.GpuPercent:0.0}%");
        sb.AppendLine($"Вклад в нагрев: CPU {row.TempCpu:+0.0;-0.0;0} °C, GPU {row.TempGpu:+0.0;-0.0;0} °C");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(cmdLine))
        {
            sb.AppendLine("Командная строка:");
            sb.AppendLine("  " + cmdLine);
            sb.AppendLine();
        }

        if (services.Count > 0)
        {
            sb.AppendLine("Службы в этом процессе:");
            foreach (var s in services) sb.AppendLine("  • " + s);
            sb.AppendLine();
        }

        var signature = GetSignature(row.Path);
        if (signature is not null)
        {
            sb.AppendLine("Цифровая подпись:");
            sb.AppendLine("  " + signature);
            sb.AppendLine();
        }

        var net = NetworkProvider.GetForPid(row.Pid);
        if (net.Count > 0)
        {
            sb.AppendLine($"Сетевые соединения ({net.Count}):");
            foreach (var c in net.Take(12))
                sb.AppendLine($"  {c.Proto}  {c.Local}  →  {c.Remote}   [{c.State}]");
            if (net.Count > 12) sb.AppendLine($"  … ещё {net.Count - 12}");
            sb.AppendLine();
        }

        sb.AppendLine("Что это значит:");
        sb.AppendLine("  " + Explain(row, services.Count, net.Count, cmdLine));
        return sb.ToString();
    }

    private static string Explain(ProcessRow row, int services, int netCount, string? cmdLine)
    {
        var parts = new List<string>();
        string name = row.Name.ToLowerInvariant();
        string path = (row.Path ?? "").ToLowerInvariant();

        if (services > 0) parts.Add($"фоновый хост служб Windows ({services}) — обслуживает систему, не имеет окна");
        else if (name is "system" or "registry" or "smss" or "csrss" or "wininit" or "winlogon" or "services" or "lsass" or "dwm" or "fontdrvhost")
            parts.Add("ядро/базовая подсистема Windows — трогать нельзя");
        else if (path.Contains(@"\windows\system32") || path.Contains(@"\windows\syswow64")) parts.Add("системный компонент Windows");
        else if (path.Contains(@"\windows\") && path.Contains("explorer.exe")) parts.Add("оболочка рабочего стола");
        else if (!string.IsNullOrEmpty(row.Company)) parts.Add($"приложение «{row.Company}»");
        else parts.Add("сторонняя программа");

        if (row.WindowTitle is not null) parts.Add($"есть окно «{row.WindowTitle}»");
        if (netCount > 0) parts.Add($"держит {netCount} сетевых соединений");
        if (row.CpuPercent >= 1) parts.Add($"сейчас грузит CPU на {row.CpuPercent:0.#}%");
        if (row.GpuPercent >= 1) parts.Add($"использует GPU на {row.GpuPercent:0.#}%");
        if (row.TempCpu + row.TempGpu >= 0.5) parts.Add($"добавляет ~{row.TempCpu + row.TempGpu:0.0} °C к нагреву");
        if (row.IsCritical) parts.Add("помечен Windows как критический");
        if (!string.IsNullOrEmpty(cmdLine) && cmdLine.Contains("--service")) parts.Add("запущен как служба");

        return string.Join("; ", parts) + ".";
    }

    private static (string? cmdLine, string? path, int parentPid) QueryWmi(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ExecutablePath, ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                string? cmd = mo["CommandLine"] as string;
                string? path = mo["ExecutablePath"] as string;
                int parent = mo["ParentProcessId"] is { } v ? Convert.ToInt32(v) : 0;
                return (cmd, path, parent);
            }
        }
        catch { }
        return (null, null, 0);
    }

    private static List<string> QueryServices(int pid)
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, DisplayName, State FROM Win32_Service WHERE ProcessId={pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? "?";
                string display = mo["DisplayName"] as string ?? "";
                string state = mo["State"] as string ?? "";
                list.Add($"{display} [{name}] — {state}");
            }
        }
        catch { }
        return list;
    }

    private static string? GetSignature(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            bool trusted = chain.Build(cert);
            return $"{cert.Subject}\n  Действительна до {cert.NotAfter:dd.MM.yyyy}   {(trusted ? "✔ цепочка доверия в порядке" : "⚠ цепочка не подтверждена")}";
        }
        catch
        {
            return "нет цифровой подписи";
        }
    }
}
