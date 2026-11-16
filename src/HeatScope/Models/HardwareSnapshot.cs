namespace HeatScope.Models;

public sealed class SensorReading
{
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public double Value { get; init; }
    public string Unit { get; init; } = "";
}

public sealed class HardwareSnapshot
{
    public string CpuName { get; init; } = "—";
    public string GpuName { get; init; } = "—";

    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotspot { get; init; }

    public double? CpuLoad { get; init; }
    public double? GpuLoad { get; init; }

    public double? CpuPower { get; init; }
    public double? GpuPower { get; init; }

    public double RamUsedMb { get; init; }
    public double RamTotalMb { get; init; }

    public bool DriverAvailable { get; init; }
    public bool IsElevated { get; init; }
    public string? Error { get; init; }

    public IReadOnlyList<SensorReading> AllTemps { get; init; } = Array.Empty<SensorReading>();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public string CpuTempText => CpuTemp is { } t ? t.ToString("0.0") + " °C" : "—";
    public string GpuTempText => GpuTemp is { } t ? t.ToString("0.0") + " °C" : "—";
    public string CpuLoadText => CpuLoad is { } l ? l.ToString("0") + " %" : "—";
    public string GpuLoadText => GpuLoad is { } l ? l.ToString("0") + " %" : "—";
    public string RamText => RamTotalMb > 0 ? $"{RamUsedMb / 1024:0.0} / {RamTotalMb / 1024:0.0} ГБ" : "—";
}
