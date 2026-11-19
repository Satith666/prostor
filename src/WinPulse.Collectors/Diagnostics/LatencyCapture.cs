using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Collectors.Diagnostics;

/// <summary>
/// Замер задержек DPC/ISR через ETW (как LatencyMon и xtw): своя «системная» сессия ядра с флагами DPC, INTERRUPT
/// и IMAGE_LOAD, чтение в реальном времени. Нужны права администратора. Ничего в системе не меняет.
/// </summary>
public static unsafe class LatencyCapture
{
    private const string SessionName = "WinPulse Latency";

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint EVENT_TRACE_FLAG_IMAGE_LOAD = 0x00000004;
    private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
    private const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;
    // Модули ядра лежат в верхней половине адресного пространства x64.
    private const ulong KernelSpace = 0xFFFF800000000000UL;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    private static extern int StartTrace(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern int ControlTrace(ulong handle, string? name, IntPtr properties, uint code);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW")]
    private static extern ulong OpenTrace(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers([Out] IntPtr[] imageBases, uint size, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDeviceDriverBaseNameW")]
    private static extern uint GetDeviceDriverBaseName(IntPtr imageBase, char[] name, uint size);

    /// <summary>Замер на <paramref name="seconds"/> секунд. Бросает UnauthorizedAccessException без прав администратора.</summary>
    public static LatencyReport Capture(int seconds, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("x64 only");
        var agg = new LatencyAggregator();
        double freq = Stopwatch.Frequency;
        int dcStart = 0;

        // Обработчик событий: держим ссылку на делегат до конца замера, иначе сборщик мусора его уберёт.
        EventRecordCallback callback = record =>
        {
            try
            {
                byte* r = (byte*)record;
                ushort flags = *(ushort*)(r + 4);
                long timestamp = *(long*)(r + 16);
                Guid provider = *(Guid*)(r + 24);
                byte opcode = r[45];
                ushort length = *(ushort*)(r + 86);
                byte* data = *(byte**)(r + 96);
                if (data == null || length == 0) return;
                var span = new ReadOnlySpan<byte>(data, length);
                bool p64 = (flags & EVENT_HEADER_FLAG_32_BIT_HEADER) == 0;

                if (provider == KernelEvents.PerfInfo)
                {
                    bool isr = KernelEvents.IsIsr(opcode);
                    if (!isr && !KernelEvents.IsDpc(opcode)) return;
                    if (!KernelEvents.TryParseDpc(span, p64, out long initial, out ulong routine)) return;
                    agg.Add(isr, routine, (timestamp - initial) * 1_000_000.0 / freq);
                }
                else if (provider == KernelEvents.Image && opcode is KernelEvents.OpImageDcStart or KernelEvents.OpImageLoad)
                {
                    if (KernelEvents.TryParseImage(span, p64, out ulong imageBase, out ulong size, out uint pid, out string file) &&
                        (pid == 0 || imageBase >= KernelSpace))
                    {
                        agg.AddModule(imageBase, size, file);
                        dcStart++;
                    }
                }
            }
            catch
            {
                // Одно битое событие не должно ронять замер.
            }
        };

        IntPtr props = Marshal.AllocHGlobal(PropertiesSize + 1024);
        IntPtr logfile = Marshal.AllocHGlobal(LogFileSize);
        IntPtr namePtr = Marshal.StringToHGlobalUni(SessionName);
        ulong session = 0, trace = INVALID_PROCESSTRACE_HANDLE;
        long lost = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            // Хвост прошлого запуска (если WinPulse упал посреди замера) — останавливаем свою сессию по имени.
            FillProperties(props, Guid.Empty);
            ControlTrace(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);

            FillProperties(props, Guid.NewGuid());
            int rc = StartTrace(out session, SessionName, props);
            if (rc == 5) throw new UnauthorizedAccessException(Lang.T("Для замера задержек нужны права администратора.", "Administrator rights are required to measure latency."));
            if (rc != 0) throw new InvalidOperationException(Lang.T($"Windows не запустила сессию ETW (код {rc}).", $"Windows didn't start the ETW session (code {rc})."));

            new Span<byte>((void*)logfile, LogFileSize).Clear();
            Marshal.WriteIntPtr(logfile, 8, namePtr);                                   // LoggerName
            Marshal.WriteInt32(logfile, 28, unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP)));
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback)); // EventRecordCallback
            trace = OpenTrace(logfile);
            if (trace == INVALID_PROCESSTRACE_HANDLE)
                throw new InvalidOperationException(Lang.T("Не удалось открыть сессию ETW.", "Couldn't open the ETW session."));

