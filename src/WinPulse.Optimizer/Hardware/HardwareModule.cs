using WinPulse.Collectors.Gpu;
using WinPulse.Collectors.Hardware;
using WinPulse.Core;
using WinPulse.Core.Formatting;
using WinPulse.Core.Model;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.Optimizer.Hardware;

/// <summary>
/// «Железо и FPS»: то, где теряется больше всего кадров и что Windows сама не исправит — XMP, каналы памяти,
/// шина видеокарты, кабель монитора, игры на HDD. Ничего не меняет, только находит и объясняет, что сделать.
/// </summary>
public sealed class HardwareModule : FixModule
{
    public HardwareModule(string? backupPath = null)
        : base("hardware", new FixItem[]
        {
            new MonitorCableItem(),
            new RamSpeedItem(),
            new DualChannelItem(),
            new RamAmountItem(),
            new GpuLinkItem(),
            new GamesOnHddItem(),
        }, backupPath)
    {
    }

    public override bool NeedsHardware => true;
    public override bool IsDiagnosticOnly => true;

    internal static FixState NoData() => FixState.Unavailable(Lang.T(
        "Windows не отдала сведения о памяти (служба WMI не ответила). Попробуйте «Обновить» чуть позже.",
        "Windows didn't report memory details (WMI didn't respond). Try Refresh a bit later."));
}

// 1. Монитор в материнской плате
public sealed class MonitorCableItem : FixItem
{
    public override string Id => "monitor-cable";
    public override int Number => 1;
    public override string Title => Lang.T("Монитор подключён к видеокарте", "Monitor is plugged into the graphics card");
    public override string Explanation => Lang.T(
        "Частая ошибка после сборки или переезда: кабель монитора вставлен в разъём на материнской плате, а не на видеокарте. " +
        "Тогда игра либо идёт на встроенной графике процессора, либо каждый кадр гоняется через неё — FPS в разы ниже, чем может видеокарта.",
        "A common mistake after building or moving a PC: the monitor cable is in the motherboard's port, not the graphics card's. " +
        "Then the game either runs on the CPU's integrated graphics or every frame is routed through it — far fewer FPS than the card can do.");

    private static bool IsDiscrete(GpuAdapterInfo g) =>
        g.DedicatedMemory >= 2L * 1024 * 1024 * 1024 &&
        (g.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

    public override FixState Evaluate(FixContext ctx)
    {
        if (ctx.Hardware?.IsLaptop == true || ctx.HasBattery)
            return FixState.Unavailable(Lang.T(
                "Это ноутбук: встроенный экран всегда подключён через встроенную графику (NVIDIA Optimus, AMD Switchable) — это нормально. Важно, чтобы сама игра шла на мощной видеокарте — это проверяет пункт в «Играх в окне».",
                "This is a laptop: the built-in screen is always wired through integrated graphics (NVIDIA Optimus, AMD Switchable) — that's normal. What matters is that the game itself runs on the powerful GPU — see the item in Windowed games."));

        var discrete = ctx.Gpus.Where(IsDiscrete).ToList();
        if (discrete.Count == 0)
            return FixState.Unavailable(Lang.T("Отдельная видеокарта не найдена — проверять нечего.", "No discrete graphics card found — nothing to check."));
        if (ctx.Monitors.Count == 0)
            return FixState.Unavailable(Lang.T("Не удалось получить список мониторов.", "Couldn't list the monitors."));

        var entries = new List<FixEntry>();
        var wrong = new List<string>();
        foreach (var m in ctx.Monitors)
        {
            bool onDiscrete = discrete.Any(d => SameName(d.Name, m.AdapterName));
            entries.Add(new FixEntry(m.FriendlyName, Lang.T("подключён к: ", "connected to: ") + m.AdapterName, Warning: !onDiscrete));
            if (!onDiscrete) wrong.Add(m.FriendlyName);
        }

        string technical = string.Join("\n", ctx.Monitors.Select(m => $"{m.DeviceName}: {m.AdapterName}")) + "\n" +
                           Lang.T("Видеокарты: ", "GPUs: ") + string.Join(", ", ctx.Gpus.Select(g => $"{g.Name} ({Ru.Bytes(g.DedicatedMemory)})"));
        if (wrong.Count == 0)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T("Всё подключено к видеокарте", "Everything is on the graphics card"),
                Entries = entries,
                Technical = technical,
            };

        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T("Монитор в материнской плате", "Monitor on the motherboard"),
            Reason = Lang.T($"{string.Join(", ", wrong)} — подключён к встроенной графике, а не к {discrete[0].Name}.",
                $"{string.Join(", ", wrong)} — connected to integrated graphics, not to the {discrete[0].Name}."),
            Entries = entries,
            Steps =
            [
                Lang.T("Выключите компьютер.", "Shut down the PC."),
                Lang.T("Сзади корпуса найдите разъёмы видеокарты — горизонтальный ряд ниже, на отдельной металлической планке. Разъёмы материнской платы — вертикально, рядом с USB.",
                    "At the back of the case find the graphics card's ports — a horizontal row lower down, on a separate metal bracket. Motherboard ports are vertical, next to USB."),
                Lang.T("Переставьте кабель монитора в разъём видеокарты (DisplayPort или HDMI) и включите компьютер.", "Move the monitor cable to a graphics card port (DisplayPort or HDMI) and power on."),
            ],
            Technical = technical,
        };
    }

    private static bool SameName(string a, string b) =>
        a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase) ||
        a.Contains(b.Trim(), StringComparison.OrdinalIgnoreCase) || b.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase);
}

