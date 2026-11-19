using WinPulse.Collectors.Diagnostics;
using WinPulse.Core.Analysis;
using WinPulse.Core.Diagnostics;
using WinPulse.Core.Model;

namespace WinPulse.Core.Tests;

public class BugCheckTests
{
    [Fact]
    public void ParsesParam1()
    {
        Assert.Equal(0x116u, BugChecks.ParseParam1("0x00000116 (0xffffb40e2c4f9010, 0xfffff8045a5a2f60, 0x0, 0x0)"));
        Assert.Equal(0x9Fu, BugChecks.ParseParam1("0x0000009f"));
        Assert.Null(BugChecks.ParseParam1("ерунда"));
        Assert.Null(BugChecks.ParseParam1(null));
    }

    [Fact]
    public void NormalizesAndDescribes()
    {
        Assert.Equal(0x7Eu, BugChecks.Normalize(0x1000007E));
        Assert.Equal(0xC000021Au, BugChecks.Normalize(0xC000021A));
        Assert.Equal(CrashCause.Graphics, BugChecks.Describe(0x116).Cause);
        Assert.Equal(CrashCause.Hardware, BugChecks.Describe(0x124).Cause);
        Assert.Equal(CrashCause.Unknown, BugChecks.Describe(0x12345).Cause);
        Assert.Equal("0x00000116", BugChecks.Describe(0x116).Hex);
    }
}

public class DetectorTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0);

    private static EventItem Ev(string provider, int id, DateTime time, int level = 2,
        Dictionary<string, string>? named = null, params string[] data) => new()
    {
        Log = "System",
        Provider = provider,
        Id = id,
        Level = level,
        Time = time,
        Named = named ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Data = data.Length > 0 ? data : (named?.Values.ToArray() ?? Array.Empty<string>()),
    };

    private static Dictionary<string, string> N(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void QuietSystemHasNoProblems()
    {
        var report = ProblemDetectors.Run(new DiagnosticInput { Now = Now, Uptime = TimeSpan.FromHours(5) });
        Assert.Empty(report.Problems);
        Assert.Contains(report.Passed, f => f.Id == "bsod");
        Assert.Contains(report.Passed, f => f.Id == "shutdowns");
        Assert.Equal(30, report.Days.Count);
        Assert.Equal(DateOnly.FromDateTime(Now), report.Days[^1].Day);
    }

    [Fact]
    public void BlueScreenIsExplained()
    {
        var crashTime = Now.AddDays(-1);
        var events = new List<EventItem>
        {
            Ev(ProblemDetectors.Wer, 1001, crashTime, 2, N(("param1", "0x00000116 (0xffff, 0x0, 0x0, 0x0)"), ("param2", @"C:\Windows\Minidump\111826-1.dmp"))),
            // Kernel-Power 41 с тем же сбоем — не должен считаться вторым синим экраном
            Ev(ProblemDetectors.KernelPower, 41, crashTime.AddMinutes(-1), 1, N(("BugcheckCode", "278"), ("PowerButtonTimestamp", "0"))),
        };
        var report = ProblemDetectors.Run(new DiagnosticInput { Now = Now, Events = events });

        var bsod = Assert.Single(report.Problems, f => f.Id == "bsod");
        Assert.Equal(Severity.Critical, bsod.Severity);
        Assert.Equal(1, bsod.Count30);
        Assert.Contains("VIDEO_TDR_FAILURE", bsod.Summary);
        Assert.Contains(bsod.Evidence, e => e.Text.Contains("Minidump"));
        Assert.DoesNotContain(report.Problems, f => f.Id == "shutdowns");
        Assert.Equal(2, report.Days.Single(d => d.Day == DateOnly.FromDateTime(crashTime)).Critical);
    }

    [Fact]
    public void UnexpectedShutdownWithoutBugcheck()
    {
        var events = new List<EventItem>
        {
            Ev(ProblemDetectors.KernelPower, 41, Now.AddDays(-3), 1, N(("BugcheckCode", "0"), ("PowerButtonTimestamp", "133771234567890000"))),
        };
        var report = ProblemDetectors.Run(new DiagnosticInput { Now = Now, Events = events });
        var f = Assert.Single(report.Problems, x => x.Id == "shutdowns");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("кнопка питания", f.Summary);
    }

    [Fact]
    public void GameCrashInGpuDriverIsRecognised()
    {
        var events = Enumerable.Range(1, 3).Select(i => new EventItem
        {
            Log = "Application",
            Provider = ProblemDetectors.ApplicationError,
            Id = 1000,
            Level = 2,
            Time = Now.AddHours(-i * 10),
            Data = new[] { "game.exe", "1.0.0.0", "5f0e", "nvwgf2umx.dll", "32.0.15.6094", "66b1", "c0000005", "0x1234", "0x2a0" },
        }).ToList();
        var report = ProblemDetectors.Run(new DiagnosticInput { Now = Now, Events = events });

        var f = Assert.Single(report.Problems, x => x.Id == "crash-game.exe");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal(3, f.Count7);
        Assert.Contains("видеодрайвер", f.Summary);
        Assert.Contains(f.Evidence, e => e.Text.Contains("нарушение доступа"));
    }

    [Fact]
    public void BadBlocksAreCriticalAndNamed()
    {
        var events = new List<EventItem>
        {
            Ev("disk", 7, Now.AddDays(-2), 2, null, @"\Device\Harddisk1\DR1"),
            Ev("stornvme", 129, Now.AddDays(-4), 3, null, @"\Device\RaidPort0"),
        };
        var input = new DiagnosticInput
        {
            Now = Now,
            Events = events,
            DiskNames = new Dictionary<int, string> { [1] = "ST2000DM008" },
        };
        var f = Assert.Single(ProblemDetectors.Run(input).Problems, x => x.Id == "disk-errors");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(f.Evidence, e => e.Text.Contains("ST2000DM008"));
    }

    [Fact]
    public void LowSpaceOnSystemDrive()
    {
        const long GB = 1024L * 1024 * 1024;
        var input = new DiagnosticInput
        {
            Now = Now,
            SystemDrive = "C:",
            Volumes = new[] { new VolumeState("C:", null, 500 * GB, 2 * GB), new VolumeState("D:", "Игры", 2000 * GB, 900 * GB) },
        };
        var report = ProblemDetectors.Run(input);
        var f = Assert.Single(report.Problems, x => x.Id == "space-C:");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.DoesNotContain(report.Problems, x => x.Id == "space-D:");
    }

    [Fact]
    public void GpuCode43IsCritical()
    {
        var input = new DiagnosticInput
        {
            Now = Now,
            DevicesChecked = true,
            Devices = new[]
            {
                new DeviceProblem("NVIDIA GeForce RTX 3050", @"PCI\VEN_10DE&DEV_2507", "Display", 43),
                new DeviceProblem("Bluetooth", @"USB\VID_8087", "Bluetooth", 22),
            },
        };
        var report = ProblemDetectors.Run(input);
        var f = Assert.Single(report.Problems, x => x.Id == "devices");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains(report.Problems, x => x.Id == "devices-disabled" && x.Severity == Severity.Info);
    }

    [Fact]
    public void FailedUpdateThatLaterSucceededIsIgnored()
    {
        var events = new List<EventItem>
        {
            Ev(ProblemDetectors.WindowsUpdate, 20, Now.AddDays(-5), 2, N(("errorCode", "0x80070643"), ("updateTitle", "KB5040000"))),
            Ev(ProblemDetectors.WindowsUpdate, 19, Now.AddDays(-4), 4, N(("updateTitle", "KB5040000"))),
            Ev(ProblemDetectors.WindowsUpdate, 20, Now.AddDays(-3), 2, N(("errorCode", "0x800f0922"), ("updateTitle", "KB5050000"))),
            Ev(ProblemDetectors.WindowsUpdate, 20, Now.AddDays(-1), 2, N(("errorCode", "0x800f0922"), ("updateTitle", "KB5050000"))),
        };
        var f = Assert.Single(ProblemDetectors.Run(new DiagnosticInput { Now = Now, Events = events }).Problems, x => x.Id == "updates");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Single(f.Evidence);
        Assert.Contains("KB5050000", f.Evidence[0].Text);
    }
}

