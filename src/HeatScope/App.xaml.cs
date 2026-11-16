using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using HeatScope.Services;

namespace HeatScope;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var log = Path.Combine(AppContext.BaseDirectory, "heatscope-error.log");
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {args.Exception}\r\n\r\n");
            }
            catch { }
            MessageBox.Show(args.Exception.Message, "HeatScope — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        bool noElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        if (!noElevate && !IsElevated())
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = "--elevated"
                    });
                    Shutdown();
                    return;
                }
            }
            catch
            {
                // пользователь отклонил UAC — продолжаем без прав администратора
            }
        }

        HardwareSampler.Prepare();

        new MainWindow().Show();
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
