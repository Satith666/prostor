using System.Diagnostics;
using WinPulse.Core;
using WinPulse.Core.Display;
using WinPulse.Core.Tweaks;
using WinPulse.Collectors.Gpu;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.WindowedGames;

internal static class WgPaths
{
    public const string DirectX = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string DirectXGlobal = "DirectXUserGlobalSettings";
    public const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string GameBar = @"Software\Microsoft\GameBar";
    public const string GameConfigStore = @"System\GameConfigStore";
    public const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string Dwm = @"SOFTWARE\Microsoft\Windows\Dwm";

    public static string Hz => Lang.T("Гц", "Hz");
}

// 1. SwapEffectUpgradeEnable
public sealed class SwapEffectItem : FixItem
{
    private const string Sub = "SwapEffectUpgradeEnable";

    public override string Id => "swap-effect";
    public override int Number => 1;
    public override string Title => Lang.T("Оптимизация для игр в оконном режиме", "Optimizations for windowed games");
    public override string Explanation => Lang.T(
        "Windows 11 переводит игры DirectX 10/11 в окне и без рамки на современный способ вывода кадров (flip model). " +
        "Задержка становится как в полноэкранном режиме, в окне начинают работать VRR и Auto HDR.",
        "Windows 11 moves DirectX 10/11 games running windowed or borderless to the modern flip presentation model. " +
        "Latency drops to fullscreen levels, and VRR and Auto HDR start working in a window.");

    private static string? Current() => Reg.GetSub(RegHive.CurrentUser, WgPaths.DirectX, WgPaths.DirectXGlobal, Sub);

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.Os.Build < 22557)
            return FixState.Unavailable(Lang.T(
                $"Нужна Windows 11 22H2 или новее (сборка 22557+). У вас {ctx.Os.Display}, сборка {ctx.Os.Build}.",
                $"Requires Windows 11 22H2 or newer (build 22557+). You have {ctx.Os.Display}, build {ctx.Os.Build}."));
        string? v = Current();
        bool on = v == "1";
        return new FixState
        {
            Status = on ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = on ? Lang.T("Включено", "On") : Lang.T("Выключено", "Off"),
            Reason = on ? null : Lang.T("После включения перезапустите игру.", "Restart the game after enabling."),
            CanFix = !on,
            Recommended = !on,
            Technical = $@"HKCU\{WgPaths.DirectX} → {WgPaths.DirectXGlobal} → {Sub} = {v ?? Missing}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx) => new()
    {
        RegChange(RegHive.CurrentUser, WgPaths.DirectX, WgPaths.DirectXGlobal, "String", "1",
            Current() ?? Missing, "1",
            Lang.T("Включает «Оптимизацию для игр в оконном режиме» (Параметры → Дисплей → Графика). Подействует после перезапуска игры.",
                "Turns on “Optimizations for windowed games” (Settings → Display → Graphics). Takes effect after the game restarts."),
            subKey: Sub),
    };
}

// 2. VRROptimizeEnable
public sealed class VrrOptimizeItem : FixItem
{
    private const string Sub = "VRROptimizeEnable";

    public override string Id => "vrr-optimize";
    public override int Number => 2;
    public override string Title => Lang.T("Переменная частота (VRR) для игр", "Variable refresh rate for games");
    public override string Explanation => Lang.T(
        "Разрешает Windows включать G-SYNC/FreeSync для игр DirectX 11, которые сами этого не умеют. " +
        "Работает, только если монитор и драйвер поддерживают переменную частоту.",
        "Lets Windows use G-SYNC/FreeSync for DirectX 11 games that don't support it natively. " +
        "Works only when the monitor and driver support variable refresh rate.");

