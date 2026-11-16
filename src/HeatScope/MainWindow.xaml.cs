using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HeatScope.Models;
using HeatScope.Services;

namespace HeatScope;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly HardwareSampler _hardware = new();
    private readonly ProcessSampler _sampler = new();
    private readonly ThermalModel _model = new();
    private readonly Dictionary<int, ProcessRow> _byPid = new();
    private readonly HashSet<int> _suspended = new();
    private readonly Queue<(double? cpu, double? gpu)> _tempHistory = new();
    private readonly DispatcherTimer _timer;
    private readonly ListCollectionView _view;

    private HardwareSnapshot _snapshot = new();
    private int _tick;
    private bool _sampling;
    private bool _paused;
    private ProcessRow? _selectedRow;
    private string _detailsText = "Выберите процесс в таблице — здесь появятся сведения о нём.";
    private string _statusText = "Готово.";
    private string _statusRightText = "";
    private CancellationTokenSource? _detailsCts;

    public ObservableCollection<ProcessRow> Rows { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        _view.CustomSort = new HeatComparer();
        ProcessGrid.ItemsSource = _view;

        _hardware.SnapshotReady += OnHardwareSnapshot;
        _hardware.Start();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();

        UpdateBanner();
        StatusRightText = "опрос 1 с";
    }

    // ───────────────────────────── данные ─────────────────────────────

    private void OnHardwareSnapshot(HardwareSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _snapshot = snapshot;
            if (snapshot.CpuTemp is not null || snapshot.GpuTemp is not null)
            {
                _tempHistory.Enqueue((snapshot.CpuTemp, snapshot.GpuTemp));
                while (_tempHistory.Count > 30) _tempHistory.Dequeue();
            }
            RefreshHardwareUi();
        }));
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
            RefreshModelUi();

            _tick++;
            if (AutoSort && _tick % 2 == 0) _view.Refresh();

            StatusRightText = $"{stats.Count} процессов · опрос 1 с";
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

    private void UpdateRows(List<ProcessStat> stats)
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
            row.PrivateMb = s.PrivateMb;
            row.Threads = s.Threads;
            row.Handles = s.Handles;
            row.ParentPid = s.ParentPid;
            row.SessionId = s.SessionId;
            row.StartTime = s.StartTime;
            row.WindowTitle = s.WindowTitle;
            row.IsCritical = s.IsCritical;
            row.Path = s.Path ?? row.Path;
            row.Company = s.Company ?? row.Company;
            row.Description = s.Description ?? row.Description;
            row.Suspended = _suspended.Contains(s.Pid);

            var (c, g) = _model.Contribution(s.CpuPercent, s.GpuPercent);
            row.TempCpu = c;
            row.TempGpu = g;
        }

        foreach (var row in _byPid.Values)
        {
            if (row.ParentPid > 0 && _byPid.TryGetValue(row.ParentPid, out var parent))
                row.ParentName = parent.Name;
        }

        foreach (var pid in _byPid.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _suspended.Remove(pid);
            Rows.Remove(_byPid[pid]);
            _byPid.Remove(pid);
        }

        if (SelectedRow is not null && !_byPid.ContainsKey(SelectedRow.Pid))
            SelectedRow = null;
    }

    // ───────────────────────────── UI состояния ─────────────────────────────

    private void RefreshHardwareUi()
    {
        OnPropertyChanged(nameof(CpuTempText));
        OnPropertyChanged(nameof(GpuTempText));
        OnPropertyChanged(nameof(CpuTempBrush));
        OnPropertyChanged(nameof(GpuTempBrush));
        OnPropertyChanged(nameof(CpuName));
        OnPropertyChanged(nameof(GpuName));
        OnPropertyChanged(nameof(CpuLoadText));
        OnPropertyChanged(nameof(GpuLoadText));
        OnPropertyChanged(nameof(CpuPowerText));
        OnPropertyChanged(nameof(GpuPowerText));
        OnPropertyChanged(nameof(RamText));
        UpdateBanner();
    }

    private void RefreshModelUi()
    {
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(ModelBrush));
        OnPropertyChanged(nameof(ModelHint));
    }

    private void UpdateBanner()
    {
        if (!_hardware.IsElevated)
        {
            ShowBanner = true;
            ShowElevate = true;
            ShowInstallDriver = false;
            BannerTitle = "Нет прав администратора";
            BannerText = "Температуры процессора и видеокарты недоступны без прав администратора. " +
                         "Нажмите «Запустить от администратора» — настройки и список процессов продолжат работать.";
            return;
        }

        if (!HardwareSampler.DriverInstalled)
        {
            ShowBanner = true;
            ShowElevate = false;
            ShowInstallDriver = true;
            BannerTitle = "Не установлен драйвер датчиков (PawnIO)";
            BannerText = "LibreHardwareMonitor читает температуры через подписанный драйвер PawnIO. " +
                         "Нажмите «Установить драйвер PawnIO» — скачается официальный установщик. После установки перезапустите HeatScope.";
            return;
        }

        if (_snapshot.Error is { Length: > 0 } error)
        {
            ShowBanner = true;
            ShowElevate = false;
            ShowInstallDriver = false;
            BannerTitle = "Датчики недоступны";
            BannerText = error;
            return;
        }

        ShowBanner = false;
        ShowElevate = false;
        ShowInstallDriver = false;
    }

    private void UpdateStatus(string? text = null)
    {
        if (text is not null) StatusText = text;
    }

    // ───────────────────────────── действия ─────────────────────────────

    private void Kill_Click(object sender, RoutedEventArgs e) => Kill(entireTree: false);

    private void KillTree_Click(object sender, RoutedEventArgs e) => Kill(entireTree: true);

    private void Kill(bool entireTree)
    {
        var row = SelectedRow;
        if (row is null) { UpdateStatus("Сначала выберите процесс."); return; }

        var verdict = ProcessGuard.Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            MessageBox.Show(this, verdict.Reason, "Защита процессов", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus("Заблокировано: " + verdict.Reason);
            return;
        }

        string question = entireTree
            ? $"Завершить «{row.Name}» (PID {row.Pid}) вместе с дочерними процессами?"
            : $"Завершить «{row.Name}» (PID {row.Pid})?";

        if (verdict.Level == GuardLevel.Warning)
            question += "\n\n⚠ " + verdict.Reason;

        if (MessageBox.Show(this, question, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (ProcessGuard.Kill(row, entireTree, out var result))
        {
            _suspended.Remove(row.Pid);
            UpdateStatus(result);
        }
        else
        {
            UpdateStatus(result);
            MessageBox.Show(this, result, "Не удалось", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Suspend_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { UpdateStatus("Сначала выберите процесс."); return; }

        var verdict = ProcessGuard.Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            MessageBox.Show(this, verdict.Reason, "Защита процессов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (verdict.Level == GuardLevel.Warning &&
            MessageBox.Show(this, "Поставить на паузу «" + row.Name + "»?\n\n" + verdict.Reason,
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (row.Suspended) { UpdateStatus($"«{row.Name}» уже на паузе."); return; }

        if (NativeMethods.Suspend(row.Pid, out var error))
        {
            _suspended.Add(row.Pid);
            row.Suspended = true;
            UpdateStatus($"«{row.Name}» (PID {row.Pid}) поставлен на паузу. Процесс не выполняется, память остаётся занятой.");
        }
        else
        {
            UpdateStatus("Не удалось поставить на паузу: " + error);
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { UpdateStatus("Сначала выберите процесс."); return; }
        if (!row.Suspended && !_suspended.Contains(row.Pid))
        {
            UpdateStatus($"«{row.Name}» не находится на паузе.");
            return;
        }

        if (NativeMethods.Resume(row.Pid, out var error))
        {
            _suspended.Remove(row.Pid);
            row.Suspended = false;
            UpdateStatus($"«{row.Name}» (PID {row.Pid}) продолжает работу.");
        }
        else
        {
            UpdateStatus("Не удалось продолжить: " + error);
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null) { UpdateStatus("Сначала выберите процесс."); return; }

        var verdict = ProcessGuard.Check(row);
        if (verdict.Level == GuardLevel.Blocked)
        {
            MessageBox.Show(this, verdict.Reason, "Защита процессов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (verdict.Level == GuardLevel.Warning &&
            MessageBox.Show(this, "Для замера процесс «" + row.Name + "» будет заморожен на 20 секунд.\n\n" + verdict.Reason,
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (row.Suspended) { UpdateStatus("Процесс уже на паузе — сначала снимите паузу."); return; }

        var before = AverageTemps(8);
        if (before.cpu is null && before.gpu is null)
        {
            UpdateStatus("Нет данных о температуре — нужны права администратора и драйвер PawnIO.");
            return;
        }

        if (!NativeMethods.Suspend(row.Pid, out var error))
        {
            UpdateStatus("Не удалось заморозить процесс: " + error);
            return;
        }

        _suspended.Add(row.Pid);
        row.Suspended = true;

        try
        {
            for (int i = 20; i > 0; i--)
            {
                UpdateStatus($"Замер вклада «{row.Name}»: процесс на паузе, осталось {i} с…");
                await Task.Delay(1000);
            }
        }
        finally
        {
            NativeMethods.Resume(row.Pid, out _);
            _suspended.Remove(row.Pid);
            row.Suspended = false;
        }

        var after = AverageTemps(6);
        double? dCpu = before.cpu is not null && after.cpu is not null ? before.cpu - after.cpu : null;
        double? dGpu = before.gpu is not null && after.gpu is not null ? before.gpu - after.gpu : null;

        string msg = $"Процесс: {row.Name} (PID {row.Pid})\n\n" +
                     $"Температура CPU: {before.cpu:0.0} → {after.cpu:0.0} °C   ({(dCpu is null ? "—" : $"{dCpu:+0.0;-0.0;0} °C")})\n" +
                     $"Температура GPU: {before.gpu:0.0} → {after.gpu:0.0} °C   ({(dGpu is null ? "—" : $"{dGpu:+0.0;-0.0;0} °C")})\n\n" +
                     "Положительное значение = столько градусов процесс добавлял к нагреву.\n" +
                     "Учитывайте тепловую инерцию: радиатор остывает не мгновенно, поэтому за 20 секунд видно не весь эффект.";

        MessageBox.Show(this, msg, "Замер вклада в нагрев", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateStatus($"Замер завершён: CPU {dCpu:+0.0;-0.0;0} °C, GPU {dGpu:+0.0;-0.0;0} °C.");
    }

    private (double? cpu, double? gpu) AverageTemps(int count)
    {
        var cpu = _tempHistory.Where(t => t.cpu is not null).Select(t => t.cpu!.Value).TakeLast(count).ToList();
        var gpu = _tempHistory.Where(t => t.gpu is not null).Select(t => t.gpu!.Value).TakeLast(count).ToList();
        return (cpu.Count > 0 ? cpu.Average() : null, gpu.Count > 0 ? gpu.Average() : null);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        UpdateStatus(_paused ? "Опрос приостановлен." : "Опрос возобновлён.");
        OnPropertyChanged(nameof(PauseText));
    }

    private async void RefreshDetails_Click(object sender, RoutedEventArgs e)
    {
        await LoadDetailsAsync(SelectedRow, force: true);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = LoadDetailsAsync(SelectedRow);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is not null) ProcessGrid.ScrollIntoView(SelectedRow);
    }

    private async Task LoadDetailsAsync(ProcessRow? row, bool force = false)
    {
        if (row is null)
        {
            DetailsText = "Выберите процесс в таблице — здесь появятся сведения о нём.";
            return;
        }

        _detailsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailsCts = cts;

        try
        {
            if (!force) await Task.Delay(220, cts.Token);
            var snapshot = _snapshot;
            var text = await Task.Run(() => ProcessDetails.Build(row, snapshot), cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(row, SelectedRow))
                DetailsText = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                DetailsText = "Не удалось получить сведения: " + ex.Message;
        }
    }

    // ───────────────────────────── админ / драйвер ─────────────────────────────

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { UpdateStatus("Не удалось определить путь к программе."); return; }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus("Перезапуск от администратора отменён: " + ex.Message);
        }
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        ShowInstallDriver = false;
        UpdateStatus("Скачиваю установщик PawnIO…");
        try
        {
            const string url = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = await http.GetByteArrayAsync(url);
            var path = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
            await File.WriteAllBytesAsync(path, bytes);
            UpdateStatus("Запускаю установщик PawnIO (подтвердите UAC)…");
            var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            if (p is not null)
            {
                await p.WaitForExitAsync();
                UpdateStatus("Установщик завершён.");
                MessageBox.Show(this,
                    "Если установка прошла успешно — перезапустите HeatScope, чтобы включить датчики температуры.\n\n" +
                    "Приложение перезапустится автоматически после нажатия ОК.",
                    "PawnIO", MessageBoxButton.OK, MessageBoxImage.Information);
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Не удалось установить драйвер: " + ex.Message);
            ShowInstallDriver = HardwareSampler.DriverInstalled is false && _hardware.IsElevated;
            MessageBox.Show(this,
                "Не удалось автоматически скачать установщик.\n\n" +
                "Скачайте вручную: https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe\n\n" +
                ex.Message, "PawnIO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────────────────────── свойства ─────────────────────────────

    public string PauseText => _paused ? "Продолжить опрос" : "Пауза опроса";

    public string AdminBadge => _hardware.IsElevated ? "администратор" : "без прав администратора";

    public bool ShowElevate { get; private set; }
    public bool ShowInstallDriver { get; private set; }
    public bool ShowBanner { get; private set; }
    public string BannerTitle { get; private set; } = "";
    public string BannerText { get; private set; } = "";

    public string CpuTempText => _snapshot.CpuTempText;
    public string GpuTempText => _snapshot.GpuTempText;
    public string CpuLoadText => _snapshot.CpuLoadText;
    public string GpuLoadText => _snapshot.GpuLoadText;
    public string CpuPowerText => _snapshot.CpuPower is { } p ? $"{p:0} Вт" : "—";
    public string GpuPowerText => _snapshot.GpuPower is { } p ? $"{p:0} Вт" : "—";
    public string CpuName => _snapshot.CpuName;
    public string GpuName => _snapshot.GpuName;
    public string RamText => _snapshot.RamText;

    public Brush CpuTempBrush => TempBrush(_snapshot.CpuTemp);
    public Brush GpuTempBrush => TempBrush(_snapshot.GpuTemp);

    public string ModelText => _model.QualityText;
    public Brush ModelBrush =>
        _model.QualityText.Contains("высокая") ? new SolidColorBrush(Color.FromRgb(0x45, 0xD4, 0x83)) :
        _model.QualityText.Contains("средняя") ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)) :
        new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7));

    public string ModelHint => _model.SampleCount < 15
        ? "накапливаю замеры для оценки"
        : (_model.CpuRegressionUsed || _model.GpuRegressionUsed)
            ? "регрессия по замерам"
            : "подъём температуры × доля нагрузки";

    public string DetailsText { get => _detailsText; set { _detailsText = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string StatusRightText { get => _statusRightText; set { _statusRightText = value; OnPropertyChanged(); } }

    public bool AutoSort { get; set; } = true;

    public ProcessRow? SelectedRow
    {
        get => _selectedRow;
        set { if (!ReferenceEquals(_selectedRow, value)) { _selectedRow = value; OnPropertyChanged(); } }
    }

    private static Brush TempBrush(double? temp)
    {
        if (temp is null) return new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7));
        if (temp >= 85) return new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4A));
        if (temp >= 70) return new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
        return new SolidColorBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hardware.SnapshotReady -= OnHardwareSnapshot;
        _hardware.Dispose();
        _sampler.Dispose();
        base.OnClosed(e);
    }

    private sealed class HeatComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ProcessRow a || y is not ProcessRow b) return 0;
            int byHeat = b.HeatScore.CompareTo(a.HeatScore);
            if (byHeat != 0) return byHeat;
            int byCpu = b.CpuPercent.CompareTo(a.CpuPercent);
            if (byCpu != 0) return byCpu;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}




