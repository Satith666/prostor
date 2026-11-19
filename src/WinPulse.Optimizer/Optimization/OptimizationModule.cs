using WinPulse.Collectors.Gpu;
using WinPulse.Core;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.Optimizer.Optimization;

/// <summary>
/// «Оптимизация Windows»: только настройки с понятным и проверяемым эффектом. Каждая — с ценой, резервной копией и откатом.
/// Схема питания, игровой режим, HAGS и фоновая запись — в модуле «Игры в окне», здесь не дублируются.
/// </summary>
public sealed class OptimizationModule : FixModule
{
    public OptimizationModule(string? backupPath = null)
        : base("optimization", new FixItem[]
        {
            new MemoryIntegrityItem(),
            new MouseAccelerationItem(),
            new PageFileItem(),
            new FastStartupItem(),
            new TransparencyItem(),
            new SuggestedAppsItem(),
            new TipsItem(),
            new WebSearchItem(),
        }, backupPath)
    {
    }
}

internal static class OptPaths
{
    public const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    public const string HvciPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
    public const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public const string SessionPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    public const string Power = @"SYSTEM\CurrentControlSet\Control\Power";
    public const string Mouse = @"Control Panel\Mouse";
    public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string Engagement = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement";
    public const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";
}

/// <summary>Базовый класс для пунктов-переключателей «включено/выключено», где оба состояния допустимы.</summary>
public abstract class ToggleItem : FixItem
{
    protected static FixState Toggle(bool on, string onText, string offText, string turnOff, string turnOn, string? reason,
        string? warning, bool admin, bool restart, string technical, bool improvable = false, bool recommended = false) => new()
    {
        Status = improvable ? FixStatus.Improvable : FixStatus.Ok,
        StatusText = on ? onText : offText,
        Reason = reason,
        Warning = warning,
        CanFix = true,
        FixLabel = on ? turnOff : turnOn,
        Recommended = recommended,
        NeedsAdmin = admin,
        NeedsRestart = restart,
        Technical = technical,
    };
}

// 1. Целостность памяти (HVCI)
public sealed class MemoryIntegrityItem : ToggleItem
{
    public override string Id => "hvci";
    public override int Number => 1;
    public override string Title => Lang.T("Изоляция ядра: целостность памяти (HVCI)", "Core isolation: Memory integrity (HVCI)");
    public override string Explanation => Lang.T(
        "Защита, которая не даёт вредоносному коду попасть в ядро Windows. Цена — до 5–10% FPS в части игр, особенно на процессорах старше 2018 года. " +
        "Microsoft сама называет её отключение одним из способов поднять производительность в играх. Это выбор между скоростью и защитой — решать вам.",
        "Protection that keeps malicious code out of the Windows kernel. The cost is up to 5–10% FPS in some games, especially on CPUs older than 2018. " +
        "Microsoft itself lists turning it off as a way to improve gaming performance. It's a trade-off between speed and security.");

