using System.Windows;
using System.Windows.Threading;
using WinPulse.App.Infrastructure;
using WinPulse.App.ViewModels;
using WinPulse.Collectors;
using WinPulse.Core;
using WinPulse.Core.Demo;
using WinPulse.Core.Model;
using WinPulse.Optimizer.Changes;

namespace WinPulse.App;

public partial class App : Application
{
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        // Вспомогательный режим с правами администратора: выполнить пачку изменений и выйти без окон.
        int opsIndex = Array.IndexOf(args, "--elevated-ops");
        if (opsIndex >= 0)
        {
            int code = opsIndex + 1 < args.Length ? ElevatedHost.Run(args[opsIndex + 1]) : 2;
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Write("FATAL", ev.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("task", ev.Exception);
            ev.SetObserved();
        };
        Diag.Message += m => Log.Write("DIAG", m);

        var settings = AppSettings.Load();
        Lang.English = settings.Language == "en";
        bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        Log.Info($"Запуск: {Environment.OSVersion}, {(demo ? "демо-режим" : "обычный режим")}, администратор: {OsInfo.IsElevated()}");

        ISystemSource source;
        try
        {
            source = demo ? new DemoSystemSource() : new WindowsSystemSource();
        }
        catch (Exception ex)
        {
            Log.Error("source", ex);
            MessageBox.Show("Не удалось подключиться к счётчикам Windows, запускаю демо-режим.\n\n" + ex.Message,
                "WinPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            source = new DemoSystemSource();
        }

        var engine = new MonitorEngine(source, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        _vm = new MainViewModel(engine, settings);
        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _vm.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _vm?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("exit", ex);
        }
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        MessageBox.Show(
            "Произошла ошибка. Она записана в журнал, программа продолжит работу.\n\n" + e.Exception.Message +
            "\n\nЖурнал: " + Log.FilePath,
            "WinPulse", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
