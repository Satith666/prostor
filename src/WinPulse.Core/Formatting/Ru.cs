using System.Globalization;

namespace WinPulse.Core.Formatting;

/// <summary>Форматирование чисел для русского интерфейса: «1,2 ГБ», «3,2 МБ/с», «34%».</summary>
public static class Ru
{
    private const double KB = 1024;
    private const double MB = KB * 1024;
    private const double GB = MB * 1024;
    private const double TB = GB * 1024;

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Культура чисел по языку интерфейса: «1,2» по-русски, «1.2» по-английски.</summary>
    public static CultureInfo Culture => Lang.English ? EnCulture : RuCulture;

    public static string Bytes(double bytes)
    {
        double a = Math.Abs(bytes);
        if (a >= TB) return Num(bytes / TB, 100) + Lang.T(" ТБ", " TB");
        if (a >= GB) return Num(bytes / GB, 100) + Lang.T(" ГБ", " GB");
        if (a >= MB) return Num(bytes / MB, 10) + Lang.T(" МБ", " MB");
        if (a >= KB) return Num(bytes / KB, 10) + Lang.T(" КБ", " KB");
        return bytes.ToString("0", Culture) + Lang.T(" Б", " B");
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + Lang.T("/с", "/s");

    /// <summary>Сетевая скорость: принимает байты в секунду, показывает биты.</summary>
    public static string Bits(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        if (bits >= 1e9) return Num(bits / 1e9, 10) + Lang.T(" Гбит/с", " Gbps");
        if (bits >= 1e6) return Num(bits / 1e6, 10) + Lang.T(" Мбит/с", " Mbps");
        return (bits / 1e3).ToString("0", Culture) + Lang.T(" Кбит/с", " Kbps");
    }

    public static string Percent(double value) => value.ToString("0", Culture) + "%";

    public static string Percent1(double value) => value.ToString("0.0", Culture) + "%";

    public static string GHz(double mhz) => (mhz / 1000.0).ToString("0.0", Culture) + Lang.T(" ГГц", " GHz");

    public static string Ms(double ms) => ms < 10 ? ms.ToString("0.0", Culture) + Lang.T(" мс", " ms") : ms.ToString("0", Culture) + Lang.T(" мс", " ms");

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return Lang.T($"{(int)t.TotalDays} д {t.Hours} ч", $"{(int)t.TotalDays} d {t.Hours} h");
        if (t.TotalHours >= 1) return Lang.T($"{(int)t.TotalHours} ч {t.Minutes} мин", $"{(int)t.TotalHours} h {t.Minutes} min");
        return Lang.T($"{Math.Max(0, (int)t.TotalMinutes)} мин", $"{Math.Max(0, (int)t.TotalMinutes)} min");
    }

    /// <summary>Склонение: Plural(5, "процесс", "процесса", "процессов") → «процессов».</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long n100 = Math.Abs(n) % 100;
        long n10 = n100 % 10;
        if (n100 is >= 11 and <= 14) return many;
        if (n10 == 1) return one;
        if (n10 is >= 2 and <= 4) return few;
        return many;
    }

    public static string Count(long n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";

    private static string Num(double v, double decimalsBelow) =>
        Math.Abs(v) < decimalsBelow ? v.ToString("0.0", Culture) : v.ToString("0", Culture);
}
