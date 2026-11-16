using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HeatScope.Linux.Models;
using HeatScope.Linux.Services;

namespace HeatScope.Linux;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LinuxHardwareSampler _hardware = new();
    private readonly LinuxProcessSampler _sampler = new();
    private readonly ThermalModel _model = new();
    private readonly CoolingSession _cooling = new();
    private readonly Dictionary<int, ProcessRow> _byPid = new();
    private readonly HashSet<int> _suspended = new();
    private readonly DispatcherTimer _timer;

    private HardwareSnapshot _snapshot = new();
    private int _tick;
    private bool _sampling;
    private bool _paused;
    private ProcessRow? _selectedRow;
    private string _detailsText = "Выберите процесс в таблице — здесь появятся сведения о нём.";
    private string _statusText = "Готово.";
    private string _statusRightText = "";

    private (int pid, string action, DateTime until)? _pending;

    public ObservableCollection<ProcessRow> Rows { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _hardware.Updated += OnHardwareSnapshot;
        _hardware.Start();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();

        StatusRightText = "опрос 1 с";
    }

    private void OnHardwareSnapshot(HardwareSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _snapshot = snapshot;
            OnPropertyChanged(nameof(CpuTempText));
            OnPropertyChanged(nameof(GpuTempText));
            OnPropertyChanged(nameof(CpuTempBrush));
            OnPropertyChanged(nameof(GpuTempBrush));
            OnPropertyChanged(nameof(CpuName));
            OnPropertyChanged(nameof(GpuName));
            OnPropertyChanged(nameof(CpuLoadText));
            OnPropertyChanged(nameof(GpuLoadText));
            OnPropertyChanged(nameof(CpuPowerText));
            OnPropertyChanged(nameof(RamText));
            OnPropertyChanged(nameof(AdminBadge));
        });
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_paused || _sampling) return;
        _sampling = true;
        try
        {
            var stats = await Task.Run(() => _sampler.Sample());
            _model.Add(_snapshot.CpuLoad ?? 0, _snapshot.GpuLoad ?? 0, _snapshot.CpuTemp, _snapshot.GpuTemp);
            UpdateRows(stats);

            OnPropertyChanged(nameof(ModelText));
            OnPropertyChanged(nameof(ModelBrush));
            OnPropertyChanged(nameof(ModelHint));

            if (_cooling.Active && DateTime.Now >= _cooling.Until)
                StopCooling("Автоохлаждение завершено — процессы возобновлены.");

            _tick++;
            StatusRightText = _cooling.Active
                ? $"{stats.Count} процессов · охлаждение ещё {_cooling.RemainingText}"
                : $"{stats.Count} процессов · опрос 1 с";
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка опроса: " + ex.Message;
        }
        finally
        {
            _sampling = false;
        }
    }

    private void UpdateRows(List<ProcInfo> stats)
    {
        var seen = new HashSet<int>();
        foreach (var s in stats)
        {
            seen.Add(s.Pid);
            if (!_byPid.TryGetValue(s.Pid, out var row))
            {
                row = new ProcessRow { Pid = s.Pid, Name = s.Name };
                _byPid[s.Pid] = row;
                Rows.Add(row);
            }

            row.CpuPercent = s.CpuPercent;
            row.GpuPercent = s.GpuPercent;
            row.RamMb = s.RamMb;
            row.Threads = s.Threads;
            row.ParentPid = s.ParentPid;
            row.Pgid = s.Pgid;
            row.StartTime = s.StartTime;
            row.Path = s.Path ?? row.Path;
            row.CommandLine = s.CmdLine;
            row.UserName = s.User;
            row.IsCritical = s.IsCritical;
            row.Suspended = s.Stopped || _suspended.Contains(s.Pid);

            var (c, g) = _model.Contribution(s.CpuPercent, s.GpuPercent);
            row.TempCpu = c;
            row.TempGpu = g;
        }

        foreach (var row in _byPid.Values)
            if (row.ParentPid > 0 && _byPid.TryGetValue(row.ParentPid, out var parent))
                row.ParentName = parent.Name;

        foreach (var pid in _byPid.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _suspended.Remove(pid);
            Rows.Remove(_byPid[pid]);
            _byPid.Remove(pid);
        }

        if (SelectedRow is not null && !_byPid.ContainsKey(SelectedRow.Pid))
            SelectedRow = null;
    }

    // ───────────── действия ─────────────

    private void Kill_Click(object? sender, RoutedEventArgs e) => Kill(entireTree: false);

    private void KillTree_Click(object? sender, RoutedEventArgs e) => Kill(entireTree: true);

    private void Kill(bool entireTree)
    {
        var row = SelectedRow;
        if (row is null) { StatusText = "Сначала выберите процесс."; return; }

        var verdict = LinuxGuard.Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            StatusText = "Заблокировано: " + verdict.Reason;
            return;
        }

        string action = entireTree ? "kill-tree" : "kill";
        if (verdict.Level == GuardLevel.Warning && !Confirm(row.Pid, action))
        {
            StatusText = $"⚠ {verdict.Reason} Нажмите кнопку ещё раз в течение 5 секунд, чтобы подтвердить.";
            return;
        }

        if (LinuxGuard.Kill(row, entireTree, force: false, out var result))
        {
            _suspended.Remove(row.Pid);
            StatusText = result;
        }
        else
        {
            StatusText = result;
        }
    }

    private void Suspend_Click(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { StatusText = "Сначала выберите процесс."; return; }

        var verdict = LinuxGuard.Check(row);
        if (verdict.Level == GuardLevel.Blocked) { StatusText = "Заблокировано: " + verdict.Reason; return; }
        if (verdict.Level == GuardLevel.Warning && !Confirm(row.Pid, "stop"))
        {
            StatusText = $"⚠ {verdict.Reason} Нажмите «Пауза» ещё раз для подтверждения.";
            return;
        }

        if (LinuxGuard.Suspend(row.Pid, out var error))
        {
            _suspended.Add(row.Pid);
            row.Suspended = true;
            StatusText = $"«{row.Name}» (PID {row.Pid}) поставлен на паузу (SIGSTOP).";
        }
        else StatusText = "Не удалось поставить на паузу: " + error;
    }

    private void Resume_Click(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { StatusText = "Сначала выберите процесс."; return; }

        if (LinuxGuard.Resume(row.Pid, out var error))
        {
            _suspended.Remove(row.Pid);
            row.Suspended = false;
            StatusText = $"«{row.Name}» (PID {row.Pid}) продолжает работу (SIGCONT).";
        }
        else StatusText = "Не удалось продолжить: " + error;
    }

    private void Cool_Click(object? sender, RoutedEventArgs e)
    {
        if (_cooling.Active)
        {
            StopCooling("Охлаждение выключено — процессы возобновлены.");
            return;
        }

        if (_cooling.Start(Rows, TimeSpan.FromMinutes(5), out var message, out var affected))
        {
            foreach (var row in affected)
            {
                _suspended.Add(row.Pid);
                row.Suspended = true;
            }
            OnPropertyChanged(nameof(CoolText));
            StatusText = message + " Через 5 минут всё вернётся автоматически.";
        }
        else StatusText = message;
    }

    private void StopCooling(string message)
    {
        var pids = _cooling.Pids.ToList();
        _cooling.Stop();
        foreach (var pid in pids)
        {
            _suspended.Remove(pid);
            if (_byPid.TryGetValue(pid, out var row)) row.Suspended = false;
        }
        OnPropertyChanged(nameof(CoolText));
        StatusText = message;
    }

    private bool Confirm(int pid, string action)
    {
        var now = DateTime.Now;
        if (_pending is { } p && p.pid == pid && p.action == action && now < p.until)
        {
            _pending = null;
            return true;
        }
        _pending = (pid, action, now.AddSeconds(5));
        return false;
    }

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        StatusText = _paused ? "Опрос приостановлен." : "Опрос возобновлён.";
        OnPropertyChanged(nameof(PauseText));
    }

    private void RefreshDetails_Click(object? sender, RoutedEventArgs e) => LoadDetails(SelectedRow);

    private void LoadDetails(ProcessRow? row)
    {
        if (row is null)
        {
            DetailsText = "Выберите процесс в таблице — здесь появятся сведения о нём.";
            return;
        }

        try { DetailsText = LinuxDetails.Build(row); }
        catch (Exception ex) { DetailsText = "Не удалось получить сведения: " + ex.Message; }
    }

    // ───────────── свойства ─────────────

    public string PauseText => _paused ? "Возобновить опрос" : "Остановить опрос";
    public string CoolText => _cooling.Active ? "Прекратить охлаждение" : "Охладить (5 мин)";
    public string AdminBadge => Native.IsRoot ? "root" : "обычный пользователь";

    public bool ShowBanner => !Native.IsRoot;
    public string BannerTitle => "Нет прав root";
    public string BannerText =>
        "Часть данных и управление чужими процессами доступны только от root. " +
        "Запустите приложение через sudo или pkexec — тогда появятся температуры и сигналы.";

    public string CpuTempText => _snapshot.CpuTempText;
    public string GpuTempText => _snapshot.GpuTempText;
    public string CpuLoadText => _snapshot.CpuLoadText;
    public string GpuLoadText => _snapshot.GpuLoadText;
    public string CpuPowerText => _snapshot.CpuPowerText;
    public string CpuName => _snapshot.CpuName;
    public string GpuName => _snapshot.GpuName;
    public string RamText => _snapshot.RamText;

    public IBrush CpuTempBrush => TempBrush(_snapshot.CpuTemp);
    public IBrush GpuTempBrush => TempBrush(_snapshot.GpuTemp);

    public string ModelText => _model.QualityText;
    public IBrush ModelBrush =>
        _model.QualityText.Contains("высокая") ? Brush("#45D483") :
        _model.QualityText.Contains("средняя") ? Brush("#FFB020") : Brush("#8B93A7");

    public string ModelHint => _model.SampleCount < 15
        ? "накапливаю замеры для оценки"
        : (_model.CpuRegressionUsed || _model.GpuRegressionUsed)
            ? "регрессия по замерам"
            : "подъём температуры × доля нагрузки";

    public string DetailsText { get => _detailsText; set { _detailsText = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string StatusRightText { get => _statusRightText; set { _statusRightText = value; OnPropertyChanged(); } }

    public ProcessRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            OnPropertyChanged();
            LoadDetails(value);
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static IBrush TempBrush(double? temp)
    {
        if (temp is null) return Brush("#8B93A7");
        if (temp >= 85) return Brush("#FF5A4A");
        if (temp >= 70) return Brush("#FFB020");
        return Brush("#45D483");
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _cooling.Stop();
        _hardware.Stop();
        base.OnClosed(e);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
