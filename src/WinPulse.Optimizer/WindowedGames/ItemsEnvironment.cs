using System.Diagnostics;
using WinPulse.Collectors.Display;
using WinPulse.Collectors.Gpu;
using WinPulse.Core;
using WinPulse.Core.Display;
using WinPulse.Optimizer.Changes;

namespace WinPulse.Optimizer.WindowedGames;

// 9. Программы, которые грузят видеокарту
public sealed class GpuAppsItem : FixItem
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "browser.exe", "vivaldi.exe",
    };

    /// <summary>Что закрыл WinPulse в этом сеансе: путь → имя. Нужен для «Запустить снова».</summary>
    private readonly Dictionary<string, string> _closed = new(StringComparer.OrdinalIgnoreCase);

    public override string Id => "gpu-apps";
    public override int Number => 9;
    public override string Title => Lang.T("Программы, которые грузят видеокарту", "Programs using the GPU in the background");
    public override string Explanation => Lang.T(
        "Живые обои, видео в браузере, оверлеи Discord, NVIDIA и Xbox, RivaTuner рисуют поверх игры или рядом с ней и отнимают кадры. " +
        "Ниже — что запущено сейчас и как это настроить. Ничего не закрываем без вашего согласия.",
        "Live wallpapers, browser video, Discord/NVIDIA/Xbox overlays and RivaTuner draw over or next to the game and cost frames. " +
        "Below is what is running now and how to tune it. Nothing is closed without your consent.");

    private static string DisplayName(string exe) => exe.ToLowerInvariant() switch
    {
        "wallpaper32.exe" or "wallpaper64.exe" => "Wallpaper Engine",
        "lively.exe" or "livelywpf.exe" or "lively.ui.winui.exe" => "Lively Wallpaper",
        "chrome.exe" => "Google Chrome",
        "msedge.exe" => "Microsoft Edge",
        "firefox.exe" => "Firefox",
        "opera.exe" => "Opera",
        "brave.exe" => "Brave",
        "browser.exe" => Lang.T("Яндекс Браузер", "Yandex Browser"),
        "vivaldi.exe" => "Vivaldi",
        "discord.exe" => "Discord",
        "nvidia overlay.exe" or "nvidia share.exe" => Lang.T("Оверлей NVIDIA", "NVIDIA overlay"),
        "gamebar.exe" => "Xbox Game Bar",
        "rtss.exe" => "RivaTuner Statistics Server",
        _ => exe,
    };

    private static string Advice(string exe)
    {
        string n = exe.ToLowerInvariant();
        if (n is "wallpaper32.exe" or "wallpaper64.exe")
            return Lang.T("Настройки → Производительность → «Другое приложение развёрнуто» и «…во весь экран» → «Пауза» (лучше «Стоп»).",
                "Settings → Performance → “Other application maximized” and “…fullscreen” → Pause (Stop is even better).");
        if (n.StartsWith("lively"))
            return Lang.T("Настройки → Производительность → «Во весь экран / развёрнуто» → «Пауза».",
                "Settings → Performance → “Fullscreen / maximized” → Pause.");
        if (Browsers.Contains(exe))
            return Lang.T("Поставьте видео на паузу или закройте вкладку. Если браузер мешает постоянно — выключите в его настройках аппаратное ускорение.",
                "Pause the video or close the tab. If the browser always interferes, turn off hardware acceleration in its settings.");
        if (n == "discord.exe")
            return Lang.T("Настройки → Игровой оверлей → выключить. Настройки → Расширенные → «Аппаратное ускорение» — по желанию.",
                "Settings → Game Overlay → off. Settings → Advanced → Hardware acceleration — optional.");
        if (n is "nvidia overlay.exe" or "nvidia share.exe")
            return Lang.T("NVIDIA App → Настройки → «Оверлей в игре» — выключите, если не пользуетесь записью и счётчиком FPS.",
                "NVIDIA App → Settings → In-game overlay — turn off if you don't use recording or the FPS counter.");
        if (n == "gamebar.exe")
            return Lang.T("Параметры → Игры → Xbox Game Bar — выключите, если не пользуетесь.", "Settings → Gaming → Xbox Game Bar — turn off if unused.");
        if (n == "rtss.exe")
            return Lang.T("Если ограничиваете им кадры — оставьте. Оверлей можно выключить: On-Screen Display support → Off.",
                "Keep it if you use it to cap frames. The overlay can be turned off: On-Screen Display support → Off.");
        return "";
    }

    public override FixState Evaluate(FixContext ctx)
    {
        var groups = ctx.Running
            .Where(p => !Browsers.Contains(p.Name) || p.GpuPercent >= 2)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<FixEntry>();
        foreach (var g in groups)
        {
            double gpu = g.Sum(p => p.GpuPercent);
            string title = DisplayName(g.Key) + (gpu >= 0.5 ? Lang.T($" — видеокарта {gpu:0}%", $" — GPU {gpu:0}%") : "");
            entries.Add(new FixEntry(title, Advice(g.Key), new FixAction("close:" + g.Key, Lang.T("Закрыть", "Close"), Danger: true), Warning: gpu >= 5));
        }
        foreach (var kv in _closed)
            entries.Add(new FixEntry(kv.Value + Lang.T(" — закрыта WinPulse", " — closed by WinPulse"), kv.Key,
                new FixAction("start:" + kv.Key, Lang.T("Запустить снова", "Start again"))));

        return new FixState
        {
            Status = groups.Count == 0 ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = groups.Count == 0
                ? Lang.T("Не найдено", "None found")
                : Lang.T($"Найдено: {groups.Count}", $"Found: {groups.Count}"),
            Reason = groups.Count == 0 ? null : Lang.T(
                "Закрывать не обязательно — часто достаточно настроить паузу на время игры.",
                "Closing isn't required — often it's enough to set them to pause while gaming."),
            Entries = entries,
        };
    }

    public override async Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host)
    {
        if (actionId.StartsWith("start:", StringComparison.Ordinal))
        {
            string path = actionId[6..];
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
                _closed.Remove(path);
            }
            catch (Exception ex)
            {
                host.Notify(Title, ex.Message);
            }
            return ActionOutcome.Refreshed;
        }

        if (!actionId.StartsWith("close:", StringComparison.Ordinal)) return ActionOutcome.None;
        string exe = actionId[6..];
        string name = DisplayName(exe);
        bool confirmed = await host.ConfirmAsync(
            Lang.T($"Закрыть «{name}»?", $"Close “{name}”?"),
            Lang.T("Программа получит обычный запрос на закрытие, как при нажатии на крестик. Несохранённые данные в ней могут потеряться. " +
                   "Запустить её снова можно здесь же.",
                "The program gets a normal close request, as if you clicked its X. Unsaved data may be lost. You can start it again from here."),
            Lang.T("Закрыть", "Close"), danger: true);
        if (!confirmed) return ActionOutcome.None;

        string processName = Path.GetFileNameWithoutExtension(exe);
        var processes = Process.GetProcessesByName(processName);
        string? path = null;
        foreach (var p in processes)
        {
            try
            {
                path ??= Collectors.ProcessPaths.TryGet(p.Id);
                p.CloseMainWindow();
            }
            catch
            {
            }
        }

        await Task.Delay(3000);
        var survivors = Process.GetProcessesByName(processName).ToList();
        if (survivors.Count > 0)
        {
            bool kill = await host.ConfirmAsync(
                Lang.T($"«{name}» не закрылась", $"“{name}” didn't close"),
                Lang.T("Программа не ответила на запрос (часто так ведут себя программы в трее). Завершить её принудительно?",
                    "The program ignored the request (tray apps often do). Force it to quit?"),
                Lang.T("Завершить", "End task"), danger: true);
            if (kill)
            {
                foreach (var p in survivors)
                {
                    try { p.Kill(entireProcessTree: false); } catch { }
                }
            }
        }
        foreach (var p in processes) p.Dispose();
        foreach (var p in survivors) p.Dispose();

        if (path is not null && Process.GetProcessesByName(processName).Length == 0) _closed[path] = name;
        return ActionOutcome.Refreshed;
    }
}

