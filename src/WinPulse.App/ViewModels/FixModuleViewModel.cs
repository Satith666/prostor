using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WinPulse.App.Dialogs;
using WinPulse.App.Infrastructure;
using WinPulse.Core;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.App.ViewModels;

public sealed class FixActionVm
{
    public FixActionVm(string label, bool danger, bool enabled, ICommand command)
    {
        Label = label;
        Danger = danger;
        Enabled = enabled;
        Command = command;
    }

    public string Label { get; }
    public bool Danger { get; }
    public bool Enabled { get; }
    public ICommand Command { get; }
}

public sealed class FixEntryVm
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Warning { get; init; }
    public FixActionVm? Action { get; init; }
}

/// <summary>Карточка одного из 13 пунктов.</summary>
public sealed class FixItemVm : ObservableObject
{
    private readonly FixModuleViewModel _owner;
    private FixStatus _status;
    private string _statusText = "";
    private string? _reason;
    private string? _warning;
    private bool _canFix;
    private string _fixLabel = "";
    private bool _needsAdmin;
    private bool _needsRestart;
    private string? _technical;
    private IReadOnlyList<string> _steps = [];
    private IReadOnlyList<FixEntryVm> _entries = [];
    private IReadOnlyList<FixActionVm> _actions = [];
    private bool _canRevert;
    private bool _showSteps;
    private bool _stepsInitialized;
    private string _title = "";
    private string _explanation = "";

    public FixItemVm(FixItem item, FixModuleViewModel owner)
    {
        Item = item;
        _owner = owner;
        FixCommand = new AsyncCommand(() => _owner.FixAsync(this), () => _canFix && !_owner.IsBusy);
        RevertCommand = new AsyncCommand(() => _owner.RevertAsync(this), () => _canRevert && !_owner.IsBusy);
        ToggleStepsCommand = new RelayCommand(() => ShowSteps = !ShowSteps);
    }

    public FixItem Item { get; }
    public FixState? State { get; private set; }
    public int Number => Item.Number;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Explanation { get => _explanation; private set => Set(ref _explanation, value); }

    public FixStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusKey));
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    /// <summary>ok | improve | na — для стилей карточки.</summary>
    public string StatusKey => _status switch
    {
        FixStatus.Ok => "ok",
        FixStatus.Improvable => "improve",
        _ => "na",
    };

    public string StatusLabel => _status switch
    {
        FixStatus.Ok => Loc.Get("wg.status.ok"),
        FixStatus.Improvable => Loc.Get("wg.status.improve"),
        _ => Loc.Get("wg.status.na"),
    };

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string? Reason { get => _reason; private set => Set(ref _reason, value); }
    public string? Warning { get => _warning; private set => Set(ref _warning, value); }
    public bool CanFix { get => _canFix; private set => Set(ref _canFix, value); }
    public string FixLabel { get => _fixLabel; private set => Set(ref _fixLabel, value); }
    public bool NeedsAdmin { get => _needsAdmin; private set => Set(ref _needsAdmin, value); }
    public bool NeedsRestart { get => _needsRestart; private set => Set(ref _needsRestart, value); }
    public string? Technical { get => _technical; private set => Set(ref _technical, value); }
    public IReadOnlyList<string> Steps { get => _steps; private set { if (Set(ref _steps, value)) OnPropertyChanged(nameof(HasSteps)); } }
    public bool HasSteps => _steps.Count > 0;
    public IReadOnlyList<FixEntryVm> Entries { get => _entries; private set { if (Set(ref _entries, value)) OnPropertyChanged(nameof(HasEntries)); } }
    public bool HasEntries => _entries.Count > 0;
    public IReadOnlyList<FixActionVm> Actions { get => _actions; private set => Set(ref _actions, value); }
    public bool CanRevert { get => _canRevert; private set => Set(ref _canRevert, value); }
    public bool ShowSteps { get => _showSteps; set => Set(ref _showSteps, value); }

    private string? _sectionHeader;
    /// <summary>Заголовок раздела над карточкой — только у первого пункта раздела.</summary>
    public string? SectionHeader { get => _sectionHeader; set => Set(ref _sectionHeader, value); }

    public ICommand FixCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ToggleStepsCommand { get; }

    public void Apply(FixState s, bool canRevert)
    {
        State = s;
        Title = Item.Title;
        Explanation = Item.Explanation;
        Status = s.Status;
        OnPropertyChanged(nameof(StatusLabel));
        StatusText = s.StatusText;
        Reason = s.Reason;
        Warning = s.Warning;
        CanFix = s.CanFix;
        FixLabel = s.FixLabel ?? Loc.Get("wg.fix");
        NeedsAdmin = s.NeedsAdmin;
        NeedsRestart = s.NeedsRestart;
        Technical = s.Technical;
        Steps = s.Steps.Select((x, i) => $"{i + 1}. {x}").ToList();
        if (!_stepsInitialized)
        {
            _stepsInitialized = true;
            ShowSteps = s.Steps.Count > 0 && s.Status == FixStatus.Improvable;
        }
        Entries = s.Entries.Select(e => new FixEntryVm
        {
            Title = e.Title,
            Detail = e.Detail,
            Warning = e.Warning,
            Action = e.Action is null ? null : MakeAction(e.Action),
        }).ToList();
        Actions = s.Actions.Select(MakeAction).ToList();
        CanRevert = canRevert;
    }

    private FixActionVm MakeAction(FixAction a) =>
        new(a.Label, a.Danger, a.Enabled, new AsyncCommand(() => _owner.RunActionAsync(this, a.Id), () => a.Enabled && !_owner.IsBusy));
}

