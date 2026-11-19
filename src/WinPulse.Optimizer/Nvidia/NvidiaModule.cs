using WinPulse.Collectors.Gpu;
using WinPulse.Core;
using WinPulse.Core.Display;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.Optimizer.Nvidia;

/// <summary>
/// «Драйвер NVIDIA»: настройки профиля игры, которые реально влияют на плавность и задержку
/// (то же, что «Управление параметрами 3D» в панели NVIDIA и NVIDIA Profile Inspector), — с резервной копией и откатом.
/// </summary>
public sealed class NvidiaModule : FixModule
{
    public NvidiaModule(string? backupPath = null)
        : base("nvidia", new FixItem[]
        {
            new NvPowerModeItem(),
            new NvLowLatencyItem(),
            new NvFrameCapItem(),
            new NvShaderCacheItem(),
        }, backupPath)
    {
    }

    /// <summary>Общие проверки: NVIDIA ли видеокарта, загрузился ли драйвер, выбрана ли игра.</summary>
    internal static FixState? Precheck(FixContext ctx, bool needsGame)
    {
        if (ctx.Vendor != GpuVendor.Nvidia)
            return FixState.Unavailable(Lang.T("Видеокарта не NVIDIA — эти настройки только для драйвера NVIDIA.", "Not an NVIDIA GPU — these settings are for the NVIDIA driver only."));
        if (!NvidiaDrs.Available)
            return FixState.Unavailable(Lang.T("Драйвер NVIDIA не ответил (NVAPI не загрузился). Обновите драйвер с сайта NVIDIA.", "The NVIDIA driver didn't respond (NVAPI failed to load). Update the driver from NVIDIA's site."));
        if (needsGame && ctx.GameExe is null) return FixState.NeedGame();
        return null;
    }

    internal static string Exe(FixContext ctx) => Path.GetFileName(ctx.GameExe!);

    internal static string GameName(FixContext ctx) => Path.GetFileNameWithoutExtension(ctx.GameExe!);

    internal static FixState Unsupported(uint id) => FixState.Unavailable(Lang.T(
        $"Эта версия драйвера не знает настройку 0x{id:X8} — WinPulse её не трогает. Обновите драйвер NVIDIA.",
        $"This driver version doesn't know setting 0x{id:X8} — WinPulse leaves it alone. Update the NVIDIA driver."));

    internal static Change Set(FixItem item, string title, string? exe, string? appName, uint id, uint value, string before, string after, string why) => new()
    {
        ItemId = item.Id,
        Kind = ChangeKind.NvidiaSetting,
        NvApp = exe,
        NvAppName = appName,
        NvSettingId = id,
        NvValue = value,
        Title = title,
        Before = before,
        After = after,
        Why = why,
    };

    internal static string Technical(string? exe, uint id, uint? value) =>
        $"NVIDIA DRS · {(exe ?? "Base Profile")} · 0x{id:X8} ({NvidiaDrs.SettingName(id) ?? "?"}) = {(value is null ? Lang.T("по умолчанию", "default") : $"0x{value:X8} ({value})")}";
}

// 1. Режим управления электропитанием
public sealed class NvPowerModeItem : FixItem
{
    public override string Id => "nv-power";
    public override int Number => 1;
    public override string Title => Lang.T("Питание видеокарты в игре: максимальная производительность", "GPU power in the game: prefer maximum performance");
    public override string Explanation => Lang.T(
        "По умолчанию драйвер снижает частоту видеокарты, как только сцена становится «лёгкой», и поднимает обратно с задержкой — " +
        "в момент, когда нагрузка резко растёт, это даёт рывки времени кадра. «Максимальная производительность» держит частоты высокими, " +
        "но только пока запущена эта игра: на рабочем столе карта по-прежнему экономит энергию.",
        "By default the driver drops GPU clocks as soon as a scene gets light and raises them with a delay — " +
        "when the load spikes, that shows up as frame-time hitches. “Prefer maximum performance” keeps clocks high, " +
        "but only while this game runs: on the desktop the card still saves power.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (NvidiaModule.Precheck(ctx, needsGame: true) is { } pre) return pre;
        if (!NvidiaDrs.IsKnown(NvidiaDrs.PowerMode, "power")) return NvidiaModule.Unsupported(NvidiaDrs.PowerMode);
        string exe = NvidiaModule.Exe(ctx);
        uint? value = NvidiaDrs.ReadEffective(exe, NvidiaDrs.PowerMode);
        string technical = NvidiaModule.Technical(exe, NvidiaDrs.PowerMode, value);
        if (value == NvidiaDrs.PowerModeMax)
            return new FixState { Status = FixStatus.Ok, StatusText = Lang.T("Максимальная производительность", "Prefer maximum performance"), Technical = technical };
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = value switch
            {
                0 => Lang.T("Адаптивный", "Adaptive"),
                5 => Lang.T("Оптимальное энергопотребление", "Optimal power"),
                null => Lang.T("По умолчанию драйвера", "Driver default"),
                _ => Lang.T($"Режим {value}", $"Mode {value}"),
            },
            CanFix = true,
            FixLabel = Lang.T("Включить для игры", "Enable for the game"),
            Recommended = true,
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        string exe = NvidiaModule.Exe(ctx);
        return
        [
            NvidiaModule.Set(this, Title, exe, NvidiaModule.GameName(ctx), NvidiaDrs.PowerMode, NvidiaDrs.PowerModeMax,
                Lang.T("по умолчанию / адаптивный", "default / adaptive"), Lang.T("максимальная производительность", "prefer maximum performance"),
                Lang.T($"Только для {exe}: частоты не будут проседать в лёгких сценах.", $"Only for {exe}: clocks won't dip in light scenes.")),
        ];
    }
}

