using System.ComponentModel;
using WinPulse.Core;

namespace WinPulse.App.Infrastructure;

/// <summary>
/// Строки интерфейса для XAML: Text="{Binding [nav.overview], Source={x:Static i:Loc.I}}".
/// При смене языка все привязки обновляются сами.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private static readonly Dictionary<string, (string Ru, string En)> Map = new()
    {
        // Оболочка
        ["app.tagline"] = ("монитор и оптимизация", "monitor & optimizer"),
        ["nav.monitoring"] = ("МОНИТОРИНГ", "MONITORING"),
        ["nav.diagnostics"] = ("ДИАГНОСТИКА", "DIAGNOSTICS"),
        ["nav.optimization"] = ("ОПТИМИЗАЦИЯ", "OPTIMIZATION"),
        ["nav.soon"] = ("СКОРО", "COMING SOON"),
        ["nav.overview"] = ("Обзор", "Overview"),
        ["nav.cpu"] = ("Процессор", "Processor"),
        ["nav.memory"] = ("Память", "Memory"),
        ["nav.disk"] = ("Диски", "Disks"),
        ["nav.gpu"] = ("Видеокарта", "Graphics"),
        ["nav.network"] = ("Сеть", "Network"),
        ["nav.windowed"] = ("Игры в окне", "Windowed games"),
        ["nav.problems"] = ("Проблемы", "Problems"),
        ["nav.optimize"] = ("Оптимизация", "Tune-up"),
        ["nav.drivers"] = ("Драйверы", "Drivers"),
        ["nav.startup"] = ("Автозагрузка", "Startup"),
        ["nav.lab"] = ("Лаборатория FPS", "FPS lab"),
        ["shell.pro"] = ("Режим «Профи»", "Pro mode"),
        ["shell.pro.hint"] = ("Больше столбцов, пути в реестре и технические подробности", "More columns, registry paths and technical details"),
        ["shell.language"] = ("Язык", "Language"),
        ["shell.demo"] = ("ДЕМО-ДАННЫЕ", "DEMO DATA"),
        ["shell.demo.hint"] = ("Программа запущена с ключом --demo: цифры выдуманы", "Started with --demo: numbers are simulated"),
        ["shell.admin"] = ("Администратор", "Administrator"),
        ["shell.user"] = ("Обычные права", "Standard rights"),
        ["shell.log"] = ("Журнал", "Log"),

        // Общие
        ["common.cancel"] = ("Отмена", "Cancel"),
        ["common.close"] = ("Закрыть", "Close"),
        ["common.ok"] = ("Готово", "Done"),
        ["common.select"] = ("Выбрать", "Select"),
        ["common.refresh"] = ("Обновить", "Refresh"),
        ["common.details"] = ("Подробнее", "Details"),
        ["common.admin"] = ("нужны права администратора", "needs admin rights"),
        ["common.restart"] = ("нужна перезагрузка", "restart required"),
        ["common.before"] = ("Было", "Before"),
        ["common.after"] = ("Станет", "After"),
        ["common.why"] = ("Зачем", "Why"),
        ["common.where"] = ("Где", "Where"),

        // Игры в окне
        ["wg.title"] = ("Игры в окне и без рамки", "Windowed & borderless games"),
        ["wg.subtitle"] = ("13 проверок для плавной игры в окне. Перед любым изменением WinPulse сохраняет старое значение и показывает, что именно меняется.",
            "13 checks for smooth windowed gaming. Before any change WinPulse saves the old value and shows exactly what changes."),
        ["wg.summary"] = ("Игра в окне: {0} из {1} настроено", "Windowed gaming: {0} of {1} set up"),
        ["opt.title"] = ("Оптимизация", "Tune-up"),
        ["opt.subtitle"] = ("Сверху — то, что влияет на игру, снизу — уборка Windows, которая на FPS не влияет (так и написано). У каждой настройки указана цена. Перед изменением WinPulse сохраняет старое значение, вернуть можно в один клик. Схема питания, игровой режим и планировщик GPU — в разделе «Игры в окне».",
            "On top — what affects games; below — Windows clean-up that does not affect FPS (and says so). Each setting shows its cost. WinPulse saves the old value before any change and restores it in one click. Power plan, Game Mode and GPU scheduling are in “Windowed games”."),
        ["opt.summary"] = ("Оптимизация: {0} из {1} в порядке", "Tune-up: {0} of {1} OK"),
        ["wg.game"] = ("Игра", "Game"),
        ["wg.game.placeholder"] = ("Путь к exe игры, например D:\\Games\\Game\\game.exe", "Path to the game exe, e.g. D:\\Games\\Game\\game.exe"),
        ["wg.browse"] = ("Обзор…", "Browse…"),
        ["wg.running"] = ("Из запущенных…", "From running…"),
        ["wg.fixall"] = ("Исправить всё рекомендуемое", "Fix all recommended"),
        ["wg.revertall"] = ("Вернуть всё как было", "Restore everything"),
        ["wg.fix"] = ("Исправить", "Fix"),
        ["wg.revert"] = ("Вернуть как было", "Restore"),
        ["wg.steps"] = ("Как сделать", "How to"),
        ["wg.technical"] = ("Технически", "Technical"),
        ["wg.backup"] = ("Резервная копия", "Backup"),
        ["wg.status.ok"] = ("в порядке", "OK"),
        ["wg.status.improve"] = ("можно улучшить", "can be improved"),
        ["wg.status.na"] = ("недоступно на этой системе", "not available on this system"),
        ["wg.picker.title"] = ("Выберите игру", "Pick the game"),
        ["wg.picker.hint"] = ("Запустите игру, затем выберите её в списке. Показаны программы с открытыми окнами.",
            "Start the game, then pick it from the list. Programs with open windows are shown."),

        // Диалоги
        ["dlg.confirm.title"] = ("Подтвердите изменения", "Confirm changes"),
        ["dlg.confirm.apply"] = ("Применить", "Apply"),
        ["dlg.confirm.lead"] = ("WinPulse сохранит текущие значения в резервную копию и внесёт следующие изменения:",
            "WinPulse will back up the current values and make these changes:"),
        ["dlg.confirm.admin"] = ("Для отмеченных пунктов Windows спросит разрешение администратора (один раз на всю пачку).",
            "Windows will ask for administrator permission for the marked items (once for the whole batch)."),
        ["dlg.result.title"] = ("Итог", "Result"),
        ["dlg.result.restart"] = ("Перезагрузить сейчас", "Restart now"),
        ["dlg.keep.keep"] = ("Оставить так", "Keep"),
        ["dlg.keep.revert"] = ("Вернуть сейчас", "Revert now"),
    };

    private Loc()
    {
        Lang.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] =>
        Map.TryGetValue(key, out var v) ? (Lang.English ? v.En : v.Ru) : key;

    public static string Get(string key) => I[key];

    public event PropertyChangedEventHandler? PropertyChanged;
}
