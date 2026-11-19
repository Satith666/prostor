using System.Globalization;
using WinPulse.Core.Analysis;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Core.Drivers;

public enum DriverCategory { Graphics, Network, Audio, Storage, Chipset, Bluetooth, Camera }

/// <summary>Драйвер устройства (из Диспетчера устройств и раздела Class в реестре).</summary>
public sealed record DriverInfo(
    string DeviceName,
    string? DeviceClass,
    string? Manufacturer,
    string? Provider,
    string? Version,
    DateTime? Date,
    string? Inf,
    string InstanceId,
    int ProblemCode);

/// <summary>Строка отчёта о драйвере: что это, сколько ему лет, что советуем и где взять новый.</summary>
public sealed class DriverRow
{
    public required DriverInfo Info { get; init; }
    public DriverCategory Category { get; init; }
    public string Vendor { get; init; } = "";
    /// <summary>Версия так, как её называет производитель (например, NVIDIA 560.94).</summary>
    public string? FriendlyVersion { get; init; }
    public int? AgeDays { get; init; }
    public Severity Severity { get; init; }
    public string Advice { get; init; } = "";
    public string? Link { get; init; }
    public string? LinkLabel { get; init; }
    public bool IsInbox { get; init; }

    public string AgeText => AgeDays is not int d ? Lang.T("дата неизвестна", "date unknown")
        : d < 31 ? Lang.T("меньше месяца", "under a month")
        : d < 365 ? Lang.T($"{d / 30} мес.", $"{d / 30} mo")
        : Lang.T($"{d / 365} г. {d % 365 / 30} мес.", $"{d / 365} y {d % 365 / 30} mo");
}

public static class DriverAnalysis
{
    public const string NvidiaUrl = "https://www.nvidia.com/Download/index.aspx";
    public const string AmdUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelUrl = "https://www.intel.com/content/www/us/en/support/detect.html";
    public const string OptionalUpdates = "ms-settings:windowsupdate-optionalupdates";

    private static readonly string[] ChipsetWords =
    {
        "Chipset", "SMBus", "Management Engine", "PSP", "GPIO", "Serial IO", "PCI Express Root", "Dynamic Tuning", "Thermal", "I2C",
    };

