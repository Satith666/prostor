using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinPulse.Core;
using WinPulse.Core.Display;

namespace WinPulse.Collectors.Display;

public sealed class MonitorInfo
{
    /// <summary>Имя устройства Windows: \\.\DISPLAY1.</summary>
    public string DeviceName { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshHz { get; init; }
    public int BitsPerPixel { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    /// <summary>Максимальная частота при текущем разрешении.</summary>
    public int MaxRefreshHz { get; init; }
    public IReadOnlyList<int> RefreshRates { get; init; } = [];
    public int ScalePercent { get; init; } = 100;
    public EdidInfo? Edid { get; init; }
    public VrrSupport Vrr { get; init; }
    public string? ConnectionHint { get; init; }

    public string Title => Lang.T($"{FriendlyName} — {Width}×{Height}, {RefreshHz} Гц", $"{FriendlyName} — {Width}×{Height}, {RefreshHz} Hz");
}

/// <summary>Мониторы: режимы, частоты, масштаб, EDID; смена частоты; временное отключение второго монитора.</summary>
public static class Displays
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const uint DM_POSITION = 0x20;
    private const uint DM_BITSPERPEL = 0x40000;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const uint CDS_TEST = 0x2;
    private const uint CDS_NORESET = 0x10000000;
    private const uint SDC_TOPOLOGY_EXTEND = 0x4;
    private const uint SDC_APPLY = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsExApply(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };
    private static DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "",
    };

    public static List<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();
        try
        {
            for (uint i = 0; i < 32; i++)
            {
                var adapter = NewDisplayDevice();
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                var current = NewDevMode();
                if (!EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref current)) continue;

                var rates = new SortedSet<int>();
                for (int m = 0; m < 4000; m++)
                {
                    var mode = NewDevMode();
                    if (!EnumDisplaySettings(adapter.DeviceName, m, ref mode)) break;
                    if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > 1)
                        rates.Add((int)mode.dmDisplayFrequency);
                }
                if (current.dmDisplayFrequency > 1) rates.Add((int)current.dmDisplayFrequency);

                var monitor = NewDisplayDevice();
                string? interfaceName = null;
                string monitorString = "";
                if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    interfaceName = monitor.DeviceID;
                    monitorString = monitor.DeviceString;
                }

                var edid = Edid.Parse(ReadEdid(interfaceName));
                int maxRate = rates.Count > 0 ? rates.Max : (int)current.dmDisplayFrequency;
                string friendly = edid?.MonitorName ?? (monitorString.Length > 0 && !monitorString.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)
                    ? monitorString
                    : Lang.T("Монитор", "Monitor") + " " + (result.Count + 1));

                result.Add(new MonitorInfo
                {
                    DeviceName = adapter.DeviceName,
                    AdapterName = adapter.DeviceString,
                    FriendlyName = friendly,
                    IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    Width = (int)current.dmPelsWidth,
                    Height = (int)current.dmPelsHeight,
                    RefreshHz = (int)current.dmDisplayFrequency,
                    BitsPerPixel = (int)current.dmBitsPerPel,
                    PositionX = current.dmPositionX,
                    PositionY = current.dmPositionY,
                    MaxRefreshHz = maxRate,
                    RefreshRates = rates.ToList(),
                    ScalePercent = ScaleAt(current.dmPositionX, current.dmPositionY),
                    Edid = edid,
                    Vrr = edid?.Vrr(maxRate) ?? VrrSupport.No,
                });
            }
        }
        catch (Exception ex)
        {
            Diag.Report("displays", ex);
        }
        return result;
    }

    private static byte[]? ReadEdid(string? interfaceName)
    {
        // \\?\DISPLAY#GSM5B7F#5&2a8d1b7b&0&UID4352#{e6f07b5f-...} → Enum\DISPLAY\GSM5B7F\5&2a8d1b7b&0&UID4352
        if (string.IsNullOrEmpty(interfaceName)) return null;
        var parts = interfaceName.Split('#');
        if (parts.Length < 3) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static int ScaleAt(int x, int y)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = x + 1, Y = y + 1 }, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                return (int)Math.Round(dpiX * 100.0 / 96);
        }
        catch
        {
        }
        return 100;
    }

    /// <summary>Ставит частоту монитору при текущем разрешении. Возвращает null при успехе или текст ошибки.</summary>
    public static string? SetRefreshRate(string deviceName, int width, int height, int hz)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode))
            return Lang.T("Монитор не найден: ", "Monitor not found: ") + deviceName;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        mode.dmDisplayFrequency = (uint)hz;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int test = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != 0) return ChangeError(test);
        int result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return result == 0 ? null : ChangeError(result);
    }

    /// <summary>Временно отключает монитор (как «Отключить этот экран» в параметрах).</summary>
    public static string? Detach(string deviceName)
    {
        var mode = NewDevMode();
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        mode.dmPelsWidth = 0;
        mode.dmPelsHeight = 0;
        mode.dmPositionX = 0;
        mode.dmPositionY = 0;
        int r = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r != 0) return ChangeError(r);
        r = ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return r == 0 ? null : ChangeError(r);
    }

    /// <summary>Возвращает отключённый монитор на прежнее место с прежним режимом.</summary>
    public static string? Reattach(string deviceName, int x, int y, int width, int height, int hz, int bits)
    {
        var mode = NewDevMode();
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | (bits > 0 ? DM_BITSPERPEL : 0);
        mode.dmPositionX = x;
        mode.dmPositionY = y;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        mode.dmDisplayFrequency = (uint)hz;
        mode.dmBitsPerPel = (uint)Math.Max(0, bits);
        int r = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r == 0) r = ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (r == 0) return null;
        // Запасной путь: попросить Windows вернуть последнюю конфигурацию «Расширить».
        int topology = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        return topology == 0 ? null : ChangeError(r);
    }

    private static string ChangeError(int code) => code switch
    {
        1 => Lang.T("нужна перезагрузка", "restart required"),
        -1 => Lang.T("драйвер отказал в смене режима", "the driver rejected the mode"),
        -2 => Lang.T("режим не поддерживается", "mode not supported"),
        -3 => Lang.T("не удалось записать в реестр", "could not write to the registry"),
        -4 => Lang.T("неверные параметры", "bad flags"),
        -5 => Lang.T("неверные параметры", "bad parameters"),
        -6 => Lang.T("неверный режим", "bad mode"),
        -7 => Lang.T("режим не подходит для двух экранов", "bad dual-view mode"),
        _ => Lang.T("ошибка ", "error ") + code,
    };
}