// 10. Мониторы с разной частотой
public sealed class MultiMonitorItem : FixItem
{
    public override string Id => "multi-monitor";
    public override int Number => 10;
    public override string Title => Lang.T("Мониторы с разной частотой", "Monitors with different refresh rates");
    public override string Explanation => Lang.T(
        "Когда рядом работают мониторы, например, 165 и 60 Гц, в Windows 10 игра в окне может подстраиваться под меньшую частоту и дёргаться. " +
        "Помогает поднять частоту второго монитора до максимума, а для проверки — временно отключить его. В Windows 11 это случается реже.",
        "With monitors like 165 Hz and 60 Hz side by side, a windowed game on Windows 10 can sync to the slower one and stutter. " +
        "Raising the second monitor to its maximum refresh rate helps; to test, temporarily turn it off. Windows 11 is less affected.");

    private static string VrrText(VrrSupport v) => v switch
    {
        VrrSupport.Yes => Lang.T("есть", "yes"),
        VrrSupport.Likely => Lang.T("вероятно", "likely"),
        _ => Lang.T("нет", "no"),
    };

    public override FixState Evaluate(FixContext ctx)
    {
        var detached = ctx.Backup.Entries.Where(e => e.Kind == "detach").ToList();
        var entries = ctx.Monitors.Select(m => new FixEntry(
            m.FriendlyName + (m.IsPrimary ? Lang.T(" (основной)", " (primary)") : ""),
            Lang.T($"{m.Width}×{m.Height}, {m.RefreshHz} Гц (максимум {m.MaxRefreshHz} Гц), масштаб {m.ScalePercent}%, VRR: {VrrText(m.Vrr)}",
                $"{m.Width}×{m.Height}, {m.RefreshHz} Hz (max {m.MaxRefreshHz} Hz), scale {m.ScalePercent}%, VRR: {VrrText(m.Vrr)}"))).ToList();

        if (detached.Count > 0)
        {
            return new FixState
            {
                Status = FixStatus.Improvable,
                StatusText = Lang.T("Второй монитор временно отключён", "Second monitor is temporarily off"),
                Reason = Lang.T("Проверьте игру. Когда закончите — верните монитор кнопкой ниже.",
                    "Test the game. When you're done, bring the monitor back with the button below."),
                Entries = entries,
                Actions = new[] { new FixAction("reattach", Lang.T("Вернуть второй монитор", "Bring the second monitor back")) },
            };
        }

        if (ctx.Monitors.Count < 2)
            return FixState.Unavailable(Lang.T("Подключён один монитор — пункт не нужен.", "Only one monitor is connected — nothing to do."));

        var main = ctx.GameMonitor!;
        var others = ctx.Monitors.Where(m => !ReferenceEquals(m, main)).ToList();
        bool mixed = others.Any(o => o.RefreshHz != main.RefreshHz);
        var testAction = new FixAction("test", Lang.T("Проверить без второго монитора", "Test without the second monitor"));

        if (!mixed)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T($"Частоты одинаковые ({main.RefreshHz} Гц)", $"Same refresh rate ({main.RefreshHz} Hz)"),
                Entries = entries,
                Actions = new[] { testAction },
            };

