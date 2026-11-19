using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using WinPulse.App.Dialogs;
using WinPulse.App.Infrastructure;
using WinPulse.Core;
using WinPulse.Core.Formatting;
using WinPulse.Core.Startup;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Startup;

namespace WinPulse.App.ViewModels;

/// <summary>Строка автозагрузки.</summary>
public sealed class StartupRowVm : ObservableObject
{
    public StartupRowVm(StartupEntry entry, long? memory, StartupViewModel owner)
    {
        Entry = entry;
        var tip = StartupKnowledge.Find(entry.Name + " " + (entry.Description ?? ""), entry.ExePath);
        Advice = tip.Advice;
        AdviceKey = tip.Advice switch
        {
            StartupAdvice.CanDisable => "disable",
            StartupAdvice.Keep => "keep",
            _ => "optional",
        };
        AdviceText = StartupKnowledge.AdviceText(tip.Advice);
        AdviceTip = tip.Text;
        Memory = memory;
        MemoryText = memory is long m ? "сейчас " + Ru.Bytes(m) : entry.Enabled ? "сейчас не запущена" : "";
        Icon = IconCache.Get(entry.ExePath);
        ToggleCommand = new AsyncCommand(() => owner.ToggleAsync(this), () => !owner.IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => entry.ExePath is not null && File.Exists(entry.ExePath));
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.DisplayName;
    public string? Publisher => Entry.Publisher;
    public string Source => Entry.SourceText + (Entry.TriggerText is null ? "" : " · " + Entry.TriggerText);
    public string Command => Entry.Command;
    public bool Enabled => Entry.Enabled;
    public string StatusText => Entry.Enabled ? "Включена" : "Отключена";
    public string ToggleLabel => Entry.Enabled ? "Отключить" : "Включить";
    public bool NeedsAdmin => Entry.NeedsAdmin;
    public StartupAdvice Advice { get; }
    /// <summary>disable | optional | keep</summary>
    public string AdviceKey { get; }
    public string AdviceText { get; }
    public string AdviceTip { get; }
    public long? Memory { get; }
    public string MemoryText { get; }
    public ImageSource? Icon { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenFolderCommand { get; }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Entry.ExePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("startup-open", ex);
        }
    }
}

/// <summary>Раздел «Автозагрузка».</summary>
public sealed class StartupViewModel : PageViewModel
{
    private readonly StartupManager _manager = new();
    private readonly Func<MonitorFrame?> _frame;
    private bool _isBusy;
    private bool _scanned;
    private string _summary = "Список ещё не загружен.";
    private string _memoryText = "";
    private string _busyText = "";
    private string _backupText = "";
    private bool _hasBackup;
    private int _recommendedCount;

    public StartupViewModel(Func<MonitorFrame?> frame)
    {
        _frame = frame;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        RevertAllCommand = new AsyncCommand(RevertAllAsync, () => !IsBusy && _hasBackup);
        DisableRecommendedCommand = new AsyncCommand(DisableRecommendedAsync, () => !IsBusy && _recommendedCount > 0);
        OpenTaskManagerCommand = new RelayCommand(() => Start("taskmgr.exe", "/0 /startup"));
        OpenTaskSchedulerCommand = new RelayCommand(() => Start("taskschd.msc", null));
    }

    public override string Key => "startup";
    public ObservableCollection<StartupRowVm> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand RevertAllCommand { get; }
    public ICommand DisableRecommendedCommand { get; }
    public ICommand OpenTaskManagerCommand { get; }
    public ICommand OpenTaskSchedulerCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string BackupText { get => _backupText; private set => Set(ref _backupText, value); }
    public bool HasBackup { get => _hasBackup; private set => Set(ref _hasBackup, value); }

    public string DisableRecommendedText => _recommendedCount > 0
        ? $"Отключить то, что можно ({_recommendedCount})"
        : "Отключать нечего";

    public override void OnShown()
    {
        if (!_scanned && !IsBusy) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Читаю автозагрузку…";
        try
        {
            var entries = await Task.Run(() => _manager.Scan());
            _scanned = true;

            // Сколько памяти программы занимают прямо сейчас (по данным монитора).
            var memoryByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var frame = _frame();
            if (frame is not null)
                foreach (var app in frame.Apps)
                    if (app.Path is not null) memoryByPath[app.Path] = app.PrivateMemory;

            var rows = entries
                .Select(e => new StartupRowVm(e, e.ExePath is not null && memoryByPath.TryGetValue(e.ExePath, out var m) ? m : null, this))
                .OrderByDescending(r => r.Enabled)
                .ThenBy(r => r.Advice switch { StartupAdvice.CanDisable => 0, StartupAdvice.Optional => 1, _ => 2 })
                .ThenByDescending(r => r.Memory ?? 0)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Items.Clear();
            foreach (var r in rows) Items.Add(r);

            int enabled = rows.Count(r => r.Enabled);
            int disabled = rows.Count - enabled;
            Summary = $"Запускаются вместе с Windows: {enabled}" + (disabled > 0 ? $" · отключено: {disabled}" : "");
            long memory = rows.Where(r => r.Enabled).Sum(r => r.Memory ?? 0);
            MemoryText = memory > 0
                ? $"Включённые программы автозагрузки сейчас занимают {Ru.Bytes(memory)} оперативной памяти."
                : "";
            _recommendedCount = rows.Count(r => r.Enabled && r.Advice == StartupAdvice.CanDisable);
            OnPropertyChanged(nameof(DisableRecommendedText));
            UpdateBackup();
        }
        catch (Exception ex)
        {
            Log.Error("startup-refresh", ex);
            Summary = "Не удалось прочитать автозагрузку: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateBackup()
    {
        int n = _manager.Module.Backup.Entries.Count;
        HasBackup = n > 0;
        BackupText = n == 0
            ? "WinPulse ещё ничего не менял в автозагрузке."
            : $"Изменено WinPulse: {n}. «Вернуть всё как было» восстановит исходное состояние.";
    }

    public async Task ToggleAsync(StartupRowVm row)
    {
        var change = StartupManager.PlanToggle(row.Entry, !row.Entry.Enabled);
        await ApplyAsync(row.ToggleLabel + ": " + row.Name, new List<Change> { change });
    }

    private async Task DisableRecommendedAsync()
    {
        var plan = Items.Where(r => r.Enabled && r.Advice == StartupAdvice.CanDisable)
            .Select(r => StartupManager.PlanToggle(r.Entry, enable: false))
            .ToList();
        if (plan.Count == 0) return;
        await ApplyAsync("Отключить автозапуск программ, которые легко запустить вручную", plan);
    }

    private async Task RevertAllAsync()
    {
        var plan = _manager.Module.PlanRevert(null);
        if (plan.Count == 0) return;
        await ApplyAsync("Вернуть автозагрузку как было", plan);
    }

    private async Task ApplyAsync(string title, List<Change> plan)
    {
        if (!ChangesDialog.Confirm(title, plan)) return;
        IsBusy = true;
        BusyText = "Применяю изменения…";
        List<ChangeResult> results;
        try
        {
            results = await _manager.Module.Executor.ApplyAsync(plan);
        }
        finally
        {
            IsBusy = false;
        }
        foreach (var r in results)
            Log.Info($"startup: {(r.Success ? "OK" : "FAIL")} {r.Change?.TargetId} {r.Change?.Before} -> {r.Change?.After} {r.Error}");
        ResultDialog.Show(title, results);
        await RefreshAsync();
    }

    private static void Start(string file, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("startup-tool", ex);
        }
    }
}