// 2. Режим низкой задержки
public sealed class NvLowLatencyItem : FixItem
{
    public override string Id => "nv-latency";
    public override int Number => 2;
    public override string Title => Lang.T("Режим низкой задержки", "Low latency mode");
    public override string Explanation => Lang.T(
        "Процессор может готовить кадры наперёд, и они стоят в очереди к видеокарте — от этого растёт задержка между движением мыши и картинкой. " +
        "Режим низкой задержки «Вкл.» ограничивает очередь одним кадром. Сильнее всего помогает, когда игра упирается в видеокарту. " +
        "Если в игре есть NVIDIA Reflex — включите его в игре: он работает лучше и сам перекрывает эту настройку. Цена: в играх, упирающихся в процессор, FPS может стать на пару кадров ниже.",
        "The CPU can prepare frames ahead and queue them for the GPU — that adds delay between mouse movement and the picture. " +
        "Low latency mode “On” limits the queue to one frame. It helps most when the game is GPU-bound. " +
        "If the game has NVIDIA Reflex, enable it in-game: it works better and overrides this setting. Cost: in CPU-bound games FPS may drop by a couple of frames.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (NvidiaModule.Precheck(ctx, needsGame: true) is { } pre) return pre;
        if (!NvidiaDrs.IsKnown(NvidiaDrs.PreRenderLimit, "pre-render", "prerender", "latency")) return NvidiaModule.Unsupported(NvidiaDrs.PreRenderLimit);
        string exe = NvidiaModule.Exe(ctx);
        uint? value = NvidiaDrs.ReadEffective(exe, NvidiaDrs.PreRenderLimit);
        string technical = NvidiaModule.Technical(exe, NvidiaDrs.PreRenderLimit, value);
        if (value == 1)
            return new FixState { Status = FixStatus.Ok, StatusText = Lang.T("Вкл. (1 кадр)", "On (1 frame)"), Technical = technical };
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = value is null or 0 ? Lang.T("Выкл. (решает игра)", "Off (app-controlled)") : Lang.T($"Очередь {value} кадра", $"Queue of {value} frames"),
            CanFix = true,
            FixLabel = Lang.T("Включить для игры", "Enable for the game"),
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        string exe = NvidiaModule.Exe(ctx);
        return
        [
            NvidiaModule.Set(this, Title, exe, NvidiaModule.GameName(ctx), NvidiaDrs.PreRenderLimit, 1,
                Lang.T("выкл. (решает игра)", "off (app-controlled)"), Lang.T("вкл. — 1 кадр в очереди", "on — 1 queued frame"),
                Lang.T($"Только для {exe}: меньше задержка ввода.", $"Only for {exe}: lower input latency.")),
        ];
    }
}

// 3. Ограничение кадров драйвером (при G-SYNC / FreeSync)
public sealed class NvFrameCapItem : FixItem
{
    public override string Id => "nv-fpscap";
    public override int Number => 3;
    public override string Title => Lang.T("Ограничение кадров на 3 ниже частоты монитора", "Frame cap 3 below the monitor's refresh rate");
    public override string Explanation => Lang.T(
        "С G-SYNC или FreeSync монитор подстраивается под игру — но только пока FPS ниже его частоты. Стоит FPS упереться в потолок, " +
        "включается обычная вертикальная синхронизация с её задержкой (или разрывы картинки). Ограничение на 3 кадра ниже частоты держит игру " +
        "внутри диапазона VRR: картинка плавная, задержка минимальная. Ставится драйвером только для этой игры.",
        "With G-SYNC or FreeSync the monitor follows the game — but only while FPS stays below its refresh rate. Once FPS hits the ceiling, " +
        "regular VSync kicks in with its latency (or tearing). Capping 3 frames below the refresh rate keeps the game inside the VRR range: " +
        "smooth picture, minimal latency. The driver applies it to this game only.");

    private static int? Target(FixContext ctx) => ctx.GameMonitor is { } m && m.MaxRefreshHz > 60 ? m.MaxRefreshHz - 3 : null;