        var fixable = others.Where(o => o.RefreshHz < o.MaxRefreshHz).ToList();
        string rates = main.RefreshHz + " / " + string.Join(" / ", others.Select(o => o.RefreshHz)) + " " + WgPaths.Hz;
        string reason = fixable.Count > 0
            ? Lang.T($"Можно поднять частоту: {string.Join(", ", fixable.Select(f => $"{f.FriendlyName} {f.RefreshHz} → {f.MaxRefreshHz} Гц"))}.",
                $"Refresh can be raised: {string.Join(", ", fixable.Select(f => $"{f.FriendlyName} {f.RefreshHz} → {f.MaxRefreshHz} Hz"))}.")
            : Lang.T("Второй монитор уже работает на своей максимальной частоте. Если игра в окне дёргается — проверьте без него.",
                "The second monitor already runs at its maximum refresh rate. If the windowed game stutters, test without it.");
        if (ctx.Os.IsWindows11)
            reason += " " + Lang.T("В Windows 11 разные частоты мешают редко.", "On Windows 11 mixed refresh rates rarely cause trouble.");

        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = rates,
            Reason = reason,
            CanFix = fixable.Count > 0,
            FixLabel = Lang.T("Поднять частоту до максимума", "Raise refresh to maximum"),
            Recommended = fixable.Count > 0 && !ctx.Os.IsWindows11,
            Entries = entries,
            Actions = new[] { testAction },
        };
    }

    public override List<Change> PlanFix(FixContext ctx)
    {
        var main = ctx.GameMonitor;
        if (main is null) return new();
        return ctx.Monitors
            .Where(m => !ReferenceEquals(m, main) && m.RefreshHz < m.MaxRefreshHz)
            .Select(m => new Change
            {
                ItemId = Id,
                Kind = ChangeKind.DisplayRefresh,
                DeviceName = m.DeviceName,
                Width = m.Width,
                Height = m.Height,
                Frequency = m.MaxRefreshHz,
                Title = Title + " — " + m.FriendlyName,
                Before = $"{m.RefreshHz} {WgPaths.Hz}",
                After = $"{m.MaxRefreshHz} {WgPaths.Hz}",
                Why = Lang.T("Чем ближе частоты мониторов, тем реже Windows 10 подстраивает игру в окне под медленный экран.",
                    "The closer the refresh rates, the less Windows 10 syncs a windowed game to the slower screen."),
            })
            .ToList();
    }

    public override async Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host)
    {
        if (actionId == "reattach")
        {
            var errors = Reattach(ctx.Backup);
            if (errors.Count > 0) host.Notify(Title, string.Join("\n", errors));
            return ActionOutcome.Refreshed;
        }
        if (actionId != "test") return ActionOutcome.None;

        var main = ctx.GameMonitor;
        if (main is null || ctx.Monitors.Count < 2) return ActionOutcome.None;
        var others = ctx.Monitors.Where(m => !ReferenceEquals(m, main)).ToList();
        string names = string.Join(", ", others.Select(o => o.FriendlyName));

        bool ok = await host.ConfirmAsync(
            Lang.T("Проверить без второго монитора?", "Test without the second monitor?"),
            Lang.T($"Монитор «{names}» будет временно отключён — окна переедут на «{main.FriendlyName}». " +
                   "Если изображение пропадёт, ничего не нажимайте: через 15 секунд всё вернётся само.",
                $"“{names}” will be turned off for now — windows move to “{main.FriendlyName}”. " +
                "If the picture disappears, don't press anything: everything comes back in 15 seconds."),
            Lang.T("Отключить на время", "Turn off for now"));
        if (!ok) return ActionOutcome.None;

        var failures = new List<string>();
        foreach (var m in others)
        {
            var entry = new BackupEntry
            {
                TargetId = ("detach:" + m.DeviceName).ToLowerInvariant(),
                ItemId = Id,
                Kind = "detach",
                SavedAt = DateTime.Now,
                Title = Title,
                Existed = true,
                DeviceName = m.DeviceName,
                Width = m.Width,
                Height = m.Height,
                Frequency = m.RefreshHz,
                PositionX = m.PositionX,
                PositionY = m.PositionY,
                BitsPerPixel = m.BitsPerPixel,
            };
            ctx.Backup.Add(entry); // сначала резервная копия, потом изменение
            string? error = await Task.Run(() => Displays.Detach(m.DeviceName));
            if (error is not null)
            {
                failures.Add($"{m.FriendlyName}: {error}");
                ctx.Backup.Remove(entry.TargetId);
            }
        }
        if (failures.Count > 0) host.Notify(Title, string.Join("\n", failures));
        if (failures.Count == others.Count) return ActionOutcome.Refreshed;

        bool keep = await host.KeepChangesAsync(
            Lang.T("Оставить без второго монитора?", "Keep the second monitor off?"),
            Lang.T("Если вы видите это окно — всё работает. Оставьте так, запустите игру и проверьте плавность. " +
                   "Вернуть монитор можно кнопкой «Вернуть второй монитор» в пункте 10 или «Вернуть всё как было».",
                "If you can see this, everything works. Keep it, start the game and check smoothness. " +
                "Bring the monitor back with “Bring the second monitor back” in item 10 or with “Restore everything”."),
            15);
        if (!keep)
        {
            var errors = Reattach(ctx.Backup);
            if (errors.Count > 0) host.Notify(Title, string.Join("\n", errors));
        }
        return ActionOutcome.Refreshed;
    }

    private static List<string> Reattach(BackupStore backup)
    {
        var errors = new List<string>();
        foreach (var e in backup.Entries.Where(e => e.Kind == "detach"))
        {
            string? error = Displays.Reattach(e.DeviceName ?? "", e.PositionX, e.PositionY, e.Width, e.Height, e.Frequency, e.BitsPerPixel);
            if (error is null) backup.Remove(e.TargetId);
            else errors.Add($"{e.DeviceName}: {error}");
        }
        return errors;
    }
}

