namespace HeatScope.Services;

/// <summary>
/// Оценка вклада процессов в нагрев относительно плавающей базы.
///
/// База — минимальная устойчивая температура CPU/GPU за последние ~15 минут.
/// Вклад процесса = (текущая температура − база) × доля процесса в общей загрузке.
/// Поэтому самая низкая температура принимается за 0 °C, а при остывании
/// значения автоматически пересчитываются вниз.
/// </summary>
public sealed class ThermalModel
{
    private const int MaxSamples = 900;      // ~15 минут при опросе 1 Гц
    private const int MinSamples = 5;        // минимум для оценки базы
    private const double MinRise = 0.2;      // °C — порог шума датчика
    private const double MaxContribution = 60.0;

    private readonly List<Sample> _samples = new(MaxSamples + 1);
    private readonly object _gate = new();

    public int SampleCount { get; private set; }

    public double? CpuBaseline { get; private set; }
    public double? GpuBaseline { get; private set; }
    public double? CpuCurrent { get; private set; }
    public double? GpuCurrent { get; private set; }

    public string QualityText => SampleCount < MinSamples
        ? $"калибровка {SampleCount}/{MinSamples}"
        : "оценка по нагреву";

    public void Add(double cpuLoadPercent, double gpuLoadPercent, double? cpuTemp, double? gpuTemp)
    {
        if (cpuTemp is null && gpuTemp is null) return;
        lock (_gate)
        {
            _samples.Add(new Sample(
                Math.Clamp(cpuLoadPercent, 0, 100) / 100.0,
                Math.Clamp(gpuLoadPercent, 0, 100) / 100.0,
                cpuTemp, gpuTemp));
            if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
            SampleCount = _samples.Count;
            RecomputeBaselines();
        }
    }

    /// <summary>Вклад процесса (в °C) в температуру CPU и GPU.</summary>
    public (double cpu, double gpu) Contribution(double procCpuPercent, double procGpuPercent)
    {
        lock (_gate)
        {
            return (Share(isCpu: true, procCpuPercent), Share(isCpu: false, procGpuPercent));
        }
    }

    private double Share(bool isCpu, double procPercent)
    {
        double? baseline = isCpu ? CpuBaseline : GpuBaseline;
        double? current = isCpu ? CpuCurrent : GpuCurrent;
        if (baseline is null || current is null) return 0;

        double rise = current.Value - baseline.Value;
        if (rise < MinRise) return 0;

        double totalLoadPercent = isCpu
            ? _samples.TakeLast(5).Average(s => s.Cpu) * 100.0
            : _samples.TakeLast(5).Average(s => s.Gpu) * 100.0;

        if (totalLoadPercent < 2.0) return 0;

        double share = Math.Clamp(procPercent / totalLoadPercent, 0, 1);
        return Math.Clamp(rise * share, 0, MaxContribution);
    }

    private void RecomputeBaselines()
    {
        var cpu = new List<double>(_samples.Count);
        var gpu = new List<double>(_samples.Count);
        foreach (var s in _samples)
        {
            if (s.CpuTemp is { } c) cpu.Add(c);
            if (s.GpuTemp is { } g) gpu.Add(g);
        }

        CpuCurrent = cpu.Count > 0 ? cpu[^1] : null;
        GpuCurrent = gpu.Count > 0 ? gpu[^1] : null;
        CpuBaseline = SmoothedMin(cpu);
        GpuBaseline = SmoothedMin(gpu);
    }

    /// <summary>Минимум по сглаженным значениям — чтобы одиночный выброс датчика не сбивал базу.</summary>
    private static double? SmoothedMin(List<double> values)
    {
        if (values.Count == 0) return null;
        if (values.Count < 3) return values.Min();

        double min = double.MaxValue;
        for (int i = 0; i + 2 < values.Count; i++)
        {
            double avg = (values[i] + values[i + 1] + values[i + 2]) / 3.0;
            if (avg < min) min = avg;
        }
        return min == double.MaxValue ? values.Min() : min;
    }

    private readonly record struct Sample(double Cpu, double Gpu, double? CpuTemp, double? GpuTemp);
}
