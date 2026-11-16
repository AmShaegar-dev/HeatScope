namespace HeatScope.Services;

/// <summary>
/// Оценка вклада процессов в нагрев.
///
/// Основной режим — линейная регрессия T = a0 + a1*CPU + a2*GPU по скользящему окну.
/// Если корреляция слабая (мало нагрузки, шум), включается устойчивая оценка:
/// фактический подъём температуры над минимумом окна распределяется между
/// процессами пропорционально их доле в общей загрузке.
/// </summary>
public sealed class ThermalModel
{
    private const int MaxSamples = 240;        // ~4 минуты при опросе 1 Гц
    private const int MinSamples = 15;
    private const double GoodR2 = 0.25;        // порог доверия к регрессии
    private const double MaxCoeff = 80.0;      // °C на 100% нагрузки (защита от выбросов)
    private const double MaxContribution = 60.0;

    private readonly List<Sample> _samples = new(MaxSamples + 1);
    private readonly object _gate = new();

    public int SampleCount { get; private set; }
    public bool CpuReady { get; private set; }
    public bool GpuReady { get; private set; }
    public double R2Cpu { get; private set; }
    public double R2Gpu { get; private set; }

    // T_cpu = Ac0 + AcCpu*loadCpu + AcGpu*loadGpu   (нагрузки 0..1)
    public double Ac0 { get; private set; }
    public double AcCpu { get; private set; }
    public double AcGpu { get; private set; }

    // T_gpu = Ag0 + AgCpu*loadCpu + AgGpu*loadGpu
    public double Ag0 { get; private set; }
    public double AgCpu { get; private set; }
    public double AgGpu { get; private set; }

    public bool CpuRegressionUsed { get; private set; }
    public bool GpuRegressionUsed { get; private set; }