// 2. Частота памяти (XMP / EXPO)
public sealed class RamSpeedItem : FixItem
{
    public override string Id => "ram-xmp";
    public override int Number => 2;
    public override string Title => Lang.T("Память на своей частоте (XMP / EXPO)", "RAM at its rated speed (XMP / EXPO)");
    public override string Explanation => Lang.T(
        "Планки продаются с профилем XMP или EXPO (например, DDR4-3200), но материнская плата по умолчанию запускает их на базовой частоте — часто 2133–2666. " +
        "В играх, которые упираются в процессор, медленная память снижает FPS и особенно 1% low. Включение профиля — штатная настройка BIOS, гарантия не теряется.",
        "Memory kits ship with an XMP or EXPO profile (e.g. DDR4-3200), but motherboards boot them at base speed by default — often 2133–2666. " +
        "In CPU-bound games slow memory costs FPS and especially 1% lows. Enabling the profile is a standard BIOS setting; it doesn't void the warranty.");

    public override FixState Evaluate(FixContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw is null || !hw.MemoryKnown) return HardwareModule.NoData();
        var modules = hw.Modules.Where(m => m.ConfiguredMts > 0).ToList();
        if (modules.Count == 0)
            return FixState.Unavailable(Lang.T("Windows не сообщила частоту памяти.", "Windows didn't report the memory speed."));

        int configured = modules.Min(m => m.ConfiguredMts);
        int rated = hw.Modules.Max(m => m.RatedMts);
        string technical = string.Join("\n", hw.Modules.Select(m =>
            $"{m.Slot} ({m.Bank}): {Ru.Bytes(m.Capacity)}, {m.Maker} {m.PartNumber} — " +
            Lang.T($"паспорт {m.RatedMts}, сейчас {m.ConfiguredMts} МТ/с", $"rated {m.RatedMts}, now {m.ConfiguredMts} MT/s")));

