using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WinPulse.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>Асинхронная команда: пока выполняется — недоступна, ошибки пишет в журнал и показывает.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Log.Error("command", ex);
            Dialogs.MessageDialog.Show(Core.Lang.T("Ошибка", "Error"), ex.Message);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class CollectionSync
{
    /// <summary>
    /// Приводит коллекцию к новому списку, сохраняя существующие объекты (и их раскрытие, выделение):
    /// лишние удаляет, новые вставляет, порядок меняет через Move.
    /// </summary>
    public static void Sync<TItem, TSource>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> source,
        Func<TSource, string> sourceKey,
        Func<TItem, string> itemKey,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update)
    {
        var keys = new HashSet<string>(source.Select(sourceKey));
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(itemKey(target[i]))) target.RemoveAt(i);

        var map = new Dictionary<string, TItem>();
        foreach (var item in target) map[itemKey(item)] = item;

        for (int i = 0; i < source.Count; i++)
        {
            string key = sourceKey(source[i]);
            if (map.TryGetValue(key, out var existing))
            {
                update(existing, source[i]);
                int current = target.IndexOf(existing);
                if (current != i && current >= 0) target.Move(current, i);
            }
            else
            {
                var created = create(source[i]);
                update(created, source[i]);
                target.Insert(Math.Min(i, target.Count), created);
                map[key] = created;
            }
        }
    }
}

/// <summary>Общее состояние интерфейса, доступное из любого XAML: {Binding ProMode, Source={x:Static i:UiState.I}}.</summary>
public sealed class UiState : ObservableObject
{
    public static UiState I { get; } = new();

    private bool _proMode;

    public bool ProMode
    {
        get => _proMode;
        set => Set(ref _proMode, value);
    }
}
