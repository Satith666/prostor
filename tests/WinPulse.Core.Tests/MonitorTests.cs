using WinPulse.Core;
using WinPulse.Core.Analysis;
using WinPulse.Core.Demo;
using WinPulse.Core.Display;
using WinPulse.Core.Model;

namespace WinPulse.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void KeepsLastValuesInOrder()
    {
        var rb = new RingBuffer(3);
        foreach (var v in new double[] { 1, 2, 3, 4, 5 }) rb.Add(v);
        Assert.Equal(new double[] { 3, 4, 5 }, rb.ToArray());
        Assert.Equal(5, rb.Last);
        Assert.Equal(4.5, rb.AverageOfLast(2));
    }

    [Fact]
    public void TrendOfLinearGrowthIsExact()
    {
        // +10 каждые 10 секунд = +3600 в час
        var ys = Enumerable.Range(0, 50).Select(i => 100.0 + i * 10).ToArray();
        var (slope, r2) = Trend.Linear(ys, 10);
        Assert.Equal(3600, slope, 3);
        Assert.Equal(1, r2, 6);
    }

    [Fact]
    public void TrendOfFlatLineIsZero()
    {
        var ys = Enumerable.Repeat(5.0, 40).ToArray();
        var (slope, r2) = Trend.Linear(ys, 10);
        Assert.Equal(0, slope, 6);
        Assert.Equal(0, r2, 6);
    }
}

public class ProcessRateTests
{
    private static ProcessSample P(int pid, long cpu, long read = 0, long create = 1) =>
        new(pid, 0, "a.exe", create, cpu, 0, 0, 0, read, 0, 0, 0, 1, 1);

    [Fact]
    public void ComputesCpuPercentAndIo()
    {
        var calc = new ProcessRateCalculator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        calc.Compute(new[] { P(10, 0, 0) }, t0, 2);
        // за 1 с процесс потратил 1 с процессорного времени на 2 ядрах → 50%
        var rates = calc.Compute(new[] { P(10, 10_000_000, 1_000_000) }, t0.AddSeconds(1), 2);
        Assert.Equal(50, rates[10].CpuPercent, 3);
        Assert.Equal(1_000_000, rates[10].ReadBps, 3);
    }

    [Fact]
    public void NewProcessWithReusedPidStartsFromZero()
    {
        var calc = new ProcessRateCalculator();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        calc.Compute(new[] { P(10, 50_000_000, create: 1) }, t0, 4);
        var rates = calc.Compute(new[] { P(10, 1_000, create: 2) }, t0.AddSeconds(1), 4);
        Assert.Equal(0, rates[10].CpuPercent);
    }
}

public class EdidTests
{
    private static byte[] BaseBlock(int extensions, int rangeMin = 48, int rangeMax = 144)
    {
        var b = new byte[128 + 128 * extensions];
        byte[] header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        header.CopyTo(b, 0);
        b[8] = 0x1E; // «GSM»
        b[9] = 0x6D;

        // Подробный режим 2560×1440 @ 144 Гц
        byte[] dtd = { 0x98, 0xE2, 0x00, 0xA0, 0xA0, 0xA0, 0x29, 0x50 };
        dtd.CopyTo(b, 54);

        // Имя монитора
        b[72 + 3] = 0xFC;
        var name = System.Text.Encoding.ASCII.GetBytes("LG ULTRAGEAR\n");
        name.CopyTo(b, 72 + 5);

        // Пределы частот
        b[90 + 3] = 0xFD;
        b[90 + 5] = (byte)rangeMin;
        b[90 + 6] = (byte)rangeMax;

        b[126] = (byte)extensions;
        return b;
    }

    [Fact]
    public void ParsesBaseBlock()
    {
        var info = Edid.Parse(BaseBlock(0));
        Assert.NotNull(info);
        Assert.Equal("GSM", info!.ManufacturerId);
        Assert.Equal("LG ULTRAGEAR", info.MonitorName);
        Assert.Equal(48, info.RangeMinHz);
        Assert.Equal(144, info.RangeMaxHz);
        Assert.Equal(144, info.PreferredRefreshHz);
        Assert.Equal(VrrSupport.Likely, info.Vrr());
    }