    private static string Warn => Lang.T(
        "Без этой защиты компьютер уязвимее к вредоносным драйверам. Некоторые античиты (например, в Valorant и FACEIT на Windows 11) могут требовать её — если игра перестанет запускаться, верните как было.",
        "Without it the PC is more exposed to malicious drivers. Some anti-cheats (e.g. Valorant, FACEIT on Windows 11) may require it — if a game stops launching, restore it.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (Reg.Read(RegHive.LocalMachine, OptPaths.HvciPolicy, "HypervisorEnforcedCodeIntegrity").Exists)
            return FixState.Unavailable(Lang.T("Настройка задана политикой организации — изменить её может только администратор домена.",
                "The setting is enforced by policy — only your domain administrator can change it."));
        if (Reg.GetDword(RegHive.LocalMachine, OptPaths.Hvci, "Locked") == 1)
            return FixState.Unavailable(Lang.T("Защита заблокирована в UEFI (Locked = 1) — из Windows её не выключить.",
                "The protection is locked in UEFI (Locked = 1) — it can't be turned off from Windows."));

        int? reg = Reg.GetDword(RegHive.LocalMachine, OptPaths.Hvci, "Enabled");
        bool running = ctx.HvciRunning ?? reg == 1;
        bool wanted = reg is null ? running : reg == 1;
        string? reason = wanted != running
            ? Lang.T("Изменение вступит в силу после перезагрузки.", "The change takes effect after a restart.")
            : null;
        return Toggle(wanted,
            Lang.T("Включена", "On") + (running != wanted ? Lang.T(" (сейчас выключена)", " (currently off)") : ""),
            Lang.T("Выключена", "Off") + (running != wanted ? Lang.T(" (сейчас включена)", " (currently on)") : ""),
            Lang.T("Выключить для игр", "Turn off for gaming"),
            Lang.T("Включить защиту", "Turn protection on"),
            reason, Warn, admin: true, restart: true,
            technical: $@"HKLM\{OptPaths.Hvci} → Enabled = {reg?.ToString() ?? Missing}" + "\n" +
                       Lang.T("Работает сейчас: ", "Running now: ") + (ctx.HvciRunning is null ? "?" : ctx.HvciRunning.Value ? "1" : "0"));
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        int? reg = Reg.GetDword(RegHive.LocalMachine, OptPaths.Hvci, "Enabled");
        bool running = ctx.HvciRunning ?? reg == 1;
        bool wanted = reg is null ? running : reg == 1;
        string target = wanted ? "0" : "1";
        return new()
        {
            RegChange(RegHive.LocalMachine, OptPaths.Hvci, "Enabled", "DWord", target, reg?.ToString() ?? Missing,
                target == "0" ? Lang.T("0 (выключена)", "0 (off)") : Lang.T("1 (включена)", "1 (on)"),
                target == "0"
                    ? Lang.T("Выключает целостность памяти: меньше накладных расходов на виртуализацию в играх. Подействует после перезагрузки.",
                        "Turns memory integrity off: less virtualization overhead in games. Takes effect after a restart.")
                    : Lang.T("Включает целостность памяти обратно. Подействует после перезагрузки.",
                        "Turns memory integrity back on. Takes effect after a restart."),
                restart: true),
        };
    }
}

// 2. Ускорение мыши
public sealed class MouseAccelerationItem : ToggleItem
{
    public override string Id => "mouse-accel";
    public override int Number => 2;
    public override string Title => Lang.T("Ускорение указателя мыши", "Mouse pointer acceleration");
    public override string Explanation => Lang.T(
        "«Повышенная точность установки указателя» меняет расстояние, на которое двигается курсор, в зависимости от скорости движения мыши. " +
        "Для прицеливания удобнее, когда одно и то же движение руки всегда даёт одинаковый сдвиг. Большинство современных игр ускорение и так не используют.",
        "“Enhance pointer precision” changes how far the cursor moves depending on how fast you move the mouse. " +
        "For aiming it's better when the same hand motion always gives the same movement. Most modern games ignore acceleration anyway.");

    public override FixState Evaluate(FixContext ctx)
    {
        string? speed = Reg.GetString(RegHive.CurrentUser, OptPaths.Mouse, "MouseSpeed");
        bool on = speed != "0";
        return Toggle(on,
            Lang.T("Включено", "On"), Lang.T("Выключено", "Off"),
            Lang.T("Выключить ускорение", "Turn acceleration off"), Lang.T("Включить ускорение", "Turn acceleration on"),
            Lang.T("Подействует после выхода из Windows и повторного входа.", "Takes effect after you sign out and back in."),
            null, admin: false, restart: false,
            technical: $@"HKCU\{OptPaths.Mouse} → MouseSpeed = {speed ?? Missing}, MouseThreshold1/2");
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        bool on = Reg.GetString(RegHive.CurrentUser, OptPaths.Mouse, "MouseSpeed") != "0";
        var values = on ? new[] { "0", "0", "0" } : new[] { "1", "6", "10" };
        string why = on
            ? Lang.T("Курсор будет двигаться строго пропорционально движению мыши.", "The cursor will move strictly in proportion to the mouse.")
            : Lang.T("Возвращает стандартное ускорение Windows.", "Restores the default Windows acceleration.");
        var names = new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" };
        var list = new List<Change>();
        for (int i = 0; i < names.Length; i++)
        {
            string? current = Reg.GetString(RegHive.CurrentUser, OptPaths.Mouse, names[i]);
            list.Add(RegChange(RegHive.CurrentUser, OptPaths.Mouse, names[i], "String", values[i], current ?? Missing, values[i], why));
        }
        return list;
    }
}

// 3. Файл подкачки
public sealed class PageFileItem : FixItem
{
    private const string Auto = @"?:\pagefile.sys";

