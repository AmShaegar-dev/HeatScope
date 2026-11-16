using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HeatScope.Models;

public sealed class ProcessRow : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; set; }
    public string? Company { get; set; }
    public string? Description { get; set; }
    public int ParentPid { get; set; }
    public string? ParentName { get; set; }
    public DateTime? StartTime { get; set; }
    public bool IsCritical { get; set; }
    public string? CriticalReason { get; set; }
    public int SessionId { get; set; }
    public string? WindowTitle { get; set; }

    private double _cpu;
    private double _gpu;
    private double _ram;
    private double _private;
    private int _threads;
    private int _handles;
    private double _tempCpu;
    private double _tempGpu;
    private bool _suspended;
    private string _status = "работает";

    public double CpuPercent { get => _cpu; set { if (Set(ref _cpu, value)) { OnPropertyChanged(nameof(CpuText)); OnPropertyChanged(nameof(HeatScore)); } } }
    public double GpuPercent { get => _gpu; set { if (Set(ref _gpu, value)) { OnPropertyChanged(nameof(GpuText)); OnPropertyChanged(nameof(HeatScore)); } } }
    public double RamMb { get => _ram; set { if (Set(ref _ram, value)) OnPropertyChanged(nameof(RamText)); } }
    public double PrivateMb { get => _private; set => Set(ref _private, value); }
    public int Threads { get => _threads; set => Set(ref _threads, value); }
    public int Handles { get => _handles; set => Set(ref _handles, value); }

    public double TempCpu { get => _tempCpu; set { if (Set(ref _tempCpu, value)) { OnPropertyChanged(nameof(TempCpuText)); OnPropertyChanged(nameof(HeatScore)); } } }
    public double TempGpu { get => _tempGpu; set { if (Set(ref _tempGpu, value)) { OnPropertyChanged(nameof(TempGpuText)); OnPropertyChanged(nameof(HeatScore)); } } }

    public bool Suspended { get => _suspended; set { if (Set(ref _suspended, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); } } }
    public string Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

    public double HeatScore => TempCpu + TempGpu;

    public string CpuText => CpuPercent >= 0.05 ? CpuPercent.ToString("0.0") + "%" : "—";
    public string GpuText => GpuPercent >= 0.05 ? GpuPercent.ToString("0.0") + "%" : "—";
    public string RamText => RamMb >= 1 ? RamMb.ToString("0") : "—";
    public string TempCpuText => TempCpu > 0.05 ? "+" + TempCpu.ToString("0.0") : "—";
    public string TempGpuText => TempGpu > 0.05 ? "+" + TempGpu.ToString("0.0") : "—";
    public string TempTotalText => HeatScore > 0.05 ? "+" + HeatScore.ToString("0.0") + " °C" : "—";
    public string StatusText => Suspended ? "пауза" : (_status == "работает" ? "" : _status);
    public string StatusBrush => Suspended ? "#FFB020" : "#8A91A3";
    public string NameWithPid => $"{Name} ({Pid})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
