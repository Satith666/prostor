using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPulse.App.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Path.Combine(Root, "logs");
}

/// <summary>Простой журнал в %LOCALAPPDATA%\WinPulse\logs\winpulse.log (до 2 МБ, затем .1).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxSize = 2 * 1024 * 1024;

    public static string FilePath => Path.Combine(AppPaths.Logs, "winpulse.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string where, Exception ex) => Write("ERROR", $"{where}: {ex}");

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    string old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // журнал не должен ронять программу
        }
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public bool ProMode { get; set; }
    public string? GameExe { get; set; }
    public HashSet<string> Marks { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings-load", ex);
        }
        // Первый запуск: язык по системе.
        return new AppSettings
        {
            Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? "ru" : "en",
        };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            string tmp = AppPaths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, AppPaths.Settings, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings-save", ex);
        }
    }
}

/// <summary>Иконки exe через оболочку Windows (без System.Drawing). Только из потока интерфейса.</summary>
public static class IconCache
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var info = new SHFILEINFO { szDisplayName = "", szTypeName = "" };
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch
        {
        }
        if (Cache.Count > 1000) Cache.Clear();
        Cache[path] = image;
        return image;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) != Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is not null && !(value is string s && s.Length == 0);
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        void Do()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 — DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ и 11), 19 — то же для старых сборок 10.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Do();
        else window.SourceInitialized += (_, _) => Do();
    }
}

/// <summary>true, если значение не null и не пустая строка.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && !(value is string s && s.Length == 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible, если все значения true.</summary>
public sealed class AllTrueToVisibility : IMultiValueConverter
{
    public static AllTrueToVisibility Instance { get; } = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