    public override string Id => "pagefile";
    public override int Number => 3;
    public override string Title => Lang.T("Файл подкачки", "Page file");
    public override string Explanation => Lang.T(
        "Когда оперативной памяти не хватает, Windows временно переносит редко используемые данные на диск. " +
        "Без файла подкачки программы и игры в такой момент закрываются с ошибкой. Отключение скорости не добавляет — это миф. " +
        "Лучший вариант для всех — «Автоматически выбирать объём».",
        "When RAM runs out, Windows temporarily moves rarely used data to disk. " +
        "Without a page file, apps and games crash at that moment. Disabling it doesn't make anything faster — that's a myth. " +
        "The best option for everyone is “Automatically manage size”.");

    public override FixState Evaluate(FixContext ctx)
    {
        var state = Reg.Read(RegHive.LocalMachine, OptPaths.MemoryManagement, "PagingFiles");
        var lines = state.Exists && state.Data is not null
            ? state.Data.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : new List<string>();
        string technical = $@"HKLM\{OptPaths.MemoryManagement} → PagingFiles = {(lines.Count == 0 ? Missing : string.Join("; ", lines))}";

        if (lines.Count == 0)
            return new FixState
            {
                Status = FixStatus.Improvable,
                StatusText = Lang.T("Отключён", "Disabled"),
                Reason = Lang.T("При нехватке памяти программы будут закрываться с ошибкой.", "Apps will crash when memory runs out."),
                CanFix = true,
                FixLabel = Lang.T("Включить (автоматически)", "Enable (automatic)"),
                Recommended = true,
                NeedsAdmin = true,
                NeedsRestart = true,
                Technical = technical,
            };

        bool auto = lines.Any(l => l.StartsWith(Auto, StringComparison.OrdinalIgnoreCase));
        return new FixState
        {
            Status = FixStatus.Ok,
            StatusText = auto ? Lang.T("Автоматически", "Automatic") : Lang.T("Задан вручную", "Set manually"),
            Reason = auto ? null : Lang.T("Если бывают ошибки нехватки памяти — переключите на автоматический размер.",
                "If you get out-of-memory errors, switch to automatic size."),
            CanFix = !auto,
            FixLabel = Lang.T("Сделать автоматическим", "Make automatic"),
            NeedsAdmin = true,
            NeedsRestart = true,
            Technical = technical,
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        var state = Reg.Read(RegHive.LocalMachine, OptPaths.MemoryManagement, "PagingFiles");
        string before = state.Exists && !string.IsNullOrWhiteSpace(state.Data) ? state.Data.Replace("\n", "; ") : Lang.T("отключён", "disabled");
        return new()
        {
            RegChange(RegHive.LocalMachine, OptPaths.MemoryManagement, "PagingFiles", "MultiString", Auto, before,
                Lang.T("автоматически (" + Auto + ")", "automatic (" + Auto + ")"),
                Lang.T("Windows сама выберет размер файла подкачки на системном диске. Подействует после перезагрузки.",
                    "Windows will size the page file on the system drive itself. Takes effect after a restart."),
                restart: true),
        };
    }
}

// 4. Быстрый запуск
public sealed class FastStartupItem : ToggleItem
{
    public override string Id => "fast-startup";
    public override int Number => 4;
    public override string Title => Lang.T("Быстрый запуск", "Fast startup");
    public override string Explanation => Lang.T(
        "При «Завершении работы» Windows сохраняет ядро и драйверы на диск, чтобы следующее включение было быстрее. " +
        "Цена: драйверы не перезапускаются, обновления иногда не доустанавливаются, а при двух системах на компьютере диски могут повредиться. " +
        "Если после включения бывают странные сбои — выключите. «Перезагрузка» в любом случае перезапускает всё полностью.",
        "On “Shut down”, Windows saves the kernel and drivers to disk to boot faster next time. " +
        "The cost: drivers aren't restarted, updates sometimes don't finish, and dual-boot setups risk disk corruption. " +
        "If you see odd glitches after power-on, turn it off. “Restart” always does a full restart either way.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (Reg.GetDword(RegHive.LocalMachine, OptPaths.Power, "HibernateEnabled") == 0)
            return FixState.Unavailable(Lang.T("Гибернация выключена, поэтому быстрый запуск не работает.", "Hibernation is off, so fast startup doesn't work."));
        int? v = Reg.GetDword(RegHive.LocalMachine, OptPaths.SessionPower, "HiberbootEnabled");
        bool on = v != 0;
        return Toggle(on, Lang.T("Включён", "On"), Lang.T("Выключен", "Off"),
            Lang.T("Выключить быстрый запуск", "Turn fast startup off"), Lang.T("Включить быстрый запуск", "Turn fast startup on"),
            null, null, admin: true, restart: false,
            technical: $@"HKLM\{OptPaths.SessionPower} → HiberbootEnabled = {v?.ToString() ?? Missing}");
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        int? v = Reg.GetDword(RegHive.LocalMachine, OptPaths.SessionPower, "HiberbootEnabled");
        bool on = v != 0;
        return new()
        {
            RegChange(RegHive.LocalMachine, OptPaths.SessionPower, "HiberbootEnabled", "DWord", on ? "0" : "1", v?.ToString() ?? Missing,
                on ? Lang.T("0 (выключен)", "0 (off)") : Lang.T("1 (включён)", "1 (on)"),
                on
                    ? Lang.T("«Завершение работы» будет полностью выключать Windows. Включение станет на несколько секунд дольше.",
                        "“Shut down” will fully power off Windows. Boot becomes a few seconds slower.")
                    : Lang.T("Включение снова станет быстрее за счёт сохранения ядра.", "Boot gets faster again by saving the kernel.")),
        };
    }
}

// 5. Прозрачность
public sealed class TransparencyItem : ToggleItem
{
    public override string Id => "transparency";
    public override int Number => 5;
    public override string Title => Lang.T("Эффекты прозрачности", "Transparency effects");
    public override string Explanation => Lang.T(
        "Полупрозрачные «Пуск», панель задач и окна. На встроенной графике немного нагружают видеоядро и батарею; на отдельной видеокарте разницы нет.",
        "Translucent Start, taskbar and windows. On integrated graphics they slightly load the GPU and battery; on a discrete GPU there's no difference.");