        if (rated <= configured * 1.08)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T($"{configured} МТ/с — паспортная частота", $"{configured} MT/s — rated speed"),
                Technical = technical,
            };

        if (hw.IsLaptop)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T($"{configured} из {rated} МТ/с", $"{configured} of {rated} MT/s"),
                Reason = Lang.T("На ноутбуках частоту памяти задаёт производитель, профиля XMP в BIOS обычно нет.",
                    "On laptops the memory speed is set by the manufacturer; BIOS usually has no XMP option."),
                Technical = technical,
            };

        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T($"{configured} вместо {rated} МТ/с", $"{configured} instead of {rated} MT/s"),
            Reason = Lang.T($"Память работает на {configured} МТ/с, а рассчитана на {rated}. Профиль XMP/EXPO в BIOS выключен.",
                $"Memory runs at {configured} MT/s but is rated for {rated}. The XMP/EXPO profile is off in BIOS."),
            Steps = StepsFor(hw.BoardMaker),
            Technical = technical,
        };
    }

    private static IReadOnlyList<string> StepsFor(string maker)
    {
        string m = maker.ToLowerInvariant();
        string where = m.Contains("asus")
            ? Lang.T("Ai Tweaker (или EZ Mode) → Ai Overclock Tuner → XMP I (на платах для AMD — DOCP или EXPO).", "Ai Tweaker (or EZ Mode) → Ai Overclock Tuner → XMP I (DOCP or EXPO on AMD boards).")
            : m.Contains("micro-star") || m.Contains("msi")
                ? Lang.T("кнопка A-XMP / XMP / EXPO в верхней части главного экрана BIOS → «Профиль 1».", "the A-XMP / XMP / EXPO button at the top of the main BIOS screen → Profile 1.")
                : m.Contains("gigabyte")
                    ? Lang.T("Tweaker → Extreme Memory Profile (X.M.P.) → Profile1.", "Tweaker → Extreme Memory Profile (X.M.P.) → Profile1.")
                    : m.Contains("asrock")
                        ? Lang.T("OC Tweaker → DRAM Configuration → Load XMP Setting → XMP Profile 1.", "OC Tweaker → DRAM Configuration → Load XMP Setting → XMP Profile 1.")
                        : Lang.T("раздел разгона или памяти (Overclocking / Memory / DRAM) → параметр XMP, EXPO или DOCP → Profile 1.", "the overclocking or memory section (Overclocking / Memory / DRAM) → XMP, EXPO or DOCP → Profile 1.");
        return
        [
            Lang.T("Перезагрузите компьютер и при включении нажимайте Del (на некоторых платах — F2), чтобы войти в BIOS.", "Restart and press Del (F2 on some boards) during startup to enter BIOS."),
            Lang.T("Найдите: ", "Find: ") + where,
            Lang.T("Сохраните и выйдите (обычно F10).", "Save and exit (usually F10)."),
            Lang.T("Если компьютер не запустится, плата сама вернёт старые настройки после 2–3 попыток. Вернуться к прежнему можно и вручную: выключите XMP обратно.",
                "If the PC won't start, the board restores the previous settings after 2–3 attempts. You can also turn XMP back off manually."),
            Lang.T("Нажмите «Обновить» на этой странице — частота должна стать паспортной.", "Click Refresh on this page — the speed should match the rating."),
        ];
    }
}

// 3. Каналы памяти
public sealed class DualChannelItem : FixItem
{
    public override string Id => "ram-channels";
    public override int Number => 3;
    public override string Title => Lang.T("Память в двухканальном режиме", "Memory in dual-channel mode");
    public override string Explanation => Lang.T(
        "Процессор читает память по двум каналам сразу, если планки две и стоят в правильных слотах. С одной планкой (или с двумя в одном канале) " +
        "пропускная способность памяти вдвое ниже — это заметно бьёт по FPS, а встроенной графике особенно.",
        "The CPU reads memory over two channels at once when there are two sticks in the right slots. With one stick (or two in the same channel) " +
        "memory bandwidth halves — a noticeable FPS hit, especially for integrated graphics.");

    private static string SlotsHint => Lang.T(
        "Для двух планок почти всегда нужны слоты A2 и B2 — второй и четвёртый, если считать от процессора. Точно — в инструкции к плате (обычно подписаны и на самой плате).",
        "For two sticks you almost always want slots A2 and B2 — the second and fourth counting from the CPU. Check the board manual (they're usually labelled on the board too).");