    private static string? Current() => Reg.GetSub(RegHive.CurrentUser, WgPaths.DirectX, WgPaths.DirectXGlobal, Sub);

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.Os.Build < 18362)
            return FixState.Unavailable(Lang.T(
                $"Нужна Windows 10 1903 (сборка 18362) или новее. У вас сборка {ctx.Os.Build}.",
                $"Requires Windows 10 1903 (build 18362) or newer. You have build {ctx.Os.Build}."));
        if (ctx.BestVrr == VrrSupport.No)
            return FixState.Unavailable(Lang.T(
                "Мониторы не сообщают о поддержке переменной частоты (G-SYNC/FreeSync/Adaptive-Sync) — включать нечего.",
                "Your monitors don't report variable refresh rate support (G-SYNC/FreeSync/Adaptive-Sync), so there is nothing to enable."));

        string? v = Current();
        bool on = v == "1";
        string? note = ctx.BestVrr == VrrSupport.Likely
            ? Lang.T("Монитор, скорее всего, поддерживает VRR (по диапазону частот в EDID), но явной метки нет.",
                "The monitor most likely supports VRR (based on the EDID refresh range), but has no explicit flag.")
            : null;
        return new FixState
        {
            Status = on ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = on ? Lang.T("Включено", "On") : Lang.T("Выключено", "Off"),
            Reason = note,
            CanFix = !on,
            Recommended = !on,
            Technical = $@"HKCU\{WgPaths.DirectX} → {WgPaths.DirectXGlobal} → {Sub} = {v ?? Missing}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx) => new()
    {
        RegChange(RegHive.CurrentUser, WgPaths.DirectX, WgPaths.DirectXGlobal, "String", "1",
            Current() ?? Missing, "1",
            Lang.T("Включает «Переменную частоту обновления» для игр (Параметры → Дисплей → Графика). Подействует после перезапуска игры.",
                "Turns on “Variable refresh rate” for games (Settings → Display → Graphics). Takes effect after the game restarts."),
            subKey: Sub),
    };
}

// 3. G-SYNC / FreeSync в окне — инструкция и ручная отметка
public sealed class GsyncItem : FixItem
{
    private const string Mark = "windowed.gsync-done";

    public override string Id => "gsync";
    public override int Number => 3;
    public override string Title => Lang.T("G-SYNC / FreeSync в оконном режиме", "G-SYNC / FreeSync in windowed mode");
    public override string Explanation => Lang.T(
        "Чтобы переменная частота работала в окне без рамки, её надо включить в панели драйвера — и для полноэкранного, и для оконного режима. " +
        "Автоматически это сделать нельзя без вмешательства в профили драйвера, поэтому даём пошаговую инструкцию.",
        "For VRR to work in borderless windows it must be enabled in the driver panel for both fullscreen and windowed mode. " +
        "This can't be done safely without editing driver profiles, so here is a step-by-step guide.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.BestVrr == VrrSupport.No)
            return FixState.Unavailable(Lang.T(
                "Мониторы не сообщают о поддержке G-SYNC/FreeSync/Adaptive-Sync.",
                "Your monitors don't report G-SYNC/FreeSync/Adaptive-Sync support."));

        string monitor = ctx.Monitors.FirstOrDefault(m => m.Vrr != VrrSupport.No)?.FriendlyName ?? Lang.T("ваш монитор", "your monitor");
        var steps = ctx.Vendor switch
        {
            GpuVendor.Nvidia => new List<string>
            {
                Lang.T("Включите FreeSync / Adaptive-Sync в меню самого монитора (кнопки на мониторе), если такой пункт есть.",
                    "Turn on FreeSync / Adaptive-Sync in the monitor's own on-screen menu, if it has one."),
                Lang.T("Откройте «Панель управления NVIDIA» (кнопка ниже или правый клик по рабочему столу).",
                    "Open NVIDIA Control Panel (button below or right-click the desktop)."),
                Lang.T("Слева: «Дисплей» → «Настройка G-SYNC».", "On the left: Display → Set up G-SYNC."),
                Lang.T("Отметьте «Включить G-SYNC, G-SYNC Compatible» и выберите «Включить для оконного и полноэкранного режима».",
                    "Check “Enable G-SYNC, G-SYNC Compatible” and choose “Enable for windowed and full screen mode”."),
                Lang.T($"Выберите монитор «{monitor}» и отметьте «Включить настройки для выбранной модели дисплея». Нажмите «Применить».",
                    $"Select “{monitor}”, check “Enable settings for the selected display model” and click Apply."),
                Lang.T("«Параметры 3D» → «Управление параметрами 3D» → «Технология монитора» = G-SYNC Compatible (или G-SYNC).",
                    "3D Settings → Manage 3D settings → Monitor Technology = G-SYNC Compatible (or G-SYNC)."),
            },
            GpuVendor.Amd => new List<string>
            {
                Lang.T("Включите FreeSync / Adaptive-Sync в меню самого монитора.", "Turn on FreeSync / Adaptive-Sync in the monitor's on-screen menu."),
                Lang.T("Откройте AMD Software: Adrenalin Edition (кнопка ниже или правый клик по рабочему столу).",
                    "Open AMD Software: Adrenalin Edition (button below or right-click the desktop)."),
                Lang.T("«Игры» → «Дисплей» (Gaming → Display).", "Gaming → Display."),
                Lang.T($"Для «{monitor}» включите «AMD FreeSync».", $"For “{monitor}”, turn on AMD FreeSync."),
                Lang.T("FreeSync у AMD работает и в окне без рамки — отдельной галочки не нужно.",
                    "AMD FreeSync works in borderless windows too — no extra checkbox is needed."),
            },
            GpuVendor.Intel => new List<string>
            {
                Lang.T("Включите Adaptive-Sync / FreeSync в меню самого монитора.", "Turn on Adaptive-Sync / FreeSync in the monitor's on-screen menu."),
                Lang.T("Откройте Intel Graphics Software / Arc Control (или Intel Graphics Command Center).",
                    "Open Intel Graphics Software / Arc Control (or Intel Graphics Command Center)."),
                Lang.T("Раздел «Дисплей» → включите «Adaptive Sync».", "Display → turn on Adaptive Sync."),
            },
            _ => new List<string>
            {
                Lang.T("Включите VRR (FreeSync / Adaptive-Sync) в меню монитора и в панели драйвера видеокарты.",
                    "Turn on VRR (FreeSync / Adaptive-Sync) in the monitor menu and in the GPU driver panel."),
            },
        };

        bool done = ctx.IsMarked(Mark);
        var actions = new List<FixAction>
        {
            new("mark", done ? Lang.T("Снять отметку", "Unmark") : Lang.T("Я включил", "I've enabled it")),
        };
        if (ctx.Vendor is GpuVendor.Nvidia or GpuVendor.Amd)
            actions.Insert(0, new FixAction("open-panel", Lang.T("Открыть панель драйвера", "Open driver panel")));

        return new FixState
        {
            Status = done ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = done ? Lang.T("Отмечено вами", "Marked by you") : Lang.T("Нужно проверить вручную", "Check manually"),
            Reason = ctx.BestVrr == VrrSupport.Likely
                ? Lang.T("Явной метки VRR в EDID нет — если в панели драйвера нет этих настроек, пропустите пункт.",
                    "No explicit VRR flag in EDID — if the driver panel lacks these settings, skip this item.")
                : null,
            Steps = steps,
            Actions = actions,
        };
    }

