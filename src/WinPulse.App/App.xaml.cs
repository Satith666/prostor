using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    /// <summary>Режим снимков экрана для сборки на GitHub: без окон с сообщениями.</summary>
    private bool _headless;

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
        int shotsIndex = Array.FindIndex(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        string? shotsDir = shotsIndex >= 0 && shotsIndex + 1 < args.Length ? args[shotsIndex + 1] : null;
        _headless = shotsDir is not null;
        if (shotsDir is not null) Mark(shotsDir, "startup");
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
        if (shotsDir is not null) Mark(shotsDir, "before show");
        window.Show();
        if (shotsDir is not null) Mark(shotsDir, "shown");
        _vm.Start();
        if (shotsDir is not null)
        {
            Mark(shotsDir, "started");
            _ = ScreenshotTourAsync(window, _vm, shotsDir);
        }
    }

    /// <summary>
    /// Обходит все страницы и сохраняет их снимки в PNG — чтобы проверять дизайн по сборке на GitHub, где нет живого экрана.
    /// Интерфейс отрисовывается вне окна в фиксированном размере: экран у сборочной машины меньше окна программы.
    /// </summary>
    private static void Mark(string dir, string text)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "progress.txt"), $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch
        {
            // Метки — только для диагностики.
        }
    }

    private static async Task ScreenshotTourAsync(Window window, MainViewModel vm, string dir)
    {
        Directory.CreateDirectory(dir);
        Mark(dir, "tour begin");
        var size = new Size(1320, 900);
        try
        {
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            root.DataContext = vm;
            void Layout()
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
            }

            Layout();
            Mark(dir, "first layout");
            await Task.Delay(10000);
            Mark(dir, "warmed up");
            int n = 0;
            foreach (var item in vm.Nav.ToList())
            {
                if (item.Soon) continue;
                Mark(dir, "page " + item.Key);
                item.IsSelected = true;
                Layout();
                bool slow = item.Key is "problems" or "drivers" or "startup" or "windowed" or "optimize";
                await Task.Delay(slow ? 8000 : 3000);
                Layout();
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(dir, $"{++n:00}-{item.Key}.png"));
                encoder.Save(file);
                Mark(dir, "saved " + item.Key);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "tour-error.txt"), ex.ToString());
        }
        finally
        {
            try
            {
                File.Copy(Log.FilePath, Path.Combine(dir, "winpulse.log"), true);
            }
            catch
            {
                // Журнала может не быть — не страшно.
            }
            window.Close();
        }
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
        if (_headless)
        {
            e.Handled = true;
            return;
        }
        MessageBox.Show(
            "Произошла ошибка. Она записана в журнал, программа продолжит работу.\n\n" + e.Exception.Message +
            "\n\nЖурнал: " + Log.FilePath,
            "WinPulse", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
