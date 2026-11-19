using WinPulse.Core;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.WindowedGames;

// 6. HAGS
public sealed class HagsItem : FixItem
{
    public override string Id => "hags";
    public override int Number => 6;
    public override string Title => Lang.T("Аппаратное планирование GPU (HAGS)", "Hardware-accelerated GPU scheduling (HAGS)");
    public override string Explanation => Lang.T(
        "Видеокарта сама распределяет очередь своих задач вместо Windows. Обязательна для DLSS Frame Generation; " +
        "на обычный FPS влияет слабо (0–2%), иногда немного снижает задержку. Нужны права администратора и перезагрузка.",
        "The GPU manages its own work queue instead of Windows. Required for DLSS Frame Generation; " +
        "barely affects regular FPS (0–2%), sometimes slightly lowers latency. Needs admin rights and a restart.");

    private static string Warn => Lang.T(
        "Не выключайте HAGS, если пользуетесь DLSS Frame Generation (генерация кадров NVIDIA) — без него она не работает.",
        "Don't turn HAGS off if you use DLSS Frame Generation — it doesn't work without it.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.Os.Build < 19041)
            return FixState.Unavailable(Lang.T(
                $"Нужна Windows 10 2004 (сборка 19041) или новее. У вас сборка {ctx.Os.Build}.",
                $"Requires Windows 10 2004 (build 19041) or newer. You have build {ctx.Os.Build}."));
        if (ctx.HagsInfo is { Supported: false })
            return FixState.Unavailable(Lang.T(
                $"Видеокарта или драйвер не поддерживают HAGS ({ctx.GpuText}). Обновите драйвер: нужна NVIDIA GTX 10xx и новее, AMD RX 5000 и новее или Intel Arc.",
                $"The GPU or driver doesn't support HAGS ({ctx.GpuText}). Update the driver: NVIDIA GTX 10xx+, AMD RX 5000+ or Intel Arc is required."));

        int? reg = Reg.GetDword(RegHive.LocalMachine, WgPaths.GraphicsDrivers, "HwSchMode");
        bool? running = ctx.HagsInfo?.Enabled;
        bool wanted = reg == 2 || (reg is null && (running ?? false));
        bool pendingRestart = reg is not null && running is not null && (reg == 2) != running.Value;

        string status = wanted ? Lang.T("Включено", "On") : Lang.T("Выключено", "Off");
        if (pendingRestart) status += Lang.T(" — после перезагрузки", " — after restart");
        string technical = $@"HKLM\{WgPaths.GraphicsDrivers} → HwSchMode = {reg?.ToString() ?? Missing}" +
                           (running is null ? "" : Lang.T($"\nСейчас работает: {(running.Value ? "да" : "нет")}", $"\nActive now: {(running.Value ? "yes" : "no")}"));

        var actions = new List<FixAction>();
        if (wanted) actions.Add(new FixAction("off", Lang.T("Выключить HAGS", "Turn HAGS off"), Danger: true));

        return new FixState
        {
            Status = wanted ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = status,
            Reason = pendingRestart ? Lang.T("Изменение вступит в силу после перезагрузки.", "The change takes effect after a restart.") : null,
            Warning = Warn,
            CanFix = !wanted,
            FixLabel = Lang.T("Включить HAGS", "Turn HAGS on"),
            Recommended = !wanted,
            NeedsAdmin = true,
            NeedsRestart = true,
            Technical = technical,
            Actions = actions,
        };
    }

    private Change Plan(int value) => RegChange(RegHive.LocalMachine, WgPaths.GraphicsDrivers, "HwSchMode", "DWord", value.ToString(),
        Reg.GetDword(RegHive.LocalMachine, WgPaths.GraphicsDrivers, "HwSchMode")?.ToString() ?? Missing,
        value.ToString() + (value == 2 ? Lang.T(" (включено)", " (on)") : Lang.T(" (выключено)", " (off)")),
        value == 2
            ? Lang.T("Включает аппаратное планирование GPU. Подействует после перезагрузки.", "Turns on hardware-accelerated GPU scheduling. Takes effect after a restart.")
            : Lang.T("Выключает аппаратное планирование GPU. DLSS Frame Generation перестанет работать. Подействует после перезагрузки.",
                "Turns off hardware-accelerated GPU scheduling. DLSS Frame Generation will stop working. Takes effect after a restart."),
        restart: true);