    public override FixState Evaluate(FixContext ctx)
    {
        int? v = Reg.GetDword(RegHive.CurrentUser, OptPaths.Personalize, "EnableTransparency");
        bool on = v != 0;
        bool weakGpu = ctx.Gpus.Count > 0 && ctx.Gpus.All(g => g.Vendor == GpuVendor.Intel || g.DedicatedMemory < 2L * 1024 * 1024 * 1024);
        return Toggle(on, Lang.T("Включены", "On"), Lang.T("Выключены", "Off"),
            Lang.T("Выключить прозрачность", "Turn transparency off"), Lang.T("Включить прозрачность", "Turn transparency on"),
            weakGpu && on
                ? Lang.T("У вас встроенная или слабая графика — выключение немного разгрузит её.", "You have integrated or weak graphics — turning it off frees a little GPU time.")
                : Lang.T("Изменение видно сразу или после повторного входа.", "Visible immediately or after signing in again."),
            null, admin: false, restart: false,
            technical: $@"HKCU\{OptPaths.Personalize} → EnableTransparency = {v?.ToString() ?? Missing}",
            improvable: weakGpu && on);
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        int? v = Reg.GetDword(RegHive.CurrentUser, OptPaths.Personalize, "EnableTransparency");
        bool on = v != 0;
        return new()
        {
            RegChange(RegHive.CurrentUser, OptPaths.Personalize, "EnableTransparency", "DWord", on ? "0" : "1", v?.ToString() ?? Missing,
                on ? "0" : "1",
                on ? Lang.T("Отключает полупрозрачность интерфейса.", "Turns interface translucency off.")
                   : Lang.T("Возвращает полупрозрачность интерфейса.", "Turns interface translucency back on.")),
        };
    }
}

/// <summary>Пункт из нескольких DWORD-значений HKCU, которые все должны быть равны «хорошему» значению.</summary>
public abstract class DwordSetItem : FixItem
{
    protected abstract IReadOnlyList<(string Key, string Name, int Good)> Values(FixContext ctx);
    protected abstract string WhyText { get; }

    public override FixState Evaluate(FixContext ctx)
    {
        var values = Values(ctx);
        var current = values.Select(v => (v.Key, v.Name, v.Good, Value: Reg.GetDword(RegHive.CurrentUser, v.Key, v.Name))).ToList();
        bool ok = current.All(c => c.Value == c.Good);
        return new FixState
        {
            Status = ok ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = ok ? Lang.T("Отключено", "Off") : Lang.T("Включено", "On"),
            CanFix = !ok,
            FixLabel = Lang.T("Отключить", "Turn off"),
            Recommended = !ok,
            Technical = string.Join("\n", current.Select(c => $@"HKCU\{c.Key} → {c.Name} = {c.Value?.ToString() ?? Missing}")),
        };
    }

