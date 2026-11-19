using WinPulse.Core.Diagnostics;

namespace WinPulse.Core.Demo;

/// <summary>Пример замера задержек для демонстрационного режима.</summary>
public static class DemoLatency
{
    public static LatencyReport Create()
    {
        var agg = new LatencyAggregator();
        agg.AddModule(0xFFFFF80000000000, 0x1000000, @"\SystemRoot\system32\ntoskrnl.exe");
        agg.AddModule(0xFFFFF80010000000, 0x3000000, @"\SystemRoot\System32\DriverStore\FileRepository\nv_dispi.inf_amd64\nvlddmkm.sys");
        agg.AddModule(0xFFFFF80020000000, 0x200000, @"\SystemRoot\System32\drivers\ndis.sys");
        agg.AddModule(0xFFFFF80030000000, 0x400000, @"\SystemRoot\System32\drivers\Netwtw10.sys");
        agg.AddModule(0xFFFFF80040000000, 0x100000, @"\SystemRoot\System32\drivers\storport.sys");
        agg.AddModule(0xFFFFF80050000000, 0x100000, @"\SystemRoot\System32\drivers\USBXHCI.SYS");
        agg.AddModule(0xFFFFF80060000000, 0x100000, @"\SystemRoot\System32\drivers\HDAudBus.sys");

        var rnd = new Random(7);
        void Many(ulong addr, bool isr, int count, double typical, double peak)
        {
            for (int i = 0; i < count; i++) agg.Add(isr, addr + 0x100, typical * (0.4 + rnd.NextDouble()));
            agg.Add(isr, addr + 0x100, peak);
        }
        Many(0xFFFFF80000000000, false, 9000, 4, 182);
        Many(0xFFFFF80010000000, false, 5200, 18, 1240);
        Many(0xFFFFF80010000000, true, 5100, 3, 41);
        Many(0xFFFFF80020000000, false, 2100, 6, 96);
        Many(0xFFFFF80030000000, false, 1300, 22, 2760);
        Many(0xFFFFF80040000000, false, 1700, 7, 120);
        Many(0xFFFFF80040000000, true, 1700, 2, 22);
        Many(0xFFFFF80050000000, false, 3900, 3, 61);
        Many(0xFFFFF80050000000, true, 3900, 1.5, 18);
        Many(0xFFFFF80060000000, false, 600, 5, 74);
        return agg.Build(30, 0, "etw");
    }

    public static List<DeviceInterrupt> Devices() =>
    [
        new("NVIDIA GeForce RTX 3050", "Display", @"PCI\VEN_10DE&DEV_2507", false, null),
        new("NVIDIA High Definition Audio", "MEDIA", @"PCI\VEN_10DE&DEV_228E", true, null),
        new("Realtek High Definition Audio", "MEDIA", @"PCI\VEN_8086&DEV_A3F0", true, null),
        new("Intel(R) Wi-Fi 6 AX200 160MHz", "Net", @"PCI\VEN_8086&DEV_2723", true, 16),
        new("Realtek PCIe GbE Family Controller", "Net", @"PCI\VEN_10EC&DEV_8168", true, 1),
        new(Lang.T("Стандартный контроллер NVM Express", "Standard NVM Express Controller"), "SCSIAdapter", @"PCI\VEN_144D&DEV_A808", true, null),
        new(Lang.T("Расширяемый хост-контроллер Intel(R) USB 3.1", "Intel(R) USB 3.1 eXtensible Host Controller"), "USB", @"PCI\VEN_8086&DEV_A3AF", true, 8),
    ];
}