    public override FixState Evaluate(FixContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw is null || !hw.MemoryKnown) return HardwareModule.NoData();
        var modules = hw.Modules;
        string technical = string.Join("\n", modules.Select(m => $"{m.Slot} / {m.Bank}: {Ru.Bytes(m.Capacity)} · " +
                                                                 Lang.T("канал ", "channel ") + (m.Channel ?? "?"))) +
                           "\n" + Lang.T($"Слотов на плате (по SMBIOS): {hw.MemorySlots}", $"Board slots (SMBIOS): {hw.MemorySlots}");

        if (modules.Count == 1)
        {
            if (hw.MemorySlots == 1)
                return new FixState
                {
                    Status = FixStatus.Ok,
                    StatusText = Lang.T("Одна планка, других слотов нет", "One stick, no other slots"),
                    Technical = technical,
                };
            return new FixState
            {
                Status = FixStatus.Improvable,
                StatusText = Lang.T("Одна планка — одноканальный режим", "One stick — single channel"),
                Reason = Lang.T($"Установлена одна планка {Ru.Bytes(modules[0].Capacity)}: память работает вполсилы.",
                    $"A single {Ru.Bytes(modules[0].Capacity)} stick is installed: memory runs at half bandwidth."),
                Steps =
                [
                    Lang.T("Купите вторую планку такого же объёма и частоты (лучше — сразу комплект из двух одинаковых).", "Buy a second stick of the same size and speed (a matched kit of two is best)."),
                    SlotsHint,
                    hw.IsLaptop
                        ? Lang.T("В ноутбуке второй слот бывает под крышкой снизу; иногда одна планка распаяна на плате — тогда добавляют вторую такого же объёма.", "In a laptop the second slot is often under the bottom cover; sometimes one module is soldered — then add a matching second one.")
                        : Lang.T("После установки нажмите «Обновить» на этой странице.", "After installing, click Refresh on this page."),
                ],
                Technical = technical,
            };
        }

        var channels = modules.Select(m => m.Channel).ToList();
        if (channels.All(c => c is not null) && channels.Distinct().Count() == 1)
            return new FixState
            {
                Status = FixStatus.Improvable,
                StatusText = Lang.T($"Все планки в канале {channels[0]}", $"All sticks in channel {channels[0]}"),
                Reason = Lang.T("Планки стоят в слотах одного канала — второй канал не используется.", "The sticks sit in slots of one channel — the second channel is unused."),
                Steps =
                [
                    Lang.T("Выключите компьютер и отключите питание.", "Shut down and unplug the PC."),
                    SlotsHint,
                    Lang.T("Переставьте одну из планок в слот другого канала (B вместо A).", "Move one stick to a slot of the other channel (B instead of A)."),
                ],
                Technical = technical,
            };

        string where = channels.All(c => c is not null) ? Lang.T(" — каналы ", " — channels ") + string.Join(" + ", channels.Distinct()) : "";
        return new FixState
        {
            Status = FixStatus.Ok,
            StatusText = Lang.T($"{modules.Count} планки{where}", $"{modules.Count} sticks{where}"),
            Reason = modules.Count % 2 == 1
                ? Lang.T("Нечётное число планок: часть памяти может работать в одноканальном режиме. Лучше 2 или 4 одинаковые.", "An odd number of sticks: part of memory may run single-channel. 2 or 4 identical sticks are best.")
                : null,
            Technical = technical,
        };
    }
}

// 4. Объём памяти
public sealed class RamAmountItem : FixItem
{
    public override string Id => "ram-amount";
    public override int Number => 4;
    public override string Title => Lang.T("Оперативной памяти хватает для игр", "Enough RAM for games");
    public override string Explanation => Lang.T(
        "Современные игры вместе с Windows, браузером и Discord занимают 10–14 ГБ. Когда памяти меньше, Windows сбрасывает часть данных на диск, " +
        "и игра подтормаживает при каждой подгрузке. 16 ГБ — комфортный минимум, 32 ГБ — с запасом.",
        "Modern games plus Windows, a browser and Discord take 10–14 GB. With less RAM, Windows pages data out to disk " +
        "and the game hitches on every load. 16 GB is a comfortable minimum, 32 GB gives headroom.");

