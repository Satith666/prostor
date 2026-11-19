using WinPulse.Core.Startup;

namespace WinPulse.Core.Tests;

public class StartupTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent", @"C:\Program Files\Steam\steam.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\Update.exe --processStart Discord.exe", @"C:\Users\me\AppData\Local\Discord\Update.exe")]
    [InlineData(@"C:\PROGRA~1\App\app.exe", @"C:\PROGRA~1\App\app.exe")]
    [InlineData(@"rundll32.exe C:\Windows\System32\shell32.dll,Control_RunDLL", @"C:\Windows\System32\shell32.dll")]
    [InlineData("\"C:\\Tools\\no exe here\"", @"C:\Tools\no exe here")]
    public void ExtractsExecutable(string command, string expected)
    {
        Assert.Equal(expected, CommandLine.ExtractExe(command));
    }

    [Fact]
    public void ExtractExeHandlesEmpty()
    {
        Assert.Null(CommandLine.ExtractExe(""));
        Assert.Null(CommandLine.ExtractExe(null));
    }

    [Fact]
    public void ApprovedFlags()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 0x06, 0, 0, 0 }));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 0x03, 0, 0, 0 }));

        var disabled = StartupApproved.Disabled(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, disabled.Length);
        Assert.False(StartupApproved.IsEnabled(disabled));
        Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(disabled, 4)));
        Assert.True(StartupApproved.IsEnabled(StartupApproved.Enabled()));
    }

    [Fact]
    public void KnowledgeFindsCommonPrograms()
    {
        Assert.Equal(StartupAdvice.CanDisable, StartupKnowledge.Find("Steam", @"C:\Program Files (x86)\Steam\steam.exe").Advice);
        Assert.Equal(StartupAdvice.CanDisable, StartupKnowledge.Find("Discord", @"C:\Users\me\AppData\Local\Discord\Update.exe").Advice);
        Assert.Equal(StartupAdvice.Keep, StartupKnowledge.Find("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe").Advice);
        Assert.Equal(StartupAdvice.Keep, StartupKnowledge.Find("RtkAudUService", @"C:\Windows\System32\RtkAudUService64.exe").Advice);
        Assert.Equal(StartupAdvice.Optional, StartupKnowledge.Find("Что-то своё", @"C:\Tools\mystery.exe").Advice);
    }
}

public class DriverTests
{
    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("27.21.14.5671", "456.71")]
    public void NvidiaVersion(string windows, string nvidia)
    {
        Assert.Equal(nvidia, WinPulse.Core.Drivers.DriverAnalysis.NvidiaVersion(windows));
    }

    [Fact]
    public void BasicDisplayAdapterIsCritical()
    {
        var info = new WinPulse.Core.Drivers.DriverInfo("Microsoft Basic Display Adapter", "Display", "(Standard display types)", "Microsoft",
            "10.0.19041.1", new DateTime(2006, 6, 21), "display.inf", @"PCI\VEN_10DE&DEV_2507&SUBSYS_00000000", 0);
        var row = Assert.Single(WinPulse.Core.Drivers.DriverAnalysis.Analyze(new[] { info }, new DateTime(2026, 11, 19)));
        Assert.Equal(WinPulse.Core.Analysis.Severity.Critical, row.Severity);
        Assert.Equal("NVIDIA", row.Vendor);
    }

    [Fact]
    public void OldGpuDriverIsWarning()
    {
        var info = new WinPulse.Core.Drivers.DriverInfo("NVIDIA GeForce RTX 3050", "Display", "NVIDIA", "NVIDIA",
            "31.0.15.3623", new DateTime(2023, 6, 1), "oem12.inf", @"PCI\VEN_10DE&DEV_2507", 0);
        var row = Assert.Single(WinPulse.Core.Drivers.DriverAnalysis.Analyze(new[] { info }, new DateTime(2026, 11, 19)));
        Assert.Equal(WinPulse.Core.Analysis.Severity.Warning, row.Severity);
        Assert.Equal("536.23", row.FriendlyVersion);
        Assert.Equal(WinPulse.Core.Drivers.DriverAnalysis.NvidiaUrl, row.Link);
    }

    [Fact]
    public void VirtualAdaptersAreSkipped()
    {
        var info = new WinPulse.Core.Drivers.DriverInfo("WAN Miniport (IP)", "Net", "Microsoft", "Microsoft", "10.0", null, null, @"SWD\MSRRAS\MS_NDISWANIP", 0);
        Assert.Empty(WinPulse.Core.Drivers.DriverAnalysis.Analyze(new[] { info }, DateTime.Now));
    }
}