    public override FixState Evaluate(FixContext ctx)
    {
        if (NvidiaModule.Precheck(ctx, needsGame: true) is { } pre) return pre;
        if (ctx.BestVrr == VrrSupport.No)
            return FixState.Unavailable(Lang.T("Монитор без G-SYNC / FreeSync: ограничение кадров здесь — дело вкуса, а не плавности.",
                "The monitor has no G-SYNC / FreeSync: a frame cap here is a matter of taste, not smoothness."));
        if (Target(ctx) is not int target)
            return FixState.Unavailable(Lang.T("Частота монитора 60 Гц или неизвестна.", "The monitor runs at 60 Hz or its rate is unknown."));
        if (!NvidiaDrs.IsKnown(NvidiaDrs.FrameRateLimit, "frame rate", "fps", "frl")) return NvidiaModule.Unsupported(NvidiaDrs.FrameRateLimit);

        string exe = NvidiaModule.Exe(ctx);
        uint? value = NvidiaDrs.ReadEffective(exe, NvidiaDrs.FrameRateLimit);
        string technical = NvidiaModule.Technical(exe, NvidiaDrs.FrameRateLimit, value);
        if (value is > 0 && value <= (uint)target)
            return new FixState { Status = FixStatus.Ok, StatusText = Lang.T($"{value} FPS", $"{value} FPS"), Technical = technical };
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = value is null or 0 ? Lang.T("Без ограничения", "No cap") : Lang.T($"{value} FPS — выше частоты", $"{value} FPS — above the refresh rate"),
            Reason = Lang.T($"Для монитора {ctx.GameMonitor!.MaxRefreshHz} Гц — {target} FPS.", $"For a {ctx.GameMonitor!.MaxRefreshHz} Hz monitor — {target} FPS."),
            CanFix = true,
            FixLabel = Lang.T($"Ограничить {target} FPS", $"Cap at {target} FPS"),
            Recommended = true,
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        if (Target(ctx) is not int target) return new();
        string exe = NvidiaModule.Exe(ctx);
        return
        [
            NvidiaModule.Set(this, Title, exe, NvidiaModule.GameName(ctx), NvidiaDrs.FrameRateLimit, (uint)target,
                Lang.T("без ограничения", "no cap"), $"{target} FPS",
                Lang.T($"Только для {exe}: игра остаётся в диапазоне G-SYNC/FreeSync.", $"Only for {exe}: the game stays inside the G-SYNC/FreeSync range.")),
        ];
    }
}

// 4. Кэш шейдеров
public sealed class NvShaderCacheItem : FixItem
{
    private const uint TargetMb = 10 * 1024;
    private const uint Unlimited = 0xFFFFFFFF;

    public override string Id => "nv-shadercache";
    public override int Number => 4;
    public override string Title => Lang.T("Кэш шейдеров не меньше 10 ГБ", "Shader cache of at least 10 GB");
    public override string Explanation => Lang.T(
        "Игры компилируют шейдеры при первой встрече с эффектом — отсюда рывки «в новых местах». Драйвер сохраняет готовые шейдеры на диск, " +
        "но место по умолчанию ограничено, и в больших играх старые шейдеры вытесняются — компиляция и рывки повторяются. " +
        "10 ГБ хватает на несколько больших игр. Настройка общая для всех игр.",
        "Games compile shaders the first time they meet an effect — hence stutters “in new places”. The driver saves compiled shaders to disk, " +
        "but the default space is limited, and in big games old shaders get evicted — compilation and stutters repeat. " +
        "10 GB fits several big games. The setting is global for all games.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (NvidiaModule.Precheck(ctx, needsGame: false) is { } pre) return pre;
        if (!NvidiaDrs.IsKnown(NvidiaDrs.ShaderCacheSize, "shader")) return NvidiaModule.Unsupported(NvidiaDrs.ShaderCacheSize);
        uint? value = NvidiaDrs.ReadEffective(null, NvidiaDrs.ShaderCacheSize);
        string technical = NvidiaModule.Technical(null, NvidiaDrs.ShaderCacheSize, value);
        if (value is Unlimited or >= TargetMb)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = value == Unlimited ? Lang.T("Без ограничения", "Unlimited") : Lang.T($"{value / 1024} ГБ", $"{value / 1024} GB"),
                Technical = technical,
            };
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = value is null ? Lang.T("По умолчанию драйвера", "Driver default") : Lang.T($"{value} МБ", $"{value} MB"),
            CanFix = true,
            FixLabel = Lang.T("Поставить 10 ГБ", "Set 10 GB"),
            Recommended = true,
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx) =>
    [
        NvidiaModule.Set(this, Title, null, null, NvidiaDrs.ShaderCacheSize, TargetMb,
            Lang.T("по умолчанию драйвера", "driver default"), "10 ГБ",
            Lang.T("Меньше повторной компиляции шейдеров и рывков в больших играх.", "Less shader recompilation and stutter in big games.")),
    ];
}