    public override FixState Evaluate(FixContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw is null || !hw.MemoryKnown) return HardwareModule.NoData();
        long total = hw.Modules.Sum(m => m.Capacity);
        double gb = total / (1024.0 * 1024 * 1024);
        if (gb >= 15)
            return new FixState { Status = FixStatus.Ok, StatusText = Ru.Bytes(total) };
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T($"{Ru.Bytes(total)} — маловато", $"{Ru.Bytes(total)} — on the low side"),
            Reason = Lang.T("Перед игрой закрывайте браузер и другие тяжёлые программы. Надёжное решение — довести память до 16 ГБ (две планки по 8 ГБ).",
                "Close the browser and other heavy apps before playing. The real fix is 16 GB (two 8 GB sticks)."),
        };
    }
}

// 5. Шина видеокарты
public sealed class GpuLinkItem : FixItem
{
    public override string Id => "gpu-pcie";
    public override int Number => 5;
    public override string Title => Lang.T("Видеокарта на полной шине PCIe", "Graphics card on the full PCIe link");
    public override string Explanation => Lang.T(
        "Видеокарта общается с процессором по шине PCI Express. Если карта стоит в нижнем слоте, через райзер или контакты загрязнены, " +
        "линий становится меньше (x4 вместо x16) — в части игр это заметно снижает FPS. Скорость шины в простое падает сама для экономии энергии — это нормально, проверяем только число линий.",
        "The graphics card talks to the CPU over PCI Express. In a lower slot, through a riser or with dirty contacts " +
        "it gets fewer lanes (x4 instead of x16) — a noticeable FPS loss in some games. The link speed drops at idle to save power — that's normal, so only the lane count is checked.");

    public override FixState Evaluate(FixContext ctx)
    {
        var links = ctx.Hardware?.GpuLinks.Where(l => l.CurrentWidth is not null && l.MaxWidth is not null).ToList() ?? new List<GpuLink>();
        if (links.Count == 0)
            return FixState.Unavailable(Lang.T("Windows не сообщила параметры шины видеокарты.", "Windows didn't report the graphics card's bus parameters."));

        string Gen(int? g) => g is null ? "?" : "Gen" + g;
        var entries = links.Select(l => new FixEntry(l.Name,
            Lang.T($"сейчас x{l.CurrentWidth}, максимум карты x{l.MaxWidth} · {Gen(l.CurrentGen)} из {Gen(l.MaxGen)}",
                $"now x{l.CurrentWidth}, card max x{l.MaxWidth} · {Gen(l.CurrentGen)} of {Gen(l.MaxGen)}"),
            Warning: l.CurrentWidth < l.MaxWidth)).ToList();
        string technical = string.Join("\n", links.Select(l => $"{l.InstanceId}: width {l.CurrentWidth}/{l.MaxWidth}, gen {l.CurrentGen}/{l.MaxGen}"));

        var narrow = links.Where(l => l.CurrentWidth < l.MaxWidth).ToList();
        if (narrow.Count == 0)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = string.Join(", ", links.Select(l => $"x{l.CurrentWidth}")),
                Entries = entries,
                Technical = technical,
            };