    [Fact]
    public void DetectsFreeSyncBlock()
    {
        var b = BaseBlock(1);
        int o = 128;
        b[o] = 0x02;
        b[o + 1] = 0x03;
        b[o + 2] = 13; // режимы начинаются после 9 байт блока данных
        byte[] vsdb = { 0x68, 0x1A, 0x00, 0x00, 0x01, 0x00, 0x30, 0x90, 0x00 };
        vsdb.CopyTo(b, o + 4);

        var info = Edid.Parse(b)!;
        Assert.True(info.HasFreeSyncBlock);
        Assert.Equal(48, info.FreeSyncMinHz);
        Assert.Equal(144, info.FreeSyncMaxHz);
        Assert.Equal(VrrSupport.Yes, info.Vrr());
        Assert.Equal<(int, int)?>((48, 144), info.VrrRange);
    }

    [Fact]
    public void DetectsHdmiVrr()
    {
        var b = BaseBlock(1);
        int o = 128;
        b[o] = 0x02;
        b[o + 1] = 0x03;
        b[o + 2] = 15;
        byte[] hf = { 0x6A, 0xD8, 0x5D, 0xC4, 0x01, 0x78, 0x00, 0x00, 0x00, 0x30, 0x78 };
        hf.CopyTo(b, o + 4);

        var info = Edid.Parse(b)!;
        Assert.True(info.HasHdmiVrr);
        Assert.Equal(48, info.HdmiVrrMinHz);
        Assert.Equal(120, info.HdmiVrrMaxHz);
        Assert.Equal(VrrSupport.Yes, info.Vrr());
    }

    [Fact]
    public void OfficeMonitorHasNoVrr()
    {
        var info = Edid.Parse(BaseBlock(0, 56, 76))!;
        Assert.Equal(VrrSupport.No, info.Vrr(60));
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Null(Edid.Parse(new byte[128]));
        Assert.Null(Edid.Parse(null));
    }
}

public class GrouperTests
{
    private sealed class FakeSource : ISystemSource
    {
        public Dictionary<int, AppIdentity> Ids { get; } = new();
        public Dictionary<int, IReadOnlyList<ServiceEntry>> Services { get; } = new();
        public SystemInfo Info { get; } = new() { LogicalCores = 4 };
        public bool IsDemo => true;
        public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> ServicesByPid => Services;
        public RawSnapshot Capture() => throw new NotSupportedException();
        public AppIdentity Identify(ProcessSample process) =>
            Ids.TryGetValue(process.Pid, out var id) ? id : new AppIdentity(null, null, null);
        public void Dispose() { }
    }

    private static ProcessSample P(int pid, int parent, string name, long create, long mem = 100) =>
        new(pid, parent, name, create, 0, mem, mem, mem, 0, 0, 0, 10, 1, 1);