// 11. Ограничение FPS на 3 ниже частоты
public sealed class FpsCapItem : FixItem
{
    private const string Mark = "windowed.fps-cap-done";

    public override string Id => "fps-cap";
    public override int Number => 11;
    public override string Title => Lang.T("Ограничение кадров на 3 ниже частоты", "Cap FPS 3 below the refresh rate");
    public override string Explanation => Lang.T(
        "С G-SYNC/FreeSync кадры должны оставаться внутри диапазона переменной частоты. Если FPS упирается в частоту монитора, " +
        "включается обычная вертикальная синхронизация или появляются разрывы, и растёт задержка. Ограничение чуть ниже частоты держит игру внутри диапазона.",
        "With G-SYNC/FreeSync, frames must stay inside the VRR range. When FPS hits the refresh rate, " +
        "regular V-Sync kicks in or tearing appears, and latency grows. A cap slightly below the refresh rate keeps the game inside the range.");

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.BestVrr == VrrSupport.No)
            return FixState.Unavailable(Lang.T("Без G-SYNC/FreeSync этот предел не нужен — мониторы не сообщают о поддержке VRR.",
                "Without G-SYNC/FreeSync this cap isn't needed — your monitors don't report VRR support."));

        var m = ctx.Monitors.Where(x => x.Vrr != VrrSupport.No).OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.MaxRefreshHz).First();
        int cap = Math.Max(30, m.RefreshHz - 3);
        var vrrRange = m.Edid?.VrrRange;
        string range = vrrRange is { } r
            ? Lang.T($" Диапазон VRR монитора: {r.Min}–{r.Max} Гц.", $" Monitor VRR range: {r.Min}–{r.Max} Hz.")
            : "";

        var steps = new List<string>();
        switch (ctx.Vendor)
        {
            case GpuVendor.Nvidia:
                steps.Add(Lang.T($"Панель управления NVIDIA → «Управление параметрами 3D» → «Макс. частота кадров» → {cap}. " +
                                 "Можно задать для всех игр или только для этой (вкладка «Программные настройки»).",
                    $"NVIDIA Control Panel → Manage 3D settings → Max Frame Rate → {cap}. Set it globally or per game (Program Settings tab)."));
                break;
            case GpuVendor.Amd:
                steps.Add(Lang.T($"AMD Software → «Игры» → выберите игру → «Radeon Chill»: минимум и максимум {cap} (или Frame Rate Target Control = {cap}).",
                    $"AMD Software → Gaming → pick the game → Radeon Chill: min and max {cap} (or Frame Rate Target Control = {cap})."));
                break;
            case GpuVendor.Intel:
                steps.Add(Lang.T($"Intel Graphics Software / Arc Control → «Игры» → ограничитель частоты кадров → {cap}.",
                    $"Intel Graphics Software / Arc Control → Gaming → frame rate limiter → {cap}."));
                break;
        }
        steps.Add(Lang.T($"Или встроенный ограничитель в настройках самой игры: {cap} FPS — обычно он даёт наименьшую задержку.",
            $"Or the game's own limiter: {cap} FPS — it usually adds the least latency."));
        steps.Add(Lang.T($"Или RivaTuner Statistics Server: Framerate limit = {cap}.", $"Or RivaTuner Statistics Server: Framerate limit = {cap}."));

        bool done = ctx.IsMarked(Mark);
        var actions = new List<FixAction> { new("mark", done ? Lang.T("Снять отметку", "Unmark") : Lang.T("Я настроил", "I've set it")) };
        if (ctx.Vendor is GpuVendor.Nvidia or GpuVendor.Amd)
            actions.Insert(0, new FixAction("open-panel", Lang.T("Открыть панель драйвера", "Open driver panel")));

        return new FixState
        {
            Status = done ? FixStatus.Ok : FixStatus.Improvable,
            StatusText = done
                ? Lang.T("Отмечено вами", "Marked by you")
                : Lang.T($"Рекомендуемый предел: {cap} FPS", $"Recommended cap: {cap} FPS"),
            Reason = Lang.T($"Монитор «{m.FriendlyName}» сейчас работает на {m.RefreshHz} Гц.{range}",
                $"“{m.FriendlyName}” currently runs at {m.RefreshHz} Hz.{range}"),
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
        if (actionId == "open-panel" && !DriverPanel.TryOpen(ctx.Vendor))
            host.Notify(Title, Lang.T("Не удалось открыть панель. Откройте её вручную.", "Could not open the panel. Open it manually."));
        return Task.FromResult(ActionOutcome.None);
    }
}
