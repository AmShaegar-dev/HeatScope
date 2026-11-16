namespace HeatScope.Linux.Models;

public sealed class HardwareSnapshot
{
    public string CpuName { get; init; } = "—";
    public string GpuName { get; init; } = "—";

    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? CpuLoad { get; init; }
    public double? GpuLoad { get; init; }
    public double? CpuPower { get; init; }

    public double RamUsedMb { get; init; }
    public double RamTotalMb { get; init; }

    public bool IsRoot { get; init; }
    public string? Error { get; init; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public string CpuTempText => CpuTemp is { } t ? t.ToString("0.0") + " °C" : "—";
    public string GpuTempText => GpuTemp is { } t ? t.ToString("0.0") + " °C" : "—";
    public string CpuLoadText => CpuLoad is { } l ? l.ToString("0") + " %" : "—";
    public string GpuLoadText => GpuLoad is { } l ? l.ToString("0") + " %" : "—";
    public string CpuPowerText => CpuPower is { } p ? $"{p:0} Вт" : "—";
    public string RamText => RamTotalMb > 0 ? $"{RamUsedMb / 1024:0.0} / {RamTotalMb / 1024:0.0} ГБ" : "—";
}