    public string QualityText
    {
        get
        {
            if (SampleCount < MinSamples) return $"калибровка {SampleCount}/{MinSamples}";
            if (CpuRegressionUsed || GpuRegressionUsed)
            {
                double r2 = Math.Max(CpuRegressionUsed ? R2Cpu : 0, GpuRegressionUsed ? R2Gpu : 0);
                string q = r2 >= 0.6 ? "высокая" : r2 >= 0.35 ? "средняя" : "умеренная";
                return $"модель: {q} (R² {r2:0.00})";
            }
            return "оценка по нагреву";
        }
    }

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
            Refit();
        }
    }

    /// <summary>Вклад процесса (в °C) в температуру CPU и GPU.</summary>
    public (double cpu, double gpu) Contribution(double procCpuPercent, double procGpuPercent)
    {
        lock (_gate)
        {
            double c = Math.Clamp(procCpuPercent, 0, 100) / 100.0;
            double g = Math.Clamp(procGpuPercent, 0, 100) / 100.0;

            double cpu;
            if (CpuRegressionUsed)
                cpu = Math.Clamp(AcCpu, 0, MaxCoeff) * c + Math.Clamp(AcGpu, 0, MaxCoeff) * g;
            else
                cpu = ShareEstimate(isCpu: true, procCpuPercent);

            double gpu;
            if (GpuRegressionUsed)
                gpu = Math.Clamp(AgCpu, 0, MaxCoeff) * c + Math.Clamp(AgGpu, 0, MaxCoeff) * g;
            else
                gpu = ShareEstimate(isCpu: false, procGpuPercent);

            return (Math.Clamp(cpu, 0, MaxContribution), Math.Clamp(gpu, 0, MaxContribution));
        }
    }

    /// <summary>
    /// Устойчивая оценка: подъём температуры над минимумом окна × доля процесса
    /// в текущей общей загрузке. Не требует хорошей корреляции.
    /// </summary>
    private double ShareEstimate(bool isCpu, double procPercent)
    {
        var values = new List<double>(_samples.Count);
        foreach (var s in _samples)
        {
            double? v = isCpu ? s.CpuTemp : s.GpuTemp;
            if (v is not null) values.Add(v.Value);
        }
        if (values.Count < 3) return 0;

        double baseline = values.Min();
        double current = values[^1];
        double rise = Math.Max(0, current - baseline);
        if (rise < 0.2) return 0;

        double totalLoadPercent = isCpu
            ? _samples.TakeLast(5).Average(s => s.Cpu) * 100.0
            : _samples.TakeLast(5).Average(s => s.Gpu) * 100.0;

        if (totalLoadPercent < 2.0) return 0;
        double share = Math.Clamp(procPercent / totalLoadPercent, 0, 1);
        return rise * share;
    }

    private void Refit()
    {
        double[]? bCpu = Fit(Sample.TargetCpu);
        if (bCpu is null)
        {
            CpuReady = false;
            CpuRegressionUsed = false;
        }
        else
        {
            Ac0 = bCpu[0];
            AcCpu = Math.Max(0, bCpu[1]);
            AcGpu = Math.Max(0, bCpu[2]);
            CpuReady = true;
            R2Cpu = R2(Sample.TargetCpu, bCpu);
            CpuRegressionUsed = R2Cpu >= GoodR2 && AcCpu <= MaxCoeff && AcGpu <= MaxCoeff;
        }

        double[]? bGpu = Fit(Sample.TargetGpu);
        if (bGpu is null)
        {
            GpuReady = false;
            GpuRegressionUsed = false;
        }
        else
        {
            Ag0 = bGpu[0];
            AgCpu = Math.Max(0, bGpu[1]);
            AgGpu = Math.Max(0, bGpu[2]);
            GpuReady = true;
            R2Gpu = R2(Sample.TargetGpu, bGpu);
            GpuRegressionUsed = R2Gpu >= GoodR2 && AgCpu <= MaxCoeff && AgGpu <= MaxCoeff;
        }
    }

    private double[]? Fit(Func<Sample, double?> target)
    {
        var rows = _samples.Where(s => target(s) is not null).ToList();
        if (rows.Count < MinSamples) return null;

        int n = rows.Count;
        double[,] a = new double[3, 3];
        double[] rhs = new double[3];
        double lambda = 1e-3 * n;

        foreach (var s in rows)
        {
            double[] x = { 1.0, s.Cpu, s.Gpu };
            double y = target(s)!.Value;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) a[i, j] += x[i] * x[j];
                rhs[i] += x[i] * y;
            }
        }
        for (int i = 0; i < 3; i++) a[i, i] += lambda;

        return Solve3(a, rhs);
    }

    private double R2(Func<Sample, double?> target, double[] b)
    {
        var rows = _samples.Where(s => target(s) is not null).ToList();
        if (rows.Count < 2) return 0;
        double mean = rows.Average(s => target(s)!.Value);
        double sse = 0, sst = 0;
        foreach (var s in rows)
        {
            double y = target(s)!.Value;
            double pred = b[0] + b[1] * s.Cpu + b[2] * s.Gpu;
            sse += (y - pred) * (y - pred);
            sst += (y - mean) * (y - mean);
        }
        return sst <= 1e-9 ? 0 : Math.Clamp(1 - sse / sst, 0, 1);
    }

    private static double[]? Solve3(double[,] a, double[] b)
    {
        double[,] m = new double[3, 4];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i, j] = a[i, j];
            m[i, 3] = b[i];
        }

        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;

            if (pivot != col)
                for (int j = col; j < 4; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

            double div = m[col, col];
            for (int j = col; j < 4; j++) m[col, j] /= div;

            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                if (Math.Abs(f) < 1e-15) continue;
                for (int j = col; j < 4; j++) m[r, j] -= f * m[col, j];
            }
        }

        var result = new[] { m[0, 3], m[1, 3], m[2, 3] };
        foreach (var v in result)
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        return result;
    }

    private readonly record struct Sample(double Cpu, double Gpu, double? CpuTemp, double? GpuTemp)
    {
        public static double? TargetCpu(Sample s) => s.CpuTemp;
        public static double? TargetGpu(Sample s) => s.GpuTemp;
    }
}