    [Fact]
    public void GroupsByPathServicesAndHelpers()
    {
        var src = new FakeSource();
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        const string teams = @"C:\Apps\ms-teams.exe";
        const string webview = @"C:\WebView\msedgewebview2.exe";
        src.Ids[100] = new AppIdentity(chrome, "Google Chrome", "Google LLC");
        src.Ids[101] = new AppIdentity(chrome, "Google Chrome", "Google LLC");
        src.Ids[200] = new AppIdentity(teams, "Microsoft Teams", "Microsoft");
        src.Ids[201] = new AppIdentity(webview, "WebView2", "Microsoft");
        src.Ids[300] = new AppIdentity(@"C:\Windows\System32\svchost.exe", null, null);
        src.Services[300] = new[] { new ServiceEntry("wuauserv", "Центр обновления Windows") };

        var snap = new RawSnapshot
        {
            Processes = new[]
            {
                P(4, 0, "System", 1),
                P(50, 4, "explorer.exe", 2),
                P(100, 50, "chrome.exe", 10),
                P(101, 100, "chrome.exe", 11),
                P(200, 50, "ms-teams.exe", 20),
                P(201, 200, "msedgewebview2.exe", 21),
                P(300, 4, "svchost.exe", 5),
            },
        };

        var groups = new AppGrouper(@"C:\Windows").Build(snap, new Dictionary<int, ProcessRate>(), src);

        var chromeGroup = Assert.Single(groups, g => g.Key == "path:" + chrome.ToLowerInvariant());
        Assert.Equal(2, chromeGroup.Processes.Count);
        Assert.Equal("Google Chrome", chromeGroup.DisplayName);

        var teamsGroup = Assert.Single(groups, g => g.Key == "path:" + teams.ToLowerInvariant());
        Assert.Equal(2, teamsGroup.Processes.Count); // WebView2 приписан к Teams
        Assert.DoesNotContain(groups, g => g.Key.Contains("msedgewebview2"));

        var svc = Assert.Single(groups, g => g.Key == "svc:wuauserv");
        Assert.Equal(AppKind.Service, svc.Kind);
        Assert.True(svc.IsCritical);

        Assert.Contains(groups, g => g.Key == "sys:system" && g.IsCritical);
    }

    [Fact]
    public void HelperIsNotAttachedToExplorer()
    {
        var src = new FakeSource();
        var snap = new RawSnapshot
        {
            Processes = new[]
            {
                P(50, 4, "explorer.exe", 2),
                P(60, 50, "conhost.exe", 3),
            },
        };
        var groups = new AppGrouper(@"C:\Windows").Build(snap, new Dictionary<int, ProcessRate>(), src);
        Assert.Contains(groups, g => g.Key == "name:conhost.exe");
    }
}

public class EngineTests
{
    [Fact]
    public void DemoEngineProducesFrames()
    {
        using var engine = new MonitorEngine(new DemoSystemSource(), @"C:\Windows");
        engine.Tick();
        Thread.Sleep(300);
        var frame = engine.Tick();

        Assert.True(frame.IsDemo);
        Assert.NotEmpty(frame.Apps);
        var game = Assert.Single(frame.Apps, a => a.DisplayName == "Cyberpunk 2077");
        Assert.True(game.CpuPercent > 5, $"CPU игры: {game.CpuPercent}");
        Assert.True(game.GpuPercent > 50);

        var teams = Assert.Single(frame.Apps, a => a.DisplayName == "Microsoft Teams");
        Assert.Equal(3, teams.Processes.Count); // сам Teams + два WebView2

        Assert.Contains(frame.Apps, a => a.Key == "svc:wuauserv");
        Assert.True(frame.Memory.Programs > 0);
        Assert.True(frame.Memory.InUse <= frame.Memory.Total);
        Assert.NotEmpty(frame.Verdicts);
        Assert.Equal(2, frame.History.Cpu.Length);
    }

    [Fact]
    public void MemoryBreakdownAddsUp()
    {
        var m = new MemoryState
        {
            TotalPhysical = 16_000,
            InstalledPhysical = 16_384,
            Available = 6_000,
            Standby = 5_000,
            Modified = 500,
            Free = 1_000,
        };
        var processes = new[]
        {
            new ProcessSample(10, 0, "a.exe", 1, 0, 4_000, 5_000, 4_500, 0, 0, 0, 0, 1, 1),
            new ProcessSample(11, 0, "Memory Compression", 1, 0, 300, 700, 0, 0, 0, 0, 0, 1, 0),
        };
        var b = MemoryMath.Compute(m, processes);
        Assert.Equal(4_000, b.Programs);
        Assert.Equal(700, b.Compressed);
        Assert.Equal(16_000 - 6_000 - 500 - 4_000 - 700, b.System);
        Assert.Equal(m.TotalPhysical, b.Programs + b.System + b.Compressed + b.Modified + b.Standby + b.Free);
        Assert.Equal(384, b.HardwareReserved);
    }
}
