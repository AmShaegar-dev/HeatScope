using System.Text;
using HeatScope.Linux.Models;

namespace HeatScope.Linux.Services;

/// <summary>Подробности о процессе для Linux: /proc, cwd, окружение, cgroup.</summary>
public static class LinuxDetails
{
    public static string Build(ProcessRow row)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ {row.Name}   PID {row.Pid} ═══");
        sb.AppendLine();
        sb.AppendLine($"Пользователь: {row.UserName ?? "—"}");
        sb.AppendLine($"Путь:         {row.Path ?? "—"}");
        sb.AppendLine($"Запущен:      {(row.StartTime is { } st ? st.ToString("dd.MM.yyyy HH:mm:ss") : "—")}");
        sb.AppendLine($"Родитель:     {(row.ParentPid > 0 ? $"{row.ParentName ?? "?"} (PID {row.ParentPid})" : "—")}");
        sb.AppendLine($"Группа:       {row.Pgid}");
        sb.AppendLine($"Потоки:       {row.Threads}");
        sb.AppendLine($"Память:       {row.RamMb:0} МБ (RSS)");
        sb.AppendLine($"Загрузка:     CPU {row.CpuPercent:0.0}%   GPU {row.GpuPercent:0.0}%");
        sb.AppendLine($"Вклад:        CPU {row.TempCpu:+0.0;-0.0;0} °C, GPU {row.TempGpu:+0.0;-0.0;0} °C");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(row.CommandLine))
        {
            sb.AppendLine("Командная строка:");
            sb.AppendLine("  " + row.CommandLine);
            sb.AppendLine();
        }

        string? cwd = ReadLink($"/proc/{row.Pid}/cwd");
        if (cwd is not null) sb.AppendLine($"Рабочая папка: {cwd}");

        string? root = ReadLink($"/proc/{row.Pid}/root");
        if (root is not null) sb.AppendLine($"Корень:        {root}");

        string? cgroup = ReadCgroup(row.Pid);
        if (cgroup is not null) sb.AppendLine($"cgroup:        {cgroup}");

        int fds = CountFds(row.Pid);
        if (fds >= 0) sb.AppendLine($"Открытых файлов: {fds}");

        sb.AppendLine();
        sb.AppendLine("Что это значит:");
        sb.AppendLine("  " + Explain(row));
        return sb.ToString();
    }

    private static string Explain(ProcessRow row)
    {
        var parts = new List<string>();
        string name = row.Name.ToLowerInvariant();
        string path = (row.Path ?? "").ToLowerInvariant();

        if (name is "systemd" or "init" or "kthreadd") parts.Add("ядро/init — трогать нельзя");
        else if (row.IsCritical) parts.Add("поток ядра или критический процесс");
        else if (path.Contains("/usr/lib/systemd/") || path.Contains("/lib/systemd/")) parts.Add("служба systemd");
        else if (path.StartsWith("/usr/") || path.StartsWith("/opt/")) parts.Add("системная программа");
        else if (path.StartsWith("/snap/") || path.Contains("/flatpak/")) parts.Add("пакет Snap/Flatpak");
        else if (path.StartsWith("/home/")) parts.Add("пользовательская программа");
        else parts.Add("фоновый процесс");

        if (row.UserName is not null) parts.Add($"работает от «{row.UserName}»");
        if (row.CpuPercent >= 1) parts.Add($"грузит CPU на {row.CpuPercent:0.#}%");
        if (row.GpuPercent >= 1) parts.Add($"использует GPU на {row.GpuPercent:0.#}%");
        if (row.TempCpu + row.TempGpu >= 0.5) parts.Add($"добавляет ~{row.TempCpu + row.TempGpu:0.0} °C к нагреву");
        if (row.Suspended) parts.Add("стоит на паузе (SIGSTOP)");

        return string.Join("; ", parts) + ".";
    }

    private static string? ReadCgroup(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cgroup").Trim().Split('\n')[0].Trim(); }
        catch { return null; }
    }

    private static int CountFds(int pid)
    {
        try { return Directory.EnumerateFiles($"/proc/{pid}/fd").Count(); }
        catch { return -1; }
    }

    private static string? ReadLink(string path)
    {
        try
        {
            var t = new FileInfo(path).LinkTarget;
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch { return null; }
    }
}