    public override Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host)
    {
        if (actionId == "mark")
        {
            host.SetMark(Mark, !ctx.IsMarked(Mark));
            return Task.FromResult(ActionOutcome.Refreshed);
        }
        if (actionId == "open-panel")
        {
            if (!DriverPanel.TryOpen(ctx.Vendor))
                host.Notify(Title, Lang.T("Не удалось открыть панель. Откройте её вручную: правый клик по рабочему столу.",
                    "Could not open the panel. Open it manually: right-click the desktop."));
        }
        return Task.FromResult(ActionOutcome.None);
    }
}

internal static class DriverPanel
{
    public static bool TryOpen(GpuVendor vendor)
    {
        try
        {
            if (vendor == GpuVendor.Nvidia)
            {
                // DCH-драйверы ставят панель NVIDIA как приложение Store.
                Process.Start(new ProcessStartInfo("explorer.exe",
                    @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel") { UseShellExecute = true });
                return true;
            }
            if (vendor == GpuVendor.Amd)
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\CNext\CNext\RadeonSoftware.exe");
                if (!File.Exists(path)) return false;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}

// 4. Флаг DISABLEDXMAXIMIZEDWINDOWEDMODE
public sealed class FsoFlagItem : FixItem
{
    public override string Id => "fso-flag";
    public override int Number => 4;
    public override string Title => Lang.T("Галочка «Отключить оптимизацию во весь экран»", "“Disable fullscreen optimizations” checkbox");
    public override string Explanation => Lang.T(
        "Эта галочка в свойствах exe (вкладка «Совместимость») мешает Windows применять к игре современный вывод кадров. " +
        "В окне без рамки из-за неё растёт задержка и может не работать VRR. Флаг нужно убрать.",
        "This checkbox in the exe properties (Compatibility tab) stops Windows from using modern presentation for the game. " +
        "In borderless mode it adds latency and can break VRR. The flag should be removed.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.GameExe is null) return FixState.NeedGame();
        string? user = Reg.GetString(RegHive.CurrentUser, WgPaths.Layers, ctx.GameExe);
        string? machine = Reg.GetString(RegHive.LocalMachine, WgPaths.Layers, ctx.GameExe);
        bool inUser = CompatLayers.Has(user, CompatLayers.DisableFullscreenOptimizations);
        bool inMachine = CompatLayers.Has(machine, CompatLayers.DisableFullscreenOptimizations);
        string technical = $"HKCU\\{WgPaths.Layers} → {ctx.GameExe} = {user ?? Missing}\nHKLM\\{WgPaths.Layers} → {ctx.GameExe} = {machine ?? Missing}";
        if (!inUser && !inMachine)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T("Флага нет", "Not set"),
                Technical = technical,
            };

        string where = inUser && inMachine
            ? Lang.T("для текущего пользователя и для всех пользователей", "for the current user and for all users")
            : inUser ? Lang.T("для текущего пользователя", "for the current user") : Lang.T("для всех пользователей", "for all users");
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T("Флаг установлен", "Flag is set"),
            Reason = Lang.T($"Галочка стоит {where}.", $"The flag is set {where}."),
            CanFix = true,
            Recommended = true,
            NeedsAdmin = inMachine,
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        var list = new List<Change>();
        if (ctx.GameExe is null) return list;
        string why = Lang.T("Убираем только этот флаг; другие флаги совместимости остаются.",
            "Only this flag is removed; other compatibility flags stay.");
        foreach (var hive in new[] { RegHive.CurrentUser, RegHive.LocalMachine })
        {
            string? value = Reg.GetString(hive, WgPaths.Layers, ctx.GameExe);
            if (!CompatLayers.Has(value, CompatLayers.DisableFullscreenOptimizations)) continue;
            string? updated = CompatLayers.Remove(value, CompatLayers.DisableFullscreenOptimizations);
            list.Add(RegChange(hive, WgPaths.Layers, ctx.GameExe, "String", updated,
                value ?? "", updated ?? Lang.T("значение удалено (других флагов нет)", "value removed (no other flags)"), why));
        }
        return list;
    }
}

// 5. GpuPreference=2
public sealed class GpuPreferenceItem : FixItem
{
    private const string Sub = "GpuPreference";

    public override string Id => "gpu-preference";
    public override int Number => 5;
    public override string Title => Lang.T("Игра на производительной видеокарте", "Run the game on the high-performance GPU");
    public override string Explanation => Lang.T(
        "Указывает Windows запускать игру на мощной видеокарте (в «Настройках графики» — «Высокая производительность»). " +
        "Важно для ноутбуков и компьютеров со встроенной графикой в процессоре.",
        "Tells Windows to run the game on the powerful GPU (“High performance” in Graphics settings). " +
        "Matters for laptops and PCs with integrated graphics.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.GameExe is null) return FixState.NeedGame();
        if (ctx.Gpus.Count < 2)
            return FixState.Unavailable(Lang.T(
                $"В системе одна видеокарта ({ctx.GpuText}) — игра и так работает на ней, выбирать не из чего.",
                $"There is only one GPU ({ctx.GpuText}) — the game already runs on it."));

        string? v = Reg.GetSub(RegHive.CurrentUser, WgPaths.DirectX, ctx.GameExe, Sub);
        bool ok = v == "2";
        return new FixState
        {
            Status = ok ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = ok
                ? Lang.T("Высокая производительность", "High performance")
                : v == "1" ? Lang.T("Энергосбережение", "Power saving") : Lang.T("Решает Windows", "Let Windows decide"),
            Reason = ok ? null : Lang.T($"Видеокарты: {string.Join(", ", ctx.Gpus.Select(g => g.Name))}.",
                $"GPUs: {string.Join(", ", ctx.Gpus.Select(g => g.Name))}."),
            CanFix = !ok,
            Recommended = !ok,
            Technical = $@"HKCU\{WgPaths.DirectX} → {ctx.GameExe} → {Sub} = {v ?? Missing}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        if (ctx.GameExe is null) return new();
        string? v = Reg.GetSub(RegHive.CurrentUser, WgPaths.DirectX, ctx.GameExe, Sub);
        return new()
        {
            RegChange(RegHive.CurrentUser, WgPaths.DirectX, ctx.GameExe, "String", "2", v ?? Missing, "2",
                Lang.T("Игра будет запускаться на производительной видеокарте. Подействует после перезапуска игры.",
                    "The game will start on the high-performance GPU. Takes effect after the game restarts."),
                subKey: Sub),
        };
    }
}