    public override List<Change> PlanFix(FixContext ctx) => Values(ctx)
        .Select(v => (v.Key, v.Name, v.Good, Value: Reg.GetDword(RegHive.CurrentUser, v.Key, v.Name)))
        .Where(c => c.Value != c.Good)
        .Select(c => RegChange(RegHive.CurrentUser, c.Key, c.Name, "DWord", c.Good.ToString(), c.Value?.ToString() ?? Missing, c.Good.ToString(), WhyText))
        .ToList();
}

// 6. Автоустановка «рекомендуемых» приложений
public sealed class SuggestedAppsItem : DwordSetItem
{
    public override string Id => "auto-apps";
    public override int Number => 6;
    public override string Title => Lang.T("Автоустановка «рекомендуемых» приложений", "Automatic install of “suggested” apps");
    public override string Explanation => Lang.T(
        "Windows сама скачивает в фоне игры и приложения-«рекомендации» и добавляет их в «Пуск». Отключение останавливает это; уже установленное остаётся — его можно удалить как обычно.",
        "Windows downloads “suggested” games and apps in the background and pins them to Start. This stops it; what's installed stays and can be uninstalled as usual.");
    protected override string WhyText => Lang.T("Windows перестанет сама ставить рекламные приложения.", "Windows will stop installing promoted apps on its own.");

    protected override IReadOnlyList<(string Key, string Name, int Good)> Values(FixContext ctx) => new[]
    {
        (OptPaths.ContentDelivery, "SilentInstalledAppsEnabled", 0),
        (OptPaths.ContentDelivery, "PreInstalledAppsEnabled", 0),
        (OptPaths.ContentDelivery, "OemPreInstalledAppsEnabled", 0),
    };
}

// 7. Советы и реклама
public sealed class TipsItem : DwordSetItem
{
    public override string Id => "tips";
    public override int Number => 7;
    public override string Title => Lang.T("Советы, предложения и напоминания Windows", "Windows tips, suggestions and nags");
    public override string Explanation => Lang.T(
        "Всплывающие «советы», предложения в «Пуске» и «Параметрах», напоминания «Завершите настройку устройства». Пользы мало, отвлекают.",
        "Pop-up “tips”, suggestions in Start and Settings, and “Let's finish setting up your device” reminders. Little value, distracting.");
    protected override string WhyText => Lang.T("Windows перестанет показывать рекламные советы и предложения.", "Windows will stop showing promotional tips and suggestions.");

    protected override IReadOnlyList<(string Key, string Name, int Good)> Values(FixContext ctx) => new[]
    {
        (OptPaths.ContentDelivery, "SubscribedContent-338388Enabled", 0),
        (OptPaths.ContentDelivery, "SubscribedContent-338389Enabled", 0),
        (OptPaths.ContentDelivery, "SubscribedContent-353694Enabled", 0),
        (OptPaths.ContentDelivery, "SubscribedContent-353696Enabled", 0),
        (OptPaths.ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
        (OptPaths.Engagement, "ScoobeSystemSettingEnabled", 0),
    };
}

// 8. Интернет-поиск в «Пуске»
public sealed class WebSearchItem : DwordSetItem
{
    public override string Id => "web-search";
    public override int Number => 8;
    public override string Title => Lang.T("Результаты из интернета в поиске «Пуска»", "Web results in Start search");
    public override string Explanation => Lang.T(
        "Поиск в «Пуске» отправляет каждый запрос в Bing и показывает веб-результаты. Без этого поиск программ и файлов быстрее и не уходит в интернет. " +
        "Подействует после перезапуска Проводника или повторного входа.",
        "Start search sends every query to Bing and shows web results. Without it, searching apps and files is faster and stays offline. " +
        "Takes effect after restarting Explorer or signing in again.");
    protected override string WhyText => Lang.T("Поиск в «Пуске» будет искать только на компьютере.", "Start search will only search this PC.");

    protected override IReadOnlyList<(string Key, string Name, int Good)> Values(FixContext ctx) => ctx.Os.IsWindows11
        ? new[] { (OptPaths.ExplorerPolicy, "DisableSearchBoxSuggestions", 1) }
        : new[] { (OptPaths.Search, "BingSearchEnabled", 0), (OptPaths.Search, "CortanaConsent", 0) };
}
