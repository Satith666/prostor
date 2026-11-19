using System.Security.Principal;
using Microsoft.Win32;
using WinPulse.Collectors.Interop;
using WinPulse.Core.Model;

namespace WinPulse.Collectors;

public sealed class OsDetails
{
    public string ProductName { get; init; } = "Windows";
    public string DisplayVersion { get; init; } = "";
    public int Build { get; init; }
    public int Ubr { get; init; }
    public string Edition { get; init; } = "";
    public bool IsWindows11 => Build >= 22000;
    public string Display => DisplayVersion.Length > 0 ? $"{ProductName} {DisplayVersion}" : ProductName;
    public string BuildText => Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();
}

public static class OsInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static OsDetails Read()
    {
        int build = Environment.OSVersion.Version.Build;
        string product = "Windows";
        string display = "";
        string edition = "";
        int ubr = 0;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(CurrentVersionKey);
            if (key is not null)
            {
                product = key.GetValue("ProductName") as string ?? product;
                display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? "";
                edition = key.GetValue("EditionID") as string ?? "";
                if (key.GetValue("UBR") is int u) ubr = u;
                if (int.TryParse(key.GetValue("CurrentBuildNumber") as string, out int b) && b > 0) build = b;
            }
        }
        catch
        {
            // оставляем значения по умолчанию
        }
        // В реестре Windows 11 по-прежнему пишет «Windows 10» — исправляем по номеру сборки.
        if (build >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        return new OsDetails { ProductName = product, DisplayVersion = display, Build = build, Ubr = ubr, Edition = edition };
    }

    public static SystemInfo ReadSystemInfo()
    {
        var os = Read();
        string cpu = "";
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch
        {
        }

        long installed = 0;
        if (Native.GetPhysicallyInstalledSystemMemory(out ulong kb)) installed = (long)kb * 1024;

        return new SystemInfo
        {
            MachineName = Environment.MachineName,
            OsName = os.Display,
            OsBuild = os.Build,
            CpuName = cpu,
            LogicalCores = Environment.ProcessorCount,
            InstalledMemory = installed,
        };
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Есть ли у компьютера батарея (ноутбук).</summary>
    public static bool HasBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return false;
        return s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }
}
