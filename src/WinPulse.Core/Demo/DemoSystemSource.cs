using WinPulse.Core.Model;

namespace WinPulse.Core.Demo;

/// <summary>
/// Правдоподобная выдуманная система: игра, браузер, Teams с утечкой памяти, живые обои,
/// периодические проверки антивируса. Нужна для запуска с ключом --demo, скриншотов и тестов.
/// </summary>
public sealed class DemoSystemSource : ISystemSource
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    private sealed class Proc
    {
        public int Pid;
        public int ParentPid;
        public string Name = "";
        public string? Path;
        public string? Description;
        public string? Company;
        public long Create;
        public double Cpu;
        public double CpuWave;
        public double Memory;
        public double MemoryGrowth;
        public double Gpu;
        public long GpuMemory;
        public double ReadBps;
        public double WriteBps;
        public int Threads;
        public int Handles;
        public bool OnSecondDisk;
        public double Phase;
        public double CpuTime;
        public double ReadTotal;
        public double WriteTotal;
        public double CurrentGpu;
        public double CurrentRead;
        public double CurrentWrite;
    }

    private readonly List<Proc> _procs = new();
    private readonly Dictionary<int, IReadOnlyList<ServiceEntry>> _services = new();
    private readonly Random _rnd = new(42);
    private readonly DateTime _bootTime;
    private DateTime _last;
    private double _t;
    private int _nextPid = 400;
    private double _netRxTotal;

    public DemoSystemSource()
    {
        Info = new SystemInfo
        {
            MachineName = "DEMO-PC",
            OsName = "Windows 10 Pro 22H2",
            OsBuild = 19045,
            CpuName = "AMD Ryzen 5 5600X 6-Core Processor",
            LogicalCores = 12,
            InstalledMemory = 16 * GB,
        };
        _last = DateTime.Now;
        _bootTime = _last.AddHours(-26.3);
        Build();
    }

    public SystemInfo Info { get; }
    public bool IsDemo => true;
    public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> ServicesByPid => _services;

    private Proc Add(string name, string? path, string? description, string? company,
        double cpu = 0.1, double memoryMb = 20, int parent = 0, double cpuWave = 0, double gpu = 0, double gpuMemMb = 0,
        double readKb = 0, double writeKb = 0, int threads = 12, int handles = 300, bool secondDisk = false, int? pid = null)
    {
        var p = new Proc
        {
            Pid = pid ?? (_nextPid += 4 + _rnd.Next(0, 40) * 4),
            ParentPid = parent,
            Name = name,
            Path = path,
            Description = description,
            Company = company,
            Create = 133_700_000_000_000_000L + _procs.Count * 10_000_000L,
            Cpu = cpu,
            CpuWave = cpuWave,
            Memory = memoryMb * MB,
            Gpu = gpu,
            GpuMemory = (long)(gpuMemMb * MB),
            ReadBps = readKb * 1024,
            WriteBps = writeKb * 1024,
            Threads = threads,
            Handles = handles,
            OnSecondDisk = secondDisk,
            Phase = _rnd.NextDouble() * Math.PI * 2,
        };
        _procs.Add(p);
        return p;
    }

    private void Service(string name, string display, double cpu = 0.05, double memoryMb = 15)
    {
        var p = Add("svchost.exe", @"C:\Windows\System32\svchost.exe", "Хост-процесс для служб Windows", "Microsoft Corporation",
            cpu: cpu, memoryMb: memoryMb, parent: ServicesPid, threads: 8, handles: 250);
        _services[p.Pid] = new[] { new ServiceEntry(name, display) };
    }

    private int ServicesPid => _procs.First(p => p.Name == "services.exe").Pid;

    private void Build()
    {
        const string ms = "Microsoft Corporation";
        const string sys32 = @"C:\Windows\System32\";

        Add("System", null, null, null, cpu: 0.6, cpuWave: 0.3, memoryMb: 0.2, threads: 210, handles: 5200, pid: 4);
        Add("Registry", null, null, null, memoryMb: 55, threads: 4, handles: 0, pid: 132);
        Add("smss.exe", sys32 + "smss.exe", null, ms, memoryMb: 1, parent: 4, threads: 2, handles: 55);
        Add("Memory Compression", null, null, null, cpu: 0.2, memoryMb: 460, parent: 4, threads: 46, handles: 0);
        Add("csrss.exe", sys32 + "csrss.exe", null, ms, cpu: 0.2, memoryMb: 2, threads: 14, handles: 800);
        Add("wininit.exe", sys32 + "wininit.exe", null, ms, memoryMb: 2);
        Add("services.exe", sys32 + "services.exe", null, ms, memoryMb: 6, threads: 9, handles: 900);
        Add("lsass.exe", sys32 + "lsass.exe", null, ms, cpu: 0.1, memoryMb: 9, threads: 10, handles: 1500);

        Service("wuauserv", "Центр обновления Windows", memoryMb: 38);
        Service("Dnscache", "DNS-клиент", memoryMb: 9);
        Service("AudioSrv", "Windows Audio", cpu: 0.3, memoryMb: 12);
        Service("BITS", "Фоновая интеллектуальная служба передачи (BITS)", memoryMb: 7);
        Service("SysMain", "SysMain", cpu: 0.2, memoryMb: 70);
        Service("CDPUserSvc", "Служба платформы подключённых устройств", memoryMb: 14);

        Add("dwm.exe", sys32 + "dwm.exe", "Диспетчер окон рабочего стола", ms, cpu: 1.4, cpuWave: 0.5, memoryMb: 140, gpu: 3, gpuMemMb: 260, threads: 22, handles: 1900);
        var explorer = Add("explorer.exe", @"C:\Windows\explorer.exe", "Проводник", ms, cpu: 0.3, cpuWave: 0.2, memoryMb: 150, threads: 90, handles: 3600);
        Add("MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", "Antimalware Service Executable", ms,
            cpu: 0.4, memoryMb: 210, threads: 40, handles: 1100);
        Add("SearchIndexer.exe", sys32 + "SearchIndexer.exe", "Индексатор Microsoft Windows Search", ms, cpu: 0.2, memoryMb: 45, writeKb: 120);

        const string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var chrome = Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 1.2, cpuWave: 0.6, memoryMb: 260, parent: explorer.Pid, threads: 45, handles: 1800);
        Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 1.1, cpuWave: 0.8, memoryMb: 190, parent: chrome.Pid, gpu: 4, gpuMemMb: 320);
        Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 2.6, cpuWave: 1.5, memoryMb: 430, parent: chrome.Pid);
        Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 0.4, memoryMb: 160, parent: chrome.Pid);
        Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 0.2, memoryMb: 120, parent: chrome.Pid);
        Add("chrome.exe", chromePath, "Google Chrome", "Google LLC", cpu: 0.1, memoryMb: 60, parent: chrome.Pid);

        var teams = Add("ms-teams.exe", @"C:\Program Files\WindowsApps\MSTeams_24295.605.3225.8804_x64__8wekyb3d8bbwe\ms-teams.exe",
            "Microsoft Teams", ms, cpu: 0.8, cpuWave: 0.4, memoryMb: 640, parent: explorer.Pid, threads: 60, handles: 1700);
        teams.MemoryGrowth = 0.16 * MB; // ≈ 560 МБ в час — утечка
        const string webview = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.80\msedgewebview2.exe";
        Add("msedgewebview2.exe", webview, "Microsoft Edge WebView2", ms, cpu: 0.9, cpuWave: 0.5, memoryMb: 210, parent: teams.Pid, gpu: 1, gpuMemMb: 120);
        Add("msedgewebview2.exe", webview, "Microsoft Edge WebView2", ms, cpu: 0.3, memoryMb: 95, parent: teams.Pid);

        const string discordPath = @"C:\Users\demo\AppData\Local\Discord\app-1.0.9178\Discord.exe";
        var discord = Add("Discord.exe", discordPath, "Discord", "Discord Inc.", cpu: 0.5, cpuWave: 0.3, memoryMb: 180, parent: explorer.Pid);
        Add("Discord.exe", discordPath, "Discord", "Discord Inc.", cpu: 0.6, memoryMb: 240, parent: discord.Pid, gpu: 2, gpuMemMb: 150);
        Add("Discord.exe", discordPath, "Discord", "Discord Inc.", cpu: 0.1, memoryMb: 70, parent: discord.Pid);

        Add("wallpaper64.exe", @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", "Wallpaper Engine",
            "Wallpaper Engine Team", cpu: 2.8, cpuWave: 0.8, memoryMb: 230, parent: explorer.Pid, gpu: 13, gpuMemMb: 380);

        var steam = Add("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", "Steam", "Valve Corporation", cpu: 0.4, memoryMb: 150, parent: explorer.Pid);
        Add("steamwebhelper.exe", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", "Steam Client WebHelper", "Valve Corporation",
            cpu: 0.6, memoryMb: 210, parent: steam.Pid);
        Add("Cyberpunk2077.exe", @"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077", "CD PROJEKT RED",
            cpu: 34, cpuWave: 6, memoryMb: 5600, parent: steam.Pid, gpu: 78, gpuMemMb: 5300, readKb: 4200, writeKb: 60,
            threads: 140, handles: 2900, secondDisk: true);

        Add("OneDrive.exe", @"C:\Users\demo\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "Microsoft OneDrive", ms, cpu: 0.1, memoryMb: 55, parent: explorer.Pid);
        Add("WinPulse.exe", @"C:\Tools\WinPulse\WinPulse.exe", "WinPulse", "WinPulse", cpu: 0.6, memoryMb: 95, parent: explorer.Pid);
    }

    public RawSnapshot Capture()
    {
        var now = DateTime.Now;
        double dt = Math.Clamp((now - _last).TotalSeconds, 0.2, 5);
        _last = now;
        _t += dt;

        bool defenderScan = _t % 90 < 15;
        bool steamDownload = _t % 150 is > 60 and < 95;
        int cores = Info.LogicalCores;

        double cpuSum = 0;
        double readC = 0, writeC = 0, readD = 0, writeD = 0;
        var samples = new List<ProcessSample>(_procs.Count);
        var gpuByPid = new Dictionary<int, GpuProcessState>();

        foreach (var p in _procs)
        {
            double cpu = p.Cpu + p.CpuWave * Math.Sin(_t / 7.0 + p.Phase) + (_rnd.NextDouble() - 0.5) * 0.3 * Math.Max(0.2, p.CpuWave);
            double read = p.ReadBps * (0.7 + _rnd.NextDouble() * 0.6);
            double write = p.WriteBps * (0.5 + _rnd.NextDouble());
            if (p.Name == "MsMpEng.exe" && defenderScan)
            {
                cpu = 14 + _rnd.NextDouble() * 6;
                read = 38 * MB * (0.8 + _rnd.NextDouble() * 0.4);
            }
            if (p.Name == "steam.exe" && steamDownload)
            {
                cpu = 3 + _rnd.NextDouble();
                write = 17 * MB;
            }
            cpu = Math.Max(0, cpu);
            cpuSum += cpu;
            p.CpuTime += cpu / 100.0 * cores * dt * 1e7;
            p.ReadTotal += read * dt;
            p.WriteTotal += write * dt;
            p.Memory += p.MemoryGrowth * dt;
            p.CurrentRead = read;
            p.CurrentWrite = write;
            if (p.OnSecondDisk) { readD += read; writeD += write; }
            else { readC += read; writeC += write; }

            if (p.Gpu > 0 || p.GpuMemory > 0)
            {
                p.CurrentGpu = Math.Clamp(p.Gpu + (p.Gpu > 20 ? 6 : 1) * Math.Sin(_t / 5.0 + p.Phase), 0, 100);
                gpuByPid[p.Pid] = new GpuProcessState(p.CurrentGpu, p.GpuMemory);
            }

            long mem = (long)p.Memory;
            samples.Add(new ProcessSample(
                p.Pid, p.ParentPid, p.Name, p.Create, (long)p.CpuTime,
                PrivateWorkingSet: mem, WorkingSet: (long)(mem * 1.25), PrivateBytes: (long)(mem * 1.1),
                ReadBytes: (long)p.ReadTotal, WriteBytes: (long)p.WriteTotal, OtherBytes: 0,
                Handles: p.Handles, Threads: p.Threads, SessionId: p.Pid < 600 ? 0 : 1));
        }

        double utility = Math.Clamp(cpuSum + 0.8, 0, 100);

        long total = 16 * GB - 140 * MB;
        long programs = 0;
        long compressed = 0;
        foreach (var s in samples)
        {
            if (s.Name == "Memory Compression") compressed += s.WorkingSet;
            else if (s.Name is not ("System" or "Registry")) programs += s.PrivateWorkingSet;
        }
        long system = (long)(2.1 * GB);
        long modified = (long)(180 * MB + 40 * MB * Math.Sin(_t / 11));
        long inUse = programs + compressed + system + modified;
        long available = Math.Max(300 * MB, total - inUse);
        long free = (long)(available * 0.22);

        var memory = new MemoryState
        {
            TotalPhysical = total,
            InstalledPhysical = 16 * GB,
            Available = available,
            Standby = available - free,
            Modified = modified,
            Free = free,
            CommitTotal = inUse + (long)(4.2 * GB),
            CommitLimit = total + 8 * GB,
            KernelPaged = 640 * MB,
            KernelNonPaged = 310 * MB,
            HardFaultsPerSec = 15 + (defenderScan ? 120 : 0) + _rnd.Next(0, 25),
        };

        var cpuState = new CpuState
        {
            UtilityPercent = utility,
            PerformancePercent = 112 + 6 * Math.Sin(_t / 9),
            NominalMHz = 3701,
            ProcessCount = samples.Count,
            ThreadCount = samples.Sum(s => s.Threads),
            HandleCount = samples.Sum(s => s.Handles),
            Uptime = now - _bootTime,
        };

        var disks = new List<DiskState>
        {
            new()
            {
                Index = 0, Model = "Samsung SSD 980 PRO 1TB", MediaType = DiskMediaType.Nvme, Bus = "NVMe",
                Volumes = new[] { new VolumeState("C:", "Система", 1000 * GB, 310 * GB) },
                ActivePercent = Math.Min(100, 2 + (readC + writeC) / (40 * MB) * 45),
                LatencyMs = 0.3 + _rnd.NextDouble() * 0.3 + (defenderScan ? 0.8 : 0),
                ReadBps = readC, WriteBps = writeC, QueueLength = defenderScan ? 1 : 0,
            },
            new()
            {
                Index = 1, Model = "ST2000DM008-2FR102", MediaType = DiskMediaType.Hdd, Bus = "SATA",
                Volumes = new[] { new VolumeState("D:", "Игры", 2000 * GB, 640 * GB) },
                ActivePercent = Math.Min(100, 6 + (readD + writeD) / MB * 2.2),
                LatencyMs = 7 + _rnd.NextDouble() * 5,
                ReadBps = readD, WriteBps = writeD, QueueLength = 0,
            },
        };

        double rx = 140 * 1024 * (0.6 + _rnd.NextDouble() * 0.8) + (steamDownload ? 18 * MB : 0);
        double tx = 40 * 1024 * (0.6 + _rnd.NextDouble() * 0.8);
        _netRxTotal += rx * dt;
        var network = new List<NetworkAdapterState>
        {
            new()
            {
                Id = "{DEMO-ETH}", Name = "Ethernet", Description = "Realtek Gaming 2.5GbE Family Controller", Kind = "Ethernet",
                SpeedBitsPerSec = 2_500_000_000, ReceiveBps = rx, SendBps = tx,
            },
        };

        double gpu3d = Math.Clamp(gpuByPid.Values.Sum(g => g.UtilPercent), 0, 100);
        long gpuMem = gpuByPid.Values.Sum(g => g.DedicatedBytes) + 420 * MB;
        var gpus = new List<GpuAdapterState>
        {
            new()
            {
                Luid = "0x00000000_0x0000d1a2", Name = "NVIDIA GeForce RTX 3050", VendorId = 0x10DE,
                DedicatedTotal = 8 * GB, DedicatedUsed = gpuMem, UtilPercent = gpu3d,
                EngineUtil = new Dictionary<string, double>
                {
                    ["3D"] = gpu3d,
                    ["VideoDecode"] = 3 + _rnd.NextDouble() * 2,
                    ["Copy"] = 1 + _rnd.NextDouble(),
                    ["VideoEncode"] = 0,
                },
            },
        };

        return new RawSnapshot
        {
            Timestamp = now,
            Processes = samples,
            Cpu = cpuState,
            Memory = memory,
            Disks = disks,
            Network = network,
            Gpus = gpus,
            GpuByPid = gpuByPid,
        };
    }

    public AppIdentity Identify(ProcessSample process)
    {
        var p = _procs.FirstOrDefault(x => x.Pid == process.Pid);
        return p is null ? new AppIdentity(null, null, null) : new AppIdentity(p.Path, p.Description, p.Company);
    }

    /// <summary>Правдоподобные датчики для демо: игра нагружает видеокарту, температуры «дышат».</summary>
    public SensorSnapshot? ReadSensors()
    {
        double t = (DateTime.Now - DateTime.Today).TotalSeconds;
        return new SensorSnapshot
        {
            CpuTemp = 62 + 4 * Math.Sin(t / 23),
            CpuPower = 58 + 9 * Math.Sin(t / 17),
            GpuTemp = 68 + 3 * Math.Sin(t / 31),
            GpuHotspot = 80 + 3 * Math.Sin(t / 31),
            GpuPower = 112 + 6 * Math.Sin(t / 13),
            GpuClockMHz = 1845 + 30 * Math.Sin(t / 19),
            GpuFanPercent = 54 + 4 * Math.Sin(t / 29),
        };
    }

    public void Dispose() { }
}