/// <summary>
/// Страница модуля с пунктами «проверить → исправить → вернуть»: «Игры в окне» и «Оптимизация Windows».
/// </summary>
public sealed class FixModuleViewModel : PageViewModel, IFixHost
{
    private readonly string _key;
    private readonly string _titleKey;
    private readonly string _subtitleKey;
    private readonly string _summaryKey;
    private readonly FixModule _module;
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyDictionary<int, double>?> _gpuByPid;
    private FixContext? _ctx;
    private string _gameExe;
    private bool _isBusy;
    private string _busyText = "";
    private string _summary = "";
    private double _progress;
    private string _systemText = "";
    private string? _gameWarning;
    private string _backupText = "";
    private bool _hasBackup;
    private bool _hasRecommended;

    public FixModuleViewModel(string key, FixModule module, string titleKey, string subtitleKey, string summaryKey, bool usesGame,
        AppSettings settings, Func<IReadOnlyDictionary<int, double>?> gpuByPid)
    {
        _key = key;
        _titleKey = titleKey;
        _subtitleKey = subtitleKey;
        _summaryKey = summaryKey;
        UsesGame = usesGame;
        _settings = settings;
        _gpuByPid = gpuByPid;
        _gameExe = usesGame ? settings.GameExe ?? "" : "";
        _module = module;
        foreach (var item in _module.Items) Items.Add(new FixItemVm(item, this));

        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        PickRunningCommand = new AsyncCommand(PickRunningAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        FixAllCommand = new AsyncCommand(FixAllAsync, () => !IsBusy && _hasRecommended);
        RevertAllCommand = new AsyncCommand(RevertAllAsync, () => !IsBusy && _hasBackup);
        OpenBackupCommand = new RelayCommand(OpenBackupFolder);

        Lang.Changed += () =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            if (_ctx is not null) _ = RefreshAsync();
        };
    }

    public override string Key => _key;
    public bool UsesGame { get; }
    /// <summary>Страница только диагностирует: прячем «Исправить всё», «Вернуть всё» и строку о резервной копии.</summary>
    public bool CanChange => !_module.IsDiagnosticOnly;
    public string Title => Loc.Get(_titleKey);
    public string Subtitle => Loc.Get(_subtitleKey);
    public ObservableCollection<FixItemVm> Items { get; } = new();
    public ICommand BrowseCommand { get; }
    public ICommand PickRunningCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand FixAllCommand { get; }
    public ICommand RevertAllCommand { get; }
    public ICommand OpenBackupCommand { get; }

