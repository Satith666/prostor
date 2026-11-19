using WinPulse.Core.Gaming;

namespace WinPulse.Core.Tests;

public class GamingTests
{
    [Fact]
    public void PresentMonCsv_ReadsV2Columns()
    {
        var csv = new PresentMonCsv();
        Assert.Null(csv.Parse("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsBetweenPresents,MsInPresentAPI", out _, out _));
        Assert.True(csv.HasHeader);
        var frame = csv.Parse("game.exe,1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,Application,1.5,5.0,1.9,0.3,6.2,6.1,0.4,6.9,0.2", out string app, out int pid);
        Assert.NotNull(frame);
        Assert.Equal("game.exe", app);
        Assert.Equal(1234, pid);
        Assert.Equal(6.9, frame!.Value.FrameTimeMs, 3);
        Assert.Equal(6.1, frame.Value.GpuBusyMs!.Value, 3);
        Assert.Equal(0, frame.Value.SyncInterval);
    }

    [Fact]
    public void PresentMonCsv_FallsBackToCpuBusyPlusWait()
    {
        var csv = new PresentMonCsv();
        csv.Parse("Application,ProcessID,MsCPUBusy,MsCPUWait,MsGPUBusy", out _, out _);
        Assert.True(csv.HasHeader);
        var frame = csv.Parse("game.exe,7,10.5,6.1,15.0", out _, out _);
        Assert.Equal(16.6, frame!.Value.FrameTimeMs, 3);
    }

    [Fact]
    public void PresentMonCsv_SkipsIncompleteAndBadLines()
    {
        var csv = new PresentMonCsv();
        csv.Parse("Application,ProcessID,MsBetweenPresents,MsGPUActive", out _, out _);
        Assert.Null(csv.Parse("game.exe,7", out _, out _));
        Assert.Null(csv.Parse("game.exe,7,NA,1", out _, out _));
        Assert.NotNull(csv.Parse("game.exe,7,8.3,NA", out _, out _));
    }

    [Fact]
    public void FrameStats_SteadySixtyFps()
    {
        var frames = Enumerable.Repeat(new FrameSample(1000.0 / 60, 1000.0 / 60 * 0.95, 0), 600).ToList();
        var stats = FrameStats.Compute(frames)!;
        Assert.Equal(60, stats.AvgFps, 1);
        Assert.Equal(60, stats.Low1Fps, 1);
        Assert.Equal(0, stats.Stutters);
        Assert.Equal(Bottleneck.Gpu, stats.Bottleneck);
        Assert.InRange(stats.FpsPerSecond.Count, 9, 11);
    }

    [Fact]
    public void FrameStats_CountsStuttersAndLowFps()
    {
        var frames = new List<FrameSample>();
        // Обычные кадры 8–12 мс (неровные, как при упоре в процессор), каждый сотый — подвисание на 60 мс.
        for (int i = 0; i < 1000; i++) frames.Add(new FrameSample(i % 100 == 50 ? 60 : 8 + i % 5, 5));
        var stats = FrameStats.Compute(frames)!;
        Assert.Equal(10, stats.Stutters);
        Assert.True(stats.Low01Fps < 20);
        Assert.True(stats.AvgFps > 90 && stats.AvgFps < 100);
        Assert.Equal(Bottleneck.CpuOrEngine, stats.Bottleneck);
    }

    [Fact]
    public void FrameStats_DetectsCap()
    {
        var frames = Enumerable.Range(0, 500).Select(i => new FrameSample(6.06 + (i % 2) * 0.02, 3.0, 1)).ToList();
        Assert.Equal(Bottleneck.Capped, FrameStats.Compute(frames)!.Bottleneck);
    }

    [Fact]
    public void FrameStats_NeedsEnoughFrames() => Assert.Null(FrameStats.Compute([new FrameSample(10, null)]));

    [Fact]
    public void Percentile_Interpolates()
    {
        Assert.Equal(2.5, FrameStats.Percentile([1, 2, 3, 4], 50), 3);
        Assert.Equal(4, FrameStats.Percentile([1, 2, 3, 4], 100), 3);
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient.exe", true)]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(null, false)]
    public void GameDetector_Folders(string? path, bool expected) => Assert.Equal(expected, GameDetector.LooksLikeGameFolder(path));

    [Fact]
    public void GameDetector_ExcludesLaunchersAndBrowsers()
    {
        Assert.True(GameDetector.IsExcluded(@"C:\Program Files (x86)\Steam\steam.exe", "Steam"));
        Assert.True(GameDetector.IsExcluded(null, "chrome"));
        Assert.False(GameDetector.IsExcluded(@"D:\Games\Witcher3\witcher3.exe", "The Witcher 3"));
    }

    private static GameSessionRecord Record(Func<int, SessionSample> make, int seconds = 300) => new()
    {
        Game = "Test Game",
        StartUtc = new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc),
        EndUtc = new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
        VramTotal = 8L << 30,
        RamTotal = 16L << 30,
        Samples = Enumerable.Range(0, seconds).Select(make).ToList(),
    };

    [Fact]
    public void SessionAnalyzer_FindsGpuBoundAndBackgroundApps()
    {
        var record = Record(i => new SessionSample
        {
            T = i, Cpu = 40, Gpu = 97, MemPercent = 60, VramUsed = 5L << 30, DiskActive = 5, GameCpu = 25, GameGpu = 85,
            Others =
            [
                new SessionApp { Name = "Wallpaper Engine", Cpu = 3, Gpu = 9 },
                new SessionApp { Name = "Google Chrome", Cpu = 6, Gpu = 1 },
            ],
        });
        var report = SessionAnalyzer.Analyze(record);
        Assert.Contains(report.Findings, f => f.Level == "info" && f.Title.Contains("видеокарт", StringComparison.OrdinalIgnoreCase) || f.Title.Contains("GPU"));
        Assert.Contains(report.Findings, f => f.Level == "warn");
        Assert.Equal("Wallpaper Engine", report.Interferers[0].Name);
        Assert.NotNull(report.Interferers[0].Advice);
        Assert.Equal(97, report.AvgGpu, 1);
    }

    [Fact]
    public void SessionAnalyzer_QuietSessionIsGood()
    {
        var record = Record(i => new SessionSample { T = i, Cpu = 50, Gpu = 75, MemPercent = 55, VramUsed = 4L << 30, DiskActive = 3, GameCpu = 45, GameGpu = 74 });
        var report = SessionAnalyzer.Analyze(record);
        Assert.Contains(report.Findings, f => f.Level == "good");
        Assert.Empty(report.Interferers);
    }

    [Fact]
    public void SessionAnalyzer_ReportsFpsDropWithCause()
    {
        var record = Record(i => new SessionSample
        {
            T = i, Cpu = 60, Gpu = 80, MemPercent = 70, DiskActive = 5, Fps = i == 200 ? 40 : 120,
            Others = i == 200 ? [new SessionApp { Name = "Windows Update", Cpu = 30 }] : [],
        });
        var report = SessionAnalyzer.Analyze(record);
        Assert.Contains(report.Moments, m => m.T == 200 && m.Text.Contains("Windows Update"));
        Assert.Equal(120, report.AvgFps!.Value, 0);
    }
}