    public static DriverCategory? Categorize(DriverInfo d)
    {
        string cls = (d.DeviceClass ?? "").Trim();
        if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase)) return DriverCategory.Graphics;
        if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase))
        {
            string n = d.DeviceName;
            if (n.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) || n.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Personal Area Network", StringComparison.OrdinalIgnoreCase) || n.Contains("TAP-", StringComparison.OrdinalIgnoreCase))
                return null;
            return DriverCategory.Network;
        }
        if (cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)) return DriverCategory.Audio;
        if (cls.Equals("HDC", StringComparison.OrdinalIgnoreCase) || cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)) return DriverCategory.Storage;
        if (cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return d.DeviceName.Contains("Enumerator", StringComparison.OrdinalIgnoreCase) || d.DeviceName.Contains("LE ", StringComparison.OrdinalIgnoreCase)
                ? null : DriverCategory.Bluetooth;
        if (cls.Equals("Camera", StringComparison.OrdinalIgnoreCase) || cls.Equals("Image", StringComparison.OrdinalIgnoreCase)) return DriverCategory.Camera;
        if (cls.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            string vendor = VendorOf(d);
            if (vendor is "Intel" or "AMD" && ChipsetWords.Any(w => d.DeviceName.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return DriverCategory.Chipset;
        }
        return null;
    }

    public static string CategoryText(DriverCategory c) => c switch
    {
        DriverCategory.Graphics => Lang.T("Видеокарта", "Graphics"),
        DriverCategory.Network => Lang.T("Сеть", "Network"),
        DriverCategory.Audio => Lang.T("Звук", "Audio"),
        DriverCategory.Storage => Lang.T("Диски и контроллеры", "Disks and controllers"),
        DriverCategory.Chipset => Lang.T("Чипсет", "Chipset"),
        DriverCategory.Bluetooth => "Bluetooth",
        DriverCategory.Camera => Lang.T("Камера", "Camera"),
        _ => Lang.T("Другое", "Other"),
    };

    /// <summary>Производитель оборудования по идентификатору устройства (VEN_/VID_), иначе по поставщику драйвера.</summary>
    public static string VendorOf(DriverInfo d)
    {
        string id = d.InstanceId.ToUpperInvariant();
        string? code = null;
        int v = id.IndexOf("VEN_", StringComparison.Ordinal);
        if (v >= 0 && v + 8 <= id.Length) code = id.Substring(v + 4, 4);
        int u = id.IndexOf("VID_", StringComparison.Ordinal);
        if (code is null && u >= 0 && u + 8 <= id.Length) code = id.Substring(u + 4, 4);
        string? byCode = code switch
        {
            "10DE" => "NVIDIA",
            "1002" or "1022" => "AMD",
            "8086" or "8087" => "Intel",
            "10EC" or "0BDA" => "Realtek",
            "14E4" or "0A5C" => "Broadcom",
            "168C" or "17CB" or "0CF3" => "Qualcomm",
            "1969" => "Killer",
            "144D" => "Samsung",
            "1B21" => "ASMedia",
            "1912" => "Renesas",
            "1106" => "VIA",
            "15B7" => "WD/SanDisk",
            "2646" => "Kingston",
            "C0A9" => "Micron/Crucial",
            "1E0F" => "KIOXIA",
            _ => null,
        };
        if (byCode is not null) return byCode;
        string p = d.Provider ?? d.Manufacturer ?? "";
        foreach (var name in new[] { "NVIDIA", "AMD", "Advanced Micro Devices", "Intel", "Realtek", "Qualcomm", "Broadcom", "MediaTek", "Microsoft" })
            if (p.Contains(name, StringComparison.OrdinalIgnoreCase))
                return name == "Advanced Micro Devices" ? "AMD" : name;
        return p;
    }

    /// <summary>Версия драйвера Windows → версия NVIDIA: 32.0.15.6094 → 560.94.</summary>
    public static string? NvidiaVersion(string? driverVersion)
    {
        if (string.IsNullOrWhiteSpace(driverVersion)) return null;
        var parts = driverVersion.Split('.');
        if (parts.Length != 4 || parts[3].Length != 4) return null;
        string digits = parts[2] + parts[3];
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return null;
        string last5 = digits[^5..];
        return int.Parse(last5[..3], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "." + last5[3..];
    }

    public static List<DriverRow> Analyze(IEnumerable<DriverInfo> drivers, DateTime now)
    {
        var rows = new List<DriverRow>();
        foreach (var d in drivers)
        {
            var category = Categorize(d);
            if (category is null) continue;
            string vendor = VendorOf(d);
            int? age = d.Date is DateTime date && date > new DateTime(2000, 1, 1) ? (int)(now - date).TotalDays : null;
            bool inbox = (d.Provider ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            string? friendly = vendor == "NVIDIA" ? NvidiaVersion(d.Version) : null;

            Severity severity = Severity.Ok;
            string advice;
            string? link = null, linkLabel = null;

            if (category == DriverCategory.Graphics)
            {
                (link, linkLabel) = vendor switch
                {
                    "NVIDIA" => (NvidiaUrl, Lang.T("Драйверы NVIDIA", "NVIDIA drivers")),
                    "AMD" => (AmdUrl, Lang.T("Драйверы AMD", "AMD drivers")),
                    "Intel" => (IntelUrl, Lang.T("Помощник Intel", "Intel assistant")),
                    _ => (OptionalUpdates, Lang.T("Обновления Windows", "Windows updates")),
                };
                if (d.DeviceName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || d.DeviceName.Contains("Базовый видеоадаптер", StringComparison.OrdinalIgnoreCase))
                {
                    severity = Severity.Critical;
                    advice = Lang.T("Драйвер видеокарты не установлен — работает «Базовый видеоадаптер». Игры будут работать медленно или не запустятся. Установите драйвер с сайта производителя видеокарты.", "The graphics driver isn't installed — Microsoft Basic Display Adapter is in use. Games will run slowly or not start. Install the driver from the GPU maker's website.");
                }
                else if (age is int a && a > 365)
                {
                    severity = Severity.Warning;
                    advice = Lang.T("Драйверу видеокарты больше года. Новые игры часто требуют свежий драйвер — проверьте обновление на сайте производителя.", "The graphics driver is over a year old. New games often need a fresh driver — check for an update on the maker's website.");
                }
                else if (age is int b && b > 120)
                {
                    severity = Severity.Info;
                    advice = Lang.T("Драйверу больше четырёх месяцев — есть смысл проверить, не вышел ли новый.", "The driver is over four months old — worth checking whether a new one is out.");
                }
                else
                {
                    advice = Lang.T("Драйвер свежий. Обновлять стоит, если вышла новая игра или драйвер исправляет вашу проблему.", "The driver is fresh. Update if a new game is out or a driver fixes your problem.");
                }
            }
            else
            {
                link = OptionalUpdates;
                linkLabel = Lang.T("Обновления Windows", "Windows updates");
                if (category == DriverCategory.Chipset && vendor == "AMD") (link, linkLabel) = (AmdUrl, Lang.T("Драйверы чипсета AMD", "AMD chipset drivers"));
                if (category == DriverCategory.Chipset && vendor == "Intel") (link, linkLabel) = (IntelUrl, Lang.T("Помощник Intel", "Intel assistant"));

                if (inbox)
                    advice = Lang.T("Стандартный драйвер Windows — обновляется через Центр обновления.", "Built-in Windows driver — updated through Windows Update.");
                else if (age is int a && a > 3 * 365)
                {
                    severity = Severity.Info;
                    advice = Lang.T("Драйверу больше трёх лет. Если устройство работает без сбоев — можно не трогать; при проблемах возьмите новый на сайте производителя материнской платы или ноутбука.", "The driver is over three years old. If the device works fine, leave it; if there are problems, get a new one from the motherboard or laptop maker's website.");
                }
                else
                    advice = Lang.T("В порядке. Обновлять стоит только при проблемах с устройством.", "Fine. Update only if the device has problems.");
            }

            if (d.ProblemCode != 0 && d.ProblemCode != 22)
            {
                severity = Severity.Warning > severity ? Severity.Warning : severity;
                advice = Lang.T($"Устройство работает с ошибкой: {DeviceProblemCodes.Describe(d.ProblemCode)} (код {d.ProblemCode}). ", $"The device has an error: {DeviceProblemCodes.Describe(d.ProblemCode)} (code {d.ProblemCode}). ") + advice;
            }

            rows.Add(new DriverRow
            {
                Info = d,
                Category = category.Value,
                Vendor = vendor,
                FriendlyVersion = friendly,
                AgeDays = age,
                Severity = severity,
                Advice = advice,
                Link = link,
                LinkLabel = linkLabel,
                IsInbox = inbox,
            });
        }
        return rows
            .OrderBy(r => r.Category)
            .ThenByDescending(r => r.Severity)
            .ThenBy(r => r.Info.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Чистая переустановка видеодрайвера — по шагам.</summary>
    public static IReadOnlyList<string> CleanInstallSteps(string vendor) => new[]
    {
        vendor is "NVIDIA" or "AMD" or "Intel"
            ? Lang.T($"Скачайте новый драйвер заранее — с сайта {vendor}.", $"Download the new driver in advance from the {vendor} website.")
            : Lang.T("Скачайте новый драйвер заранее — с сайта производителя видеокарты.", "Download the new driver in advance from the GPU maker's website."),
        Lang.T("Отключите интернет, чтобы Windows не поставила свой драйвер посреди процесса.", "Disconnect from the internet so Windows doesn't install its own driver midway."),
        vendor switch
        {
            "NVIDIA" => Lang.T("Запустите установщик NVIDIA → «Выборочная установка» → отметьте «Выполнить чистую установку».", "Run the NVIDIA installer → Custom install → check Perform a clean installation."),
            "AMD" => Lang.T("В установщике AMD Software выберите «Сброс к заводским настройкам» (Factory Reset).", "In the AMD Software installer choose Factory Reset."),
            _ => Lang.T("Удалите старый драйвер в «Приложения и возможности», перезагрузитесь и установите новый.", "Uninstall the old driver in Apps & features, restart and install the new one."),
        },
        Lang.T("Если проблемы остаются — удалите драйвер утилитой Display Driver Uninstaller (DDU) в безопасном режиме и установите заново.", "If problems remain, remove the driver with Display Driver Uninstaller (DDU) in Safe Mode and install it again."),
        Lang.T("Перезагрузите компьютер и снова подключите интернет.", "Restart the PC and reconnect to the internet."),
    };
}