    public string GameExe
    {
        get => _gameExe;
        set
        {
            if (!Set(ref _gameExe, value?.Trim().Trim('"') ?? "")) return;
            if (!UsesGame) return;
            _settings.GameExe = _gameExe.Length == 0 ? null : _gameExe;
            _settings.Save();
            _ = RefreshAsync();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string SystemText { get => _systemText; private set => Set(ref _systemText, value); }
    public string? GameWarning { get => _gameWarning; private set => Set(ref _gameWarning, value); }
    public string BackupText { get => _backupText; private set => Set(ref _backupText, value); }
    public bool HasBackup { get => _hasBackup; private set => Set(ref _hasBackup, value); }

    public override void OnShown()
    {
        if (_ctx is null && !IsBusy) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = Lang.T("Проверяю систему…", "Checking the system…");
        try
        {
            string? exe = _gameExe.Length == 0 ? null : _gameExe;
            var gpu = _gpuByPid();
            var (ctx, states) = await Task.Run(() =>
            {
                var c = FixContext.Build(exe, gpu, _module.Backup, IsMarked, _module.NeedsHardware);
                var s = _module.Items.Select(i =>
                {
                    try
                    {
                        return i.Evaluate(c);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("evaluate " + i.Id, ex);
                        return FixState.Unavailable(Lang.T("Ошибка проверки: ", "Check failed: ") + ex.Message);
                    }
                }).ToList();
                return (c, s);
            });
            _ctx = ctx;

            string? previousSection = null;
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Apply(states[i], _module.Backup.ForItem(Items[i].Item.Id).Count > 0);
                string? section = Items[i].Item.Section;
                Items[i].SectionHeader = section != previousSection ? section : null;
                previousSection = section;
            }

            int available = states.Count(s => s.Status != FixStatus.NotAvailable);
            int ok = states.Count(s => s.Status == FixStatus.Ok);
            Summary = string.Format(Loc.Get(_summaryKey), ok, available);
            Progress = available == 0 ? 0 : (double)ok / available;
            _hasRecommended = states.Any(s => s.Status == FixStatus.Improvable && s.Recommended && s.CanFix);

            int monitors = ctx.Monitors.Count;
            string monitorText = Lang.T(
                monitors == 1 ? "1 монитор" : $"{monitors} {Core.Formatting.Ru.Plural(monitors, "монитор", "монитора", "мониторов")}",
                monitors == 1 ? "1 monitor" : $"{monitors} monitors");
            string rights = ctx.IsElevated ? Loc.Get("shell.admin") : Loc.Get("shell.user");
            SystemText = $"{ctx.Os.Display} · {Lang.T("сборка", "build")} {ctx.Os.BuildText} · {ctx.GpuText} · {monitorText} · {rights}";

            GameWarning = exe is not null && !File.Exists(exe)
                ? Lang.T("Файл не найден. Проверьте путь — настройки для exe привязываются к точному пути.",
                    "File not found. Check the path — per-game settings are tied to the exact path.")
                : null;

            int backups = _module.Backup.Entries.Count;
            HasBackup = backups > 0;
            BackupText = backups == 0
                ? Lang.T("Резервная копия пуста — WinPulse ещё ничего не менял.", "The backup is empty — WinPulse hasn't changed anything yet.")
                : Lang.T($"В резервной копии: {backups} {Core.Formatting.Ru.Plural(backups, "значение", "значения", "значений")}.",
                    $"Backup holds {backups} value(s).");
        }
        catch (Exception ex)
        {
            Log.Error("windowed-refresh", ex);
            Summary = Lang.T("Не удалось проверить систему: ", "Could not check the system: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsMarked(string key)
    {
        lock (_settings.Marks) return _settings.Marks.Contains(key);
    }

    private async Task<FixContext> ContextAsync()
    {
        if (_ctx is null) await RefreshAsync();
        return _ctx ?? throw new InvalidOperationException(Lang.T("Нет данных о системе", "No system data"));
    }

    public async Task FixAsync(FixItemVm item)
    {
        var ctx = await ContextAsync();
        var plan = item.Item.PlanFix(ctx);
        if (plan.Count == 0)
        {
            Notify(item.Title, Lang.T("Менять нечего — всё уже настроено.", "Nothing to change — already set."));
            await RefreshAsync();
            return;
        }
        await ApplyPlanAsync(item.Title, plan);
    }

    public async Task RevertAsync(FixItemVm item)
    {
        var plan = _module.PlanRevert(item.Item.Id);
        if (plan.Count == 0)
        {
            Notify(item.Title, Lang.T("В резервной копии нет изменений этого пункта.", "The backup has no changes for this item."));
            return;
        }
        await ApplyPlanAsync(Loc.Get("wg.revert") + ": " + item.Title, plan);
    }

    private async Task FixAllAsync()
    {
        var ctx = await ContextAsync();
        var plan = new List<Change>();
        foreach (var vm in Items)
        {
            var s = vm.State;
            if (s is null || s.Status != FixStatus.Improvable || !s.Recommended || !s.CanFix) continue;
            plan.AddRange(vm.Item.PlanFix(ctx));
        }
        if (plan.Count == 0)
        {
            Notify(Loc.Get("wg.fixall"), Lang.T("Все рекомендуемые пункты уже настроены.", "All recommended items are already set."));
            return;
        }
        await ApplyPlanAsync(Loc.Get("wg.fixall"), plan);
    }

    private async Task RevertAllAsync()
    {
        var plan = _module.PlanRevert(null);
        if (plan.Count == 0)
        {
            Notify(Loc.Get("wg.revertall"), Lang.T("Резервная копия пуста.", "The backup is empty."));
            return;
        }
        await ApplyPlanAsync(Loc.Get("wg.revertall"), plan);
    }

    public async Task RunActionAsync(FixItemVm item, string actionId)
    {
        var ctx = await ContextAsync();
        var outcome = await item.Item.RunActionAsync(actionId, ctx, this);
        if (outcome.Message is not null) Notify(item.Title, outcome.Message);
        if (outcome.Plan is { Count: > 0 }) await ApplyPlanAsync(item.Title, outcome.Plan);
        else if (outcome.Refresh) await RefreshAsync();
    }

    private async Task ApplyPlanAsync(string title, List<Change> plan)
    {
        if (!ChangesDialog.Confirm(title, plan)) return;
        IsBusy = true;
        BusyText = Lang.T("Применяю изменения…", "Applying changes…");
        List<ChangeResult> results;
        try
        {
            results = await _module.Executor.ApplyAsync(plan);
        }
        finally
        {
            IsBusy = false;
        }
        foreach (var r in results)
            Log.Info($"windowed: {(r.Success ? "OK" : "FAIL")} {r.Change?.TargetId} {r.Change?.Before} -> {r.Change?.After} {r.Error}");
        ResultDialog.Show(title, results);
        await RefreshAsync();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Lang.T("Выберите exe игры", "Select the game executable"),
            Filter = Lang.T("Программы (*.exe)|*.exe", "Programs (*.exe)|*.exe"),
            CheckFileExists = true,
        };
        if (_gameExe.Length > 0)
        {
            try
            {
                var dir = Path.GetDirectoryName(_gameExe);
                if (dir is not null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            catch
            {
            }
        }
        if (dialog.ShowDialog() == true) GameExe = dialog.FileName;
    }

    private async Task PickRunningAsync()
    {
        var path = await ProcessPickerDialog.PickAsync();
        if (path is not null) GameExe = path;
    }

    private void OpenBackupFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(_module.Backup.FilePath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open-backup", ex);
        }
    }

    // IFixHost
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false) =>
        MessageDialog.ConfirmAsync(title, message, confirmText, danger);

    public Task<bool> KeepChangesAsync(string title, string message, int seconds) =>
        KeepDialog.ShowAsync(title, message, seconds);

    public void Notify(string title, string message) => MessageDialog.Show(title, message);

    public bool GetMark(string key) => IsMarked(key);

    public void SetMark(string key, bool value)
    {
        lock (_settings.Marks)
        {
            if (value) _settings.Marks.Add(key);
            else _settings.Marks.Remove(key);
        }
        _settings.Save();
    }
}
