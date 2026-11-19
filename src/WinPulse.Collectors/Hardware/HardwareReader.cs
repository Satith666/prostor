using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinPulse.Collectors.Diagnostics;
using WinPulse.Core;
using WinPulse.Core.Model;

namespace WinPulse.Collectors.Hardware;

/// <summary>Планка памяти. Скорости — в МТ/с (как пишут в названиях: DDR4-3200).</summary>
public sealed record MemoryModule(string Slot, string Bank, long Capacity, int RatedMts, int ConfiguredMts, string PartNumber, string Maker)
{
    /// <summary>Канал из названия слота (A, B…), если удаётся понять.</summary>
    public string? Channel => HardwareReader.ChannelOf(Slot, Bank);
}

/// <summary>Шина PCI Express видеокарты: сколько линий и какое поколение сейчас и максимум у карты.</summary>
public sealed record GpuLink(string Name, string InstanceId, int? CurrentWidth, int? MaxWidth, int? CurrentGen, int? MaxGen);

/// <summary>Установленная игра и тип диска, на котором она лежит.</summary>
public sealed record GameInstall(string Name, string Path, string Drive, DiskMediaType Media, string DiskModel);

public sealed class HardwareSnapshot
{
    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];
    /// <summary>Сколько слотов памяти на плате (по SMBIOS; бывает неточно).</summary>
    public int MemorySlots { get; init; }
    public string BoardMaker { get; init; } = "";
    public string BoardProduct { get; init; } = "";
    public bool IsLaptop { get; init; }
    public IReadOnlyList<GpuLink> GpuLinks { get; init; } = [];
    public IReadOnlyList<GameInstall> Games { get; init; } = [];
    /// <summary>WMI ответил (иначе данных о памяти нет).</summary>
    public bool MemoryKnown { get; init; }
}