            ulong handle = trace;
            var reader = new Thread(() => ProcessTrace([handle], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "WinPulse ETW" };
            reader.Start();

            var until = TimeSpan.FromSeconds(seconds);
            while (watch.Elapsed < until && !ct.IsCancellationRequested && reader.IsAlive)
            {
                Thread.Sleep(200);
                progress?.Report(Math.Min(1, watch.Elapsed / until));
            }

            FillProperties(props, Guid.Empty);
            if (ControlTrace(session, null, props, EVENT_TRACE_CONTROL_STOP) == 0)
                lost = (uint)Marshal.ReadInt32(props, 88) + (uint)Marshal.ReadInt32(props, 100);
            session = 0;
            reader.Join(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (trace != INVALID_PROCESSTRACE_HANDLE) CloseTrace(trace);
            if (session != 0)
            {
                FillProperties(props, Guid.Empty);
                ControlTrace(session, null, props, EVENT_TRACE_CONTROL_STOP);
            }
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(logfile);
            Marshal.FreeHGlobal(props);
            GC.KeepAlive(callback);
        }

        string source = "etw";
        if (dcStart == 0)
        {
            // Список модулей ядра не пришёл из ETW — берём адреса драйверов у psapi.
            AddDriversFromPsapi(agg);
            source = "psapi";
        }
        // Обработчик ещё мог успеть отработать после агрегирования — отчёт строим один раз в конце.
        var report = agg.Build(watch.Elapsed.TotalSeconds, lost, source);
        if (report.DpcCount + report.IsrCount > 0 && agg.ModuleCount == 0)
            report.Error = Lang.T("Не удалось узнать адреса драйверов — имена не определены.", "Couldn't resolve driver addresses — names are unknown.");
        return report;
    }

    private static void FillProperties(IntPtr props, Guid guid)
    {
        new Span<byte>((void*)props, PropertiesSize + 1024).Clear();
        Marshal.WriteInt32(props, 0, PropertiesSize + 1024);                // Wnode.BufferSize
        if (guid != Guid.Empty) *(Guid*)((byte*)props + 24) = guid;         // Wnode.Guid
        Marshal.WriteInt32(props, 40, 1);                                    // Wnode.ClientContext = QPC
        Marshal.WriteInt32(props, 44, unchecked((int)WNODE_FLAG_TRACED_GUID)); // Wnode.Flags
        Marshal.WriteInt32(props, 48, 64);                                   // BufferSize, КБ
        Marshal.WriteInt32(props, 52, 32);                                   // MinimumBuffers
        Marshal.WriteInt32(props, 56, 256);                                  // MaximumBuffers
        Marshal.WriteInt32(props, 64, unchecked((int)(EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE)));
        Marshal.WriteInt32(props, 68, 1);                                    // FlushTimer, с
        Marshal.WriteInt32(props, 72, unchecked((int)(EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT | EVENT_TRACE_FLAG_IMAGE_LOAD)));
        Marshal.WriteInt32(props, 116, PropertiesSize);                      // LoggerNameOffset
    }

    private static void AddDriversFromPsapi(LatencyAggregator agg)
    {
        try
        {
            EnumDeviceDrivers([], 0, out uint needed);
            if (needed == 0) return;
            var bases = new IntPtr[needed / IntPtr.Size + 16];
            if (!EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out needed)) return;
            var name = new char[260];
            for (int i = 0; i < Math.Min(bases.Length, needed / IntPtr.Size); i++)
            {
                if (bases[i] == IntPtr.Zero) continue;
                uint len = GetDeviceDriverBaseName(bases[i], name, (uint)name.Length);
                if (len > 0) agg.AddModule((ulong)bases[i], 0, new string(name, 0, (int)len));
            }
        }
        catch (Exception ex)
        {
            Diag.Report("psapi drivers", ex);
        }
    }
}
