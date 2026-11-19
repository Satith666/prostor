using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPulse.Collectors.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PDH_FMT_COUNTERVALUE
{
    [FieldOffset(0)] public uint CStatus;
    [FieldOffset(8)] public double DoubleValue;
}

internal static unsafe class Native
{
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, char* buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, int inSize,
        void* outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

    // Службы
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    public static extern bool EnumServicesStatusEx(IntPtr scManager, int infoLevel, int serviceType, int serviceState,
        IntPtr services, int bufferSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    // PDH
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    // DXGI
    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // Электропитание
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