        var n = narrow[0];
        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T($"x{n.CurrentWidth} вместо x{n.MaxWidth}", $"x{n.CurrentWidth} instead of x{n.MaxWidth}"),
            Reason = Lang.T($"{n.Name} работает на {n.CurrentWidth} линиях вместо {n.MaxWidth}.", $"{n.Name} runs on {n.CurrentWidth} lanes instead of {n.MaxWidth}."),
            Entries = entries,
            Steps =
            [
                Lang.T("Выключите компьютер и отключите питание.", "Shut down and unplug the PC."),
                Lang.T("Видеокарта должна стоять в верхнем длинном слоте — ближайшем к процессору. Нижние длинные слоты часто подключены только четырьмя линиями.",
                    "The card belongs in the top long slot — closest to the CPU. Lower long slots are often wired with only four lanes."),
                Lang.T("Если карта стоит через райзер (вертикальная установка) — подключите напрямую и проверьте снова: дешёвые райзеры теряют линии.",
                    "If the card is on a riser (vertical mount), plug it in directly and recheck: cheap risers lose lanes."),
                Lang.T("Выньте карту, протрите контакты сухой мягкой тканью и вставьте до щелчка защёлки.", "Remove the card, wipe the contacts with a dry soft cloth and reseat it until the latch clicks."),
                Lang.T("В BIOS проверьте режим слота PCIe x16: должно быть Auto (не Gen1 и не x8/x8, если вторая карта не стоит).", "In BIOS check the PCIe x16 slot mode: it should be Auto (not Gen1 or x8/x8 unless a second card is installed)."),
            ],
            Technical = technical,
        };
    }
}

// 6. Игры на HDD
public sealed class GamesOnHddItem : FixItem
{
    public override string Id => "games-hdd";
    public override int Number => 6;
    public override string Title => Lang.T("Игры на SSD", "Games on an SSD");
    public override string Explanation => Lang.T(
        "Средний FPS от диска почти не зависит, но современные игры постоянно подгружают текстуры и уровни по ходу игры. " +
        "С жёсткого диска (HDD) это в десятки раз медленнее, чем с SSD, — отсюда фризы, «мыльные» текстуры и долгие загрузки.",
        "Average FPS barely depends on the disk, but modern games constantly stream textures and levels while you play. " +
        "From a hard drive (HDD) that's dozens of times slower than from an SSD — hence freezes, blurry textures and long loads.");

    public override FixState Evaluate(FixContext ctx)
    {
        IReadOnlyList<GameInstall> games = ctx.Hardware?.Games ?? Array.Empty<GameInstall>();
        if (games.Count == 0)
            return FixState.Unavailable(Lang.T("Игры Steam и Epic Games не найдены.", "No Steam or Epic Games titles found."));

        var hdd = games.Where(g => g.Media == DiskMediaType.Hdd).ToList();
        string technical = string.Join("\n", games.Select(g => $"{g.Name}: {g.Path} — {g.Media} {g.DiskModel}"));
        if (hdd.Count == 0)
            return new FixState
            {
                Status = FixStatus.Ok,
                StatusText = Lang.T($"Все {games.Count} на SSD", $"All {games.Count} on SSD"),
                Technical = technical,
            };

        return new FixState
        {
            Status = FixStatus.Improvable,
            StatusText = Lang.T($"{hdd.Count} на HDD", $"{hdd.Count} on HDD"),
            Reason = Lang.T($"На жёстком диске: {string.Join(", ", hdd.Take(5).Select(g => g.Name))}{(hdd.Count > 5 ? "…" : "")}.",
                $"On a hard drive: {string.Join(", ", hdd.Take(5).Select(g => g.Name))}{(hdd.Count > 5 ? "…" : "")}."),
            Entries = hdd.Take(12).Select(g => new FixEntry(g.Name, $"{g.Drive} · {g.DiskModel}", Warning: true)).ToList(),
            Steps =
            [
                Lang.T("Steam: Настройки → Хранилище → добавьте библиотеку на SSD. Затем у игры: Свойства → Установленные файлы → «Переместить папку установки».",
                    "Steam: Settings → Storage → add a library on the SSD. Then for the game: Properties → Installed Files → Move install folder."),
                Lang.T("Epic Games: перенесите папку игры на SSD, затем в лаунчере «Установить» и укажите новую папку — лаунчер проверит файлы и не будет качать заново.",
                    "Epic Games: move the game folder to the SSD, then click Install in the launcher and point to the new folder — it verifies files instead of re-downloading."),
                Lang.T("В первую очередь переносите онлайн-игры и большие открытые миры — им SSD важнее всего.", "Move online games and big open worlds first — they benefit most."),
            ],
            Technical = technical,
        };
    }
}
