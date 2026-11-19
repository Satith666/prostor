using System.Runtime.InteropServices;

namespace WinPulse.Collectors.Gpu;

/// <summary>Аппаратное планирование GPU (HAGS): поддерживает ли драйвер и включено ли сейчас.</summary>
public sealed record HagsState(bool Supported, bool Enabled, bool EnabledByDefault);

public static unsafe class Hags
{
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;
    private const int KMTQAITYPE_WDDM_2_9_CAPS = 74;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    /// <summary>null — не удалось узнать (старый драйвер или Windows).</summary>
    public static HagsState? Query(GpuAdapterInfo adapter)
    {
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = adapter.LuidLow, LuidHigh = adapter.LuidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;
            try
            {
                uint caps = 0;
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = KMTQAITYPE_WDDM_2_7_CAPS,
                    pPrivateDriverData = (IntPtr)(&caps),
                    PrivateDriverDataSize = 4,
                };
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                    return new HagsState((caps & 1) != 0, (caps & 2) != 0, (caps & 4) != 0);

                caps = 0;
                q.Type = KMTQAITYPE_WDDM_2_9_CAPS;
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    uint support = caps & 3; // 0 — не поддерживается, 1 — экспериментально, 2 — стабильно, 3 — всегда включено
                    return new HagsState(support >= 1, (caps & 4) != 0, support == 3);
                }
                return null;
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch
        {
            return null;
        }
    }
}
