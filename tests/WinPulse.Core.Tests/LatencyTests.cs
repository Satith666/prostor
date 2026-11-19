using System.Buffers.Binary;
using System.Text;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Core.Tests;

public class LatencyTests
{
    [Fact]
    public void Aggregator_MapsAddressesToModules()
    {
        var agg = new LatencyAggregator();
        agg.AddModule(0xFFFFF80010000000, 0x100000, @"\SystemRoot\System32\drivers\nvlddmkm.sys");
        agg.AddModule(0xFFFFF80000000000, 0x200000, @"\SystemRoot\system32\ntoskrnl.exe");

        Assert.Equal("nvlddmkm.sys", agg.ModuleOf(0xFFFFF80010000123));
        Assert.Equal("ntoskrnl.exe", agg.ModuleOf(0xFFFFF80000000010));
        Assert.Equal(LatencyAggregator.Unknown, agg.ModuleOf(0xFFFFF80000300000)); // за концом ntoskrnl
        Assert.Equal(LatencyAggregator.Unknown, agg.ModuleOf(0x1000));
    }

    [Fact]
    public void Aggregator_WithoutSize_UsesNextModule()
    {
        var agg = new LatencyAggregator();
        agg.AddModule(0xFFFFF80000000000, 0, "ntoskrnl.exe");
        agg.AddModule(0xFFFFF80001000000, 0, "acpi.sys");
        Assert.Equal("ntoskrnl.exe", agg.ModuleOf(0xFFFFF80000800000));
        Assert.Equal("acpi.sys", agg.ModuleOf(0xFFFFF80001000400));
    }

    [Fact]
    public void Aggregator_BuildsReport()
    {
        var agg = new LatencyAggregator();
        agg.AddModule(0x10000, 0x1000, "a.sys");
        agg.AddModule(0x20000, 0x1000, "b.sys");
        agg.Add(false, 0x10010, 5);
        agg.Add(false, 0x10010, 700);
        agg.Add(true, 0x10020, 30);
        agg.Add(false, 0x20010, 2500);
        agg.Add(false, 0x20010, -3);          // отбрасывается
        agg.Add(false, 0x20010, double.NaN);  // отбрасывается

        var r = agg.Build(10, 0, "etw");
        Assert.Equal(3, r.DpcCount);
        Assert.Equal(1, r.IsrCount);
        Assert.Equal(2500, r.MaxDpcUs);
        Assert.Equal(30, r.MaxIsrUs);
        Assert.Equal("b.sys", r.Worst!.Name);
        var a = r.Drivers.Single(d => d.Name == "a.sys");
        Assert.Equal(2, a.DpcCount);
        Assert.Equal(1, a.IsrCount);
        Assert.Equal(1, a.Over500);
        Assert.Equal(0, a.Over1000);
        Assert.Equal(4, r.Histogram.Sum());
        Assert.Equal(1, r.Histogram[0]);   // ≤ 10 мкс
        Assert.Equal(1, r.Histogram[^1]);  // > 2 мс
    }

    [Fact]
    public void ParseDpc_64bit()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(data, 123456789);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 0xFFFFF80012345678);
        Assert.True(KernelEvents.TryParseDpc(data, true, out long initial, out ulong routine));
        Assert.Equal(123456789, initial);
        Assert.Equal(0xFFFFF80012345678, routine);
        Assert.False(KernelEvents.TryParseDpc(data.AsSpan(0, 12), true, out _, out _));
    }

    [Fact]
    public void ParseImage_64bit()
    {
        const string path = @"\SystemRoot\System32\drivers\nvlddmkm.sys";
        var name = Encoding.Unicode.GetBytes(path + "\0");
        var data = new byte[56 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(data, 0xFFFFF80010000000);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 0x3000000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0);
        name.CopyTo(data, 56);

        Assert.True(KernelEvents.TryParseImage(data, true, out ulong imageBase, out ulong size, out uint pid, out string file));
        Assert.Equal(0xFFFFF80010000000, imageBase);
        Assert.Equal(0x3000000UL, size);
        Assert.Equal(0u, pid);
        Assert.Equal(path, file);
        Assert.Equal("nvlddmkm.sys", LatencyAggregator.FileNameOf(file));
    }

    [Fact]
    public void Opcodes()
    {
        Assert.True(KernelEvents.IsDpc(68));
        Assert.True(KernelEvents.IsDpc(69));
        Assert.True(KernelEvents.IsDpc(66));
        Assert.True(KernelEvents.IsIsr(67));
        Assert.False(KernelEvents.IsDpc(67));
        Assert.False(KernelEvents.IsIsr(68));
    }

    [Theory]
    [InlineData(120, LatencyLevel.Good)]
    [InlineData(500, LatencyLevel.Warn)]
    [InlineData(1999, LatencyLevel.Warn)]
    [InlineData(2000, LatencyLevel.Bad)]
    public void Levels(double us, LatencyLevel level) => Assert.Equal(level, LatencyAdvisor.Level(us));

    [Theory]
    [InlineData("nvlddmkm.sys", true)]
    [InlineData("Netwtw10.sys", true)]
    [InlineData("NDIS.SYS", true)]
    [InlineData("ACPI.sys", true)]
    [InlineData("somethingelse.sys", false)]
    public void KnownDrivers(string name, bool known) => Assert.Equal(known, LatencyAdvisor.About(name) is not null);

    [Fact]
    public void Verdict_NoEvents()
    {
        Lang.English = false;
        Assert.Contains("не удался", LatencyAdvisor.Verdict(new LatencyReport()));
    }

    [Fact]
    public void Demo_HasSpikes()
    {
        var r = WinPulse.Core.Demo.DemoLatency.Create();
        Assert.True(r.DpcCount > 1000);
        Assert.Equal(LatencyLevel.Bad, LatencyAdvisor.Level(r.MaxUs));
        Assert.Equal("Netwtw10.sys", r.Worst!.Name);
    }
}