/// <summary>Сведения о железе для проверок «где теряются FPS». Чтение занимает 1–3 секунды (запуск PowerShell).</summary>
public static partial class HardwareReader
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $m = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object {
            [pscustomobject]@{
                Slot = [string]$_.DeviceLocator
                Bank = [string]$_.BankLabel
                Cap = [int64]$_.Capacity
                Speed = [int]$_.Speed
                Conf = [int]$_.ConfiguredClockSpeed
                Part = ([string]$_.PartNumber).Trim()
                Maker = ([string]$_.Manufacturer).Trim()
            }
        })
        $slots = (Get-CimInstance Win32_PhysicalMemoryArray | Measure-Object -Property MemoryDevices -Sum).Sum
        $b = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
        $e = Get-CimInstance Win32_SystemEnclosure | Select-Object -First 1
        [pscustomobject]@{
            Modules = $m
            Slots = [int]$slots
            Board = [string]$b.Manufacturer
            Product = [string]$b.Product
            Chassis = @($e.ChassisTypes)
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    private static readonly int[] LaptopChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    public static HardwareSnapshot Read()
    {
        var mem = ReadMemoryAndBoard();
        return new HardwareSnapshot
        {
            Modules = mem.Modules,
            MemorySlots = mem.Slots,
            BoardMaker = mem.Board,
            BoardProduct = mem.Product,
            IsLaptop = mem.Laptop || OsInfo.HasBattery(),
            MemoryKnown = mem.Known,
            GpuLinks = ReadGpuLinks(),
            Games = FindGames(),
        };
    }

    internal sealed record MemRead(List<MemoryModule> Modules, int Slots, string Board, string Product, bool Laptop, bool Known);

    private static MemRead ReadMemoryAndBoard()
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return new([], 0, "", "", false, false);
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new([], 0, "", "", false, false);
            }
            return ParseMemory(output.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Diag.Report("hardware-wmi", ex);
            return new([], 0, "", "", false, false);
        }
    }

    /// <summary>Разбор вывода скрипта. Открыт для тестов.</summary>
    internal static MemRead ParseMemory(string json)
    {
        int start = json.IndexOf('{');
        if (start < 0) return new([], 0, "", "", false, false);
        using var doc = JsonDocument.Parse(json[start..]);
        var root = doc.RootElement;
        var modules = new List<MemoryModule>();
        if (root.TryGetProperty("Modules", out var arr))
        {
            List<JsonElement> items = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList() : new List<JsonElement> { arr };
            foreach (var m in items)
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string part = Str(m, "Part");
                int speed = Int(m, "Speed");
                modules.Add(new MemoryModule(Str(m, "Slot"), Str(m, "Bank"), Long(m, "Cap"),
                    Math.Max(speed, SpeedFromPartNumber(part) ?? 0), Int(m, "Conf"), part, Str(m, "Maker")));
            }
        }
        bool laptop = root.TryGetProperty("Chassis", out var ch) && ch.ValueKind == JsonValueKind.Array &&
                      ch.EnumerateArray().Any(c => c.TryGetInt32(out int t) && LaptopChassis.Contains(t));
        return new MemRead(modules, Int(root, "Slots"), Str(root, "Board"), Str(root, "Product"), laptop, modules.Count > 0);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    [GeneratedRegex(@"(?<!\d)(2133|2400|2666|2800|2933|3000|3200|3333|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|6000|6200|6400|6600|6800|7200|7600|8000)(?!\d)")]
    private static partial Regex FourDigitSpeed();

    [GeneratedRegex(@"^KF(\d)(\d{2})C", RegexOptions.IgnoreCase)]
    private static partial Regex KingstonFury();

    /// <summary>
    /// Паспортная скорость по маркировке: CMK16GX4M2B3200C16 → 3200, F4-3600C16-8GVKC → 3600, KF432C16BB/16 → 3200.
    /// WMI часто отдаёт скорость по JEDEC (2133/2400), а не профиля XMP — маркировка точнее.
    /// </summary>
    public static int? SpeedFromPartNumber(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var m = FourDigitSpeed().Match(part);
        if (m.Success) return int.Parse(m.Value);
        var k = KingstonFury().Match(part);
        if (k.Success)
        {
            int value = int.Parse(k.Groups[2].Value) * 100;
            if (value is >= 2133 and <= 8000) return value == 2100 ? 2133 : value == 2600 ? 2666 : value == 2900 ? 2933 : value;
        }
        return null;
    }

    [GeneratedRegex(@"channel\s*([A-H])|dimm[_\s-]?([A-H])\d|^([A-H])\d|^([A-H])[_-]dimm|controller\d+-channel([A-H])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    /// <summary>Буква канала из DeviceLocator/BankLabel: «DIMM_A2», «ChannelB-DIMM0», «P0 CHANNEL A», «A1». null — непонятно.</summary>
    public static string? ChannelOf(string slot, string bank)
    {
        foreach (var text in new[] { slot, bank })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = ChannelPattern().Match(text.Trim());
            if (!m.Success) continue;
            for (int g = 1; g < m.Groups.Count; g++)
                if (m.Groups[g].Success) return m.Groups[g].Value.ToUpperInvariant();
        }
        return null;
    }

    // ---------- PCI Express ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    private static readonly Guid PciProps = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private const uint PciBaseClass = 3, CurrentLinkSpeed = 9, CurrentLinkWidth = 10, MaxLinkSpeed = 11, MaxLinkWidth = 12;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern int CM_Get_DevNode_Property(int devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref int size, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern int CM_Get_Device_ID_List_Size(out int size, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static extern int CM_Get_Device_ID_List(string? filter, char[] buffer, int size, int flags);

    private const int CmGetIdListFilterEnumerator = 0x1, CmGetIdListFilterPresent = 0x100;

    private static uint? PciProperty(int devInst, uint pid)
    {
        var key = new DevPropKey { FmtId = PciProps, Pid = pid };
        var buffer = new byte[8];
        int size = buffer.Length;
        if (CM_Get_DevNode_Property(devInst, ref key, out _, buffer, ref size, 0) != 0 || size < 1) return null;
        return size >= 4 ? BitConverter.ToUInt32(buffer, 0) : buffer[0];
    }

    private static List<GpuLink> ReadGpuLinks()
    {
        var result = new List<GpuLink>();
        try
        {
            if (CM_Get_Device_ID_List_Size(out int size, "PCI", CmGetIdListFilterEnumerator | CmGetIdListFilterPresent) != 0 || size <= 1) return result;
            var buffer = new char[size];
            if (CM_Get_Device_ID_List("PCI", buffer, size, CmGetIdListFilterEnumerator | CmGetIdListFilterPresent) != 0) return result;
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (DeviceProblemsReader.CM_Locate_DevNode(out int inst, id, 0) != 0) continue;
                if (PciProperty(inst, PciBaseClass) != 3) continue; // 3 — видеоконтроллер
                string name = DeviceProblemsReader.Property(inst, 1) ?? id; // CM_DRP_DEVICEDESC
                int? Gen(uint? v) => v is >= 1 and <= 6 ? (int)v : null;
                int? Width(uint? v) => v is >= 1 and <= 32 ? (int)v : null;
                result.Add(new GpuLink(name, id,
                    Width(PciProperty(inst, CurrentLinkWidth)), Width(PciProperty(inst, MaxLinkWidth)),
                    Gen(PciProperty(inst, CurrentLinkSpeed)), Gen(PciProperty(inst, MaxLinkSpeed))));
            }
        }
        catch (Exception ex)
        {
            Diag.Report("pci-link", ex);
        }
        return result;
    }

    // ---------- Игры и диски ----------

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    /// <summary>Пути библиотек Steam из libraryfolders.vdf. Открыт для тестов.</summary>
    public static List<string> SteamLibraries(string vdf) =>
        VdfPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Установленные игры Steam и Epic Games с типом диска.</summary>
    public static List<GameInstall> FindGames()
    {
        var found = new List<(string Name, string Path)>();
        try
        {
            string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam is not null)
            {
                string vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
                var libraries = File.Exists(vdf) ? SteamLibraries(File.ReadAllText(vdf)) : [steam.Replace('/', '\\')];
                foreach (var lib in libraries)
                {
                    string common = Path.Combine(lib, "steamapps", "common");
                    if (!Directory.Exists(common)) continue;
                    foreach (var dir in Directory.GetDirectories(common))
                    {
                        string name = Path.GetFileName(dir);
                        if (name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add((name, dir));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Report("steam-libraries", ex);
        }

        try
        {
            string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
            {
                foreach (var file in Directory.GetFiles(manifests, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    string? location = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                    string? name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(name)) found.Add((name, location));
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Report("epic-manifests", ex);
        }

        var media = new Dictionary<string, (DiskMediaType Type, string Model)>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GameInstall>();
        foreach (var (name, path) in found)
        {
            string drive = Path.GetPathRoot(path)?.TrimEnd('\\') ?? "";
            if (drive.Length != 2) continue;
            if (!media.TryGetValue(drive, out var info))
            {
                info = DriveMedia(drive);
                media[drive] = info;
            }
            result.Add(new GameInstall(name, path, drive, info.Type, info.Model));
        }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, out StorageDeviceNumber outBuffer, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;

    /// <summary>Тип диска, на котором лежит буква: HDD, SSD или NVMe.</summary>
    private static (DiskMediaType, string) DriveMedia(string drive)
    {
        try
        {
            using var handle = CreateFile(@"\\.\" + drive, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return (DiskMediaType.Unknown, "");
            if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out var number, Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero))
                return (DiskMediaType.Unknown, "");
            var (model, type, _) = DiskCollector.QueryStatic(number.DeviceNumber);
            return (type, model);
        }
        catch
        {
            return (DiskMediaType.Unknown, "");
        }
    }
}