public class CollectorParsingTests
{
    [Fact]
    public void ParsesEventXmlWithNamedData()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Kernel-Power" Guid="{331c3b3a-2005-44c2-ac5e-77220c37d6b4}"/>
                <EventID>41</EventID>
                <Level>1</Level>
                <TimeCreated SystemTime="2026-11-18T21:33:12.5843215Z"/>
              </System>
              <EventData>
                <Data Name="BugcheckCode">0</Data>
                <Data Name="SleepInProgress">0</Data>
                <Data Name="PowerButtonTimestamp">0</Data>
              </EventData>
            </Event>
            """;
        var e = EventLogReader.Parse(xml, "System")!;
        Assert.Equal("Microsoft-Windows-Kernel-Power", e.Provider);
        Assert.Equal(41, e.Id);
        Assert.Equal(1, e.Level);
        Assert.Equal("0", e.Get("BugcheckCode"));
        Assert.Equal(3, e.Data.Count);
        Assert.Equal(new DateTime(2026, 11, 18, 21, 33, 12, DateTimeKind.Utc).ToLocalTime().Date, e.Time.Date);
    }

    [Fact]
    public void ParsesEventXmlWithPositionalData()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Application Error"/>
                <EventID Qualifiers="0">1000</EventID>
                <Level>2</Level>
                <TimeCreated SystemTime="2026-11-18T10:00:00.000Z"/>
              </System>
              <EventData>
                <Data>game.exe</Data>
                <Data>1.0.0.0</Data>
                <Data>5f0e</Data>
                <Data>nvwgf2umx.dll</Data>
              </EventData>
            </Event>
            """;
        var e = EventLogReader.Parse(xml, "Application")!;
        Assert.Equal(1000, e.Id);
        Assert.Equal("game.exe", e.Get("AppName", 0));
        Assert.Equal("nvwgf2umx.dll", e.Get("ModuleName", 3));
    }

    [Fact]
    public void ParsesDiskHealthJson()
    {
        const string named = """[{"Name":"Samsung SSD 980 PRO 1TB","Media":"SSD","Bus":"NVMe","Health":"Healthy","Size":1000204886016,"Wear":3,"Temp":41,"TempMax":0,"ReadErr":0,"Hours":5321}]""";
        var a = Assert.Single(DiskHealthReader.Parse(named)!);
        Assert.Equal("Healthy", a.Health);
        Assert.Equal(3, a.WearPercent);
        Assert.Equal(41, a.Temperature);
        Assert.Equal(5321, a.PowerOnHours);

        const string numeric = """[{"Name":"ST2000DM008","Media":"3","Bus":"11","Health":"1","Size":2000398934016,"Wear":null,"Temp":null,"TempMax":null,"ReadErr":null,"Hours":null}]""";
        var b = Assert.Single(DiskHealthReader.Parse(numeric)!);
        Assert.Equal("HDD", b.Media);
        Assert.Equal("SATA", b.Bus);
        Assert.Null(b.WearPercent);

        var report = ProblemDetectors.Run(new DiagnosticInput
        {
            DiskHealthChecked = true,
            DiskHealth = new[] { a, b },
        });
        Assert.Contains(report.Problems, f => f.Id == "disk-health-ST2000DM008" && f.Severity == Severity.Critical);
    }
}
