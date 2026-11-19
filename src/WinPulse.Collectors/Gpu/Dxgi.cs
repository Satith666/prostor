using WinPulse.Collectors.Interop;
using WinPulse.Core;

namespace WinPulse.Collectors.Gpu;

public sealed record GpuAdapterInfo(
    string Name,
    int VendorId,
    int DeviceId,
    long DedicatedMemory,
    uint LuidLow,
    int LuidHigh,
    bool IsSoftware)
{
    /// <summary>LUID в том же виде, что в именах счётчиков PDH: 0x00000000_0x0000d1a2.</summary>
    public string Luid => $"0x{(uint)LuidHigh:x8}_0x{LuidLow:x8}";

    public GpuVendor Vendor => VendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };
}

public enum GpuVendor { Other, Nvidia, Amd, Intel }

/// <summary>Список видеоадаптеров через DXGI (без WMI — быстро и надёжно).</summary>
public static unsafe class Dxgi
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static List<GpuAdapterInfo> Enumerate()
    {
        var list = new List<GpuAdapterInfo>();
        try
        {
            Guid iid = IID_IDXGIFactory1;
            if (Native.CreateDXGIFactory1(ref iid, out IntPtr factory) < 0 || factory == IntPtr.Zero) return list;
            byte* desc = stackalloc byte[320];
            try
            {
                void** vt = *(void***)factory;
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vt[12];
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr adapter = IntPtr.Zero;
                    int hr = enumAdapters1(factory, i, &adapter);
                    if (hr < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        void** avt = *(void***)adapter;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)avt[10];
                        new Span<byte>(desc, 320).Clear();
                        if (getDesc1(adapter, desc) < 0) continue;

                        string name = new string((char*)desc).Trim();
                        int vendor = *(int*)(desc + 256);
                        int device = *(int*)(desc + 260);
                        long dedicated = (long)*(nuint*)(desc + 272);
                        uint luidLow = *(uint*)(desc + 296);
                        int luidHigh = *(int*)(desc + 300);
                        uint flags = *(uint*)(desc + 304);
                        bool software = (flags & 2) != 0 || (vendor == 0x1414 && device == 0x8c);
                        list.Add(new GpuAdapterInfo(name, vendor, device, dedicated, luidLow, luidHigh, software));
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            finally
            {
                Release(factory);
            }
        }
        catch (Exception ex)
        {
            Diag.Report("dxgi", ex);
        }
        return list;
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        void** vt = *(void***)unknown;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[2];
        release(unknown);
    }
}