    public override List<Change> PlanFix(FixContext ctx) => new() { Plan(2) };

    public override async Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host)
    {
        if (actionId != "off") return ActionOutcome.None;
        bool ok = await host.ConfirmAsync(Lang.T("Выключить HAGS?", "Turn HAGS off?"), Warn, Lang.T("Продолжить", "Continue"), danger: true);
        return ok ? new ActionOutcome { Plan = new List<Change> { Plan(1) } } : ActionOutcome.None;
    }
}

// 7. Игровой режим
public sealed class GameModeItem : FixItem
{
    public override string Id => "game-mode";
    public override int Number => 7;
    public override string Title => Lang.T("Игровой режим Windows", "Windows Game Mode");
    public override string Explanation => Lang.T(
        "Во время игры Windows не ставит обновления драйверов, не показывает уведомления о перезагрузке и отдаёт игре приоритет. По умолчанию включён.",
        "While you play, Windows holds off driver updates and restart notifications and prioritizes the game. On by default.");

    public override FixState Evaluate(FixContext ctx)
    {
        int? v = Reg.GetDword(RegHive.CurrentUser, WgPaths.GameBar, "AutoGameModeEnabled");
        bool on = v != 0;
        return new FixState
        {
            Status = on ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = on
                ? (v is null ? Lang.T("Включён (по умолчанию)", "On (default)") : Lang.T("Включён", "On"))
                : Lang.T("Выключен", "Off"),
            CanFix = !on,
            Recommended = !on,
            Technical = $@"HKCU\{WgPaths.GameBar} → AutoGameModeEnabled = {v?.ToString() ?? Missing}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx) => new()
    {
        RegChange(RegHive.CurrentUser, WgPaths.GameBar, "AutoGameModeEnabled", "DWord", "1",
            Reg.GetDword(RegHive.CurrentUser, WgPaths.GameBar, "AutoGameModeEnabled")?.ToString() ?? Missing, "1",
            Lang.T("Включает игровой режим (Параметры → Игры → Игровой режим).", "Turns on Game Mode (Settings → Gaming → Game Mode).")),
    };
}

// 8. Фоновая запись (Game DVR)
public sealed class GameDvrItem : FixItem
{
    public override string Id => "game-dvr";
    public override int Number => 8;
    public override string Title => Lang.T("Фоновая запись игр (Xbox Game Bar)", "Background game recording (Game DVR)");
    public override string Explanation => Lang.T(
        "Функция «Записать последние минуты» постоянно кодирует видео игры в фоне и отнимает ресурсы видеокарты. " +
        "Если вы ей не пользуетесь — её лучше выключить. Скриншоты и ручная запись через Win+Alt+R продолжат работать.",
        "“Record what happened” constantly encodes gameplay video in the background and takes GPU resources. " +
        "If you don't use it, turn it off. Screenshots and manual recording (Win+Alt+R) keep working.");

    public override FixState Evaluate(FixContext ctx)
    {
        int? dvr = Reg.GetDword(RegHive.CurrentUser, WgPaths.GameConfigStore, "GameDVR_Enabled");
        int? capture = Reg.GetDword(RegHive.CurrentUser, WgPaths.GameDvr, "AppCaptureEnabled");
        bool off = dvr == 0 && capture == 0;
        return new FixState
        {
            Status = off ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = off ? Lang.T("Выключена", "Off") : Lang.T("Включена", "On"),
            CanFix = !off,
            Recommended = !off,
            Technical = $@"HKCU\{WgPaths.GameConfigStore} → GameDVR_Enabled = {dvr?.ToString() ?? Missing}" + "\n" +
                        $@"HKCU\{WgPaths.GameDvr} → AppCaptureEnabled = {capture?.ToString() ?? Missing}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        var list = new List<Change>();
        int? dvr = Reg.GetDword(RegHive.CurrentUser, WgPaths.GameConfigStore, "GameDVR_Enabled");
        int? capture = Reg.GetDword(RegHive.CurrentUser, WgPaths.GameDvr, "AppCaptureEnabled");
        if (dvr != 0)
            list.Add(RegChange(RegHive.CurrentUser, WgPaths.GameConfigStore, "GameDVR_Enabled", "DWord", "0",
                dvr?.ToString() ?? Missing, "0",
                Lang.T("Выключает фоновую запись игр.", "Turns off background game recording.")));
        if (capture != 0)
            list.Add(RegChange(RegHive.CurrentUser, WgPaths.GameDvr, "AppCaptureEnabled", "DWord", "0",
                capture?.ToString() ?? Missing, "0",
                Lang.T("Выключает захват игр в фоне («Записать последние минуты»).", "Turns off background capture (“Record what happened”).")));
        return list;
    }
}

// 12. Схема электропитания
public sealed class PowerPlanItem : FixItem
{
    public override string Id => "power";
    public override int Number => 12;
    public override string Title => Lang.T("Схема электропитания", "Power plan");
    public override string Explanation => Lang.T(
        "В схеме «Экономия энергии» процессор и видеокарта занижают частоты — игра теряет кадры. " +
        "Для игр лучше «Высокая производительность». Прежняя схема сохраняется и возвращается одной кнопкой.",
        "The Power saver plan lowers CPU and GPU clocks, so games lose frames. " +
        "High performance is better for gaming. The previous plan is saved and restored with one click.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.ActiveScheme is null)
            return FixState.Unavailable(Lang.T("Не удалось узнать текущую схему (powercfg не ответил).",
                "Could not read the current plan (powercfg did not respond)."));

        string active = ctx.ActiveScheme;
        string name = PowerSchemes.FriendlyName(active, ctx.Schemes.FirstOrDefault(s => s.Guid == active)?.Name);
        bool saver = active.Equals(PowerSchemes.PowerSaver, StringComparison.OrdinalIgnoreCase);
        bool fast = active.Equals(PowerSchemes.HighPerformance, StringComparison.OrdinalIgnoreCase) ||
                    active.Equals(PowerSchemes.Ultimate, StringComparison.OrdinalIgnoreCase) ||
                    (ctx.Backup.Get("power:active")?.CreatedSchemeGuid?.Equals(active, StringComparison.OrdinalIgnoreCase) ?? false);
        bool balanced = active.Equals(PowerSchemes.Balanced, StringComparison.OrdinalIgnoreCase);
        bool highVisible = ctx.Schemes.Any(s => s.Guid == PowerSchemes.HighPerformance);

        string? reason = saver
            ? Lang.T("«Экономия энергии» заметно снижает FPS.", "Power saver noticeably lowers FPS.")
            : balanced
                ? (ctx.HasBattery
                    ? Lang.T("Для ноутбука «Сбалансированная» — нормальный выбор. «Высокая производительность» даст немного больше кадров, но быстрее сажает батарею.",
                        "Balanced is fine for a laptop. High performance gives a few more frames but drains the battery faster.")
                    : Lang.T("«Сбалансированная» — нормальный выбор. «Высокая производительность» может дать немного больше стабильности кадров.",
                        "Balanced is fine. High performance may give slightly steadier frame times."))
                : null;
        if (!highVisible && !fast)
            reason = (reason is null ? "" : reason + " ") + Lang.T(
                "Схема «Высокая производительность» скрыта — WinPulse создаст её копию.",
                "The High performance plan is hidden — WinPulse will create a copy of it.");

        return new FixState
        {
            Status = saver ? FixStatus.Improvable : FixStatus.Ok,
            StatusText = name,
            Reason = reason,
            CanFix = !fast,
            FixLabel = Lang.T("Включить «Высокую производительность»", "Switch to High performance"),
            Recommended = saver,
            NeedsAdmin = true,
            Technical = $"powercfg /getactivescheme → {active}",
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        string before = ctx.ActiveScheme is null ? "?" : PowerSchemes.FriendlyName(ctx.ActiveScheme,
            ctx.Schemes.FirstOrDefault(s => s.Guid == ctx.ActiveScheme)?.Name);
        return new()
        {
            new Change
            {
                ItemId = Id,
                Kind = ChangeKind.PowerActivate,
                SchemeGuid = PowerSchemes.HighPerformance,
                CreateIfMissing = true,
                Title = Title,
                Before = before,
                After = PowerSchemes.FriendlyName(PowerSchemes.HighPerformance),
                Why = Lang.T("Процессор и видеокарта не будут занижать частоты. Прежняя схема сохраняется для возврата.",
                    "CPU and GPU won't drop their clocks. The previous plan is saved for restoring."),
            },
        };
    }
}

// 13. MPO
public sealed class MpoItem : FixItem
{
    public override string Id => "mpo";
    public override int Number => 13;
    public override string Title => Lang.T("Отключение MPO (мерцание и чёрные вспышки)", "Disable MPO (flicker and black flashes)");
    public override string Explanation => Lang.T(
        "Multi-Plane Overlay помогает выводить кадры с меньшей нагрузкой, но на части систем (чаще с NVIDIA и несколькими мониторами) " +
        "вызывает мерцание, чёрные вспышки и подтормаживания в окне. Отключайте, только если видите эти проблемы. " +
        "Нужна перезагрузка. На новых сборках Windows 11 параметр может не действовать.",
        "Multi-Plane Overlay makes presenting frames cheaper, but on some systems (often NVIDIA with several monitors) " +
        "it causes flicker, black flashes and stutter in windowed mode. Only disable it if you see these problems. " +
        "Requires a restart. On recent Windows 11 builds the setting may have no effect.");

    public override FixState Evaluate(FixContext ctx)
    {
        int? v = Reg.GetDword(RegHive.LocalMachine, WgPaths.Dwm, "OverlayTestMode");
        bool disabled = v == 5;
        bool ours = ctx.Backup.ForItem(Id).Count > 0;
        var actions = new List<FixAction>();
        if (disabled && !ours) actions.Add(new FixAction("enable", Lang.T("Включить MPO обратно", "Turn MPO back on")));

        return new FixState
        {
            Status = FixStatus.Ok,
            StatusText = disabled ? Lang.T("MPO отключено", "MPO disabled") : Lang.T("Включено (по умолчанию)", "On (default)"),
            Reason = disabled ? null : Lang.T("Трогать нужно только при мерцании или чёрных вспышках.", "Only change this if you see flicker or black flashes."),
            CanFix = !disabled,
            FixLabel = Lang.T("Отключить MPO", "Disable MPO"),
            Recommended = false,
            NeedsAdmin = true,
            NeedsRestart = true,
            Technical = $@"HKLM\{WgPaths.Dwm} → OverlayTestMode = {v?.ToString() ?? Missing}",
            Actions = actions,
        };
    }

    public override List<Change> PlanFix(FixContext ctx) => new()
    {
        RegChange(RegHive.LocalMachine, WgPaths.Dwm, "OverlayTestMode", "DWord", "5",
            Reg.GetDword(RegHive.LocalMachine, WgPaths.Dwm, "OverlayTestMode")?.ToString() ?? Missing, "5",
            Lang.T("Отключает MPO. Подействует после перезагрузки. «Вернуть как было» удалит параметр.",
                "Disables MPO. Takes effect after a restart. “Restore” removes the value."),
            restart: true),
    };

    public override Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host)
    {
        if (actionId != "enable") return Task.FromResult(ActionOutcome.None);
        var plan = new List<Change>
        {
            RegChange(RegHive.LocalMachine, WgPaths.Dwm, "OverlayTestMode", "DWord", null, "5",
                Lang.T("параметр удалён", "value removed"),
                Lang.T("Возвращает MPO (поведение Windows по умолчанию). Подействует после перезагрузки.",
                    "Restores MPO (Windows default). Takes effect after a restart."),
                restart: true),
        };
        return Task.FromResult(new ActionOutcome { Plan = plan });
    }
}
