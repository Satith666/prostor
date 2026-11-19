using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinPulse.App.Infrastructure;
using WinPulse.Core;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.App.Dialogs;

/// <summary>Общие детали всех диалогов: тёмное окно, текст, кнопки.</summary>
internal static class Kit
{
    public static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Style? StyleOf(string key) => Application.Current?.TryFindResource(key) as Style;

    public static Window CreateWindow(string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Res("Bg"),
            Foreground = Res("Text"),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            UseLayoutRounding = true,
        };
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, w) && owner.IsLoaded)
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        DarkTitleBar.Apply(w);
        return w;
    }

    public static TextBlock Text(string text, string? style = null, Thickness? margin = null, Brush? foreground = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style is not null && StyleOf(style) is { } s) t.Style = s;
        if (margin is { } m) t.Margin = m;
        if (foreground is not null) t.Foreground = foreground;
        return t;
    }

    public static Button Button(string text, string style, RoutedEventHandler onClick, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 96,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (StyleOf(style) is { } s) b.Style = s;
        b.Click += onClick;
        return b;
    }

    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    public static Border Card(UIElement child, Brush? background = null) => new()
    {
        Background = background ?? Res("Panel"),
        BorderBrush = Res("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child,
    };

    public static Border Badge(string text, Brush foreground, Brush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11 },
    };

    public static ScrollViewer Scroll(UIElement content, double maxHeight) => new()
    {
        Content = content,
        MaxHeight = maxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static void Host(Window w, UIElement content) =>
        w.Content = new Border { Padding = new Thickness(22, 18, 22, 18), Child = content };
}

public static class MessageDialog
{
    public static void Show(string title, string message)
    {
        var w = Kit.CreateWindow(title, 460);
        var root = new StackPanel();
        root.Children.Add(Kit.Text(title, "H2", new Thickness(0, 0, 0, 8)));
        root.Children.Add(Kit.Text(message, "MutedText"));
        root.Children.Add(Kit.ButtonRow(Kit.Button(Loc.Get("common.ok"), "BtnPrimary", (_, _) => w.Close(), isDefault: true, isCancel: true)));
        Kit.Host(w, root);
        w.ShowDialog();
    }

    public static Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        var w = Kit.CreateWindow(title, 480);
        var root = new StackPanel();
        root.Children.Add(Kit.Text(title, "H2", new Thickness(0, 0, 0, 8)));
        root.Children.Add(Kit.Text(message, "MutedText"));
        root.Children.Add(Kit.ButtonRow(
            Kit.Button(Loc.Get("common.cancel"), "Btn", (_, _) => w.DialogResult = false, isCancel: true),
            Kit.Button(confirmText, danger ? "BtnDanger" : "BtnPrimary", (_, _) => w.DialogResult = true, isDefault: !danger)));
        Kit.Host(w, root);
        return Task.FromResult(w.ShowDialog() == true);
    }
}

/// <summary>«Что именно меняется и зачем» — перед любым изменением.</summary>
public static class ChangesDialog
{
    public static bool Confirm(string title, IReadOnlyList<Change> plan)
    {
        var w = Kit.CreateWindow(Loc.Get("dlg.confirm.title"), 640);
        var root = new StackPanel();
        root.Children.Add(Kit.Text(title, "H2", new Thickness(0, 0, 0, 6)));
        root.Children.Add(Kit.Text(Loc.Get("dlg.confirm.lead"), "MutedText", new Thickness(0, 0, 0, 12)));

        var list = new StackPanel();
        foreach (var c in plan)
        {
            var card = new StackPanel();
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0), TextWrapping = TextWrapping.Wrap });
            if (c.NeedsAdmin) head.Children.Add(Kit.Badge(Loc.Get("common.admin"), Kit.Res("Warn"), Kit.Res("WarnBg")));
            if (c.NeedsRestart) head.Children.Add(Kit.Badge(Loc.Get("common.restart"), Kit.Res("Yellow"), Kit.Res("WarnBg")));
            card.Children.Add(head);
            card.Children.Add(Kit.Text($"{Loc.Get("common.before")}: {c.Before}   →   {Loc.Get("common.after")}: {c.After}", null, new Thickness(0, 0, 0, 2)));
            if (c.Why.Length > 0) card.Children.Add(Kit.Text($"{Loc.Get("common.why")}: {c.Why}", "MutedText"));
            if (UiState.I.ProMode && c.Location.Length > 0)
                card.Children.Add(Kit.Text($"{Loc.Get("common.where")}: {c.Location}", "Mono", new Thickness(0, 4, 0, 0)));
            list.Children.Add(Kit.Card(card));
        }
        root.Children.Add(Kit.Scroll(list, 420));

        if (plan.Any(c => c.NeedsAdmin))
            root.Children.Add(Kit.Text(Loc.Get("dlg.confirm.admin"), "FaintText", new Thickness(0, 8, 0, 0)));

        root.Children.Add(Kit.ButtonRow(
            Kit.Button(Loc.Get("common.cancel"), "Btn", (_, _) => w.DialogResult = false, isCancel: true),
            Kit.Button(Loc.Get("dlg.confirm.apply"), "BtnPrimary", (_, _) => w.DialogResult = true, isDefault: true)));
        Kit.Host(w, root);
        return w.ShowDialog() == true;
    }
}

/// <summary>Итог: что сделано, что не получилось и почему, где нужна перезагрузка.</summary>
public static class ResultDialog
{
    public static void Show(string title, IReadOnlyList<ChangeResult> results)
    {
        var w = Kit.CreateWindow(Loc.Get("dlg.result.title"), 620);
        var root = new StackPanel();
        int ok = results.Count(r => r.Success);
        int failed = results.Count - ok;
        root.Children.Add(Kit.Text(title, "H2", new Thickness(0, 0, 0, 6)));
        root.Children.Add(Kit.Text(
            failed == 0
                ? Lang.T($"Готово: применено {ok} из {results.Count}.", $"Done: {ok} of {results.Count} applied.")
                : Lang.T($"Применено {ok} из {results.Count}, не получилось: {failed}.", $"Applied {ok} of {results.Count}, failed: {failed}."),
            "MutedText", new Thickness(0, 0, 0, 12)));

        var list = new StackPanel();
        foreach (var r in results)
        {
            var c = r.Change;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var mark = new TextBlock
            {
                Text = r.Success ? "✓" : "✗",
                Foreground = r.Success ? Kit.Res("Good") : Kit.Res("Bad"),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 10, 0),
            };
            row.Children.Add(mark);
            var body = new StackPanel();
            Grid.SetColumn(body, 1);
            body.Children.Add(new TextBlock { Text = c?.Title ?? "", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (r.Success)
                body.Children.Add(Kit.Text($"{c?.Before} → {c?.After}", "MutedText"));
            else
                body.Children.Add(Kit.Text(Lang.T("Не получилось: ", "Failed: ") + (r.Error ?? Lang.T("причина неизвестна", "unknown reason")),
                    null, null, Kit.Res("Bad")));
            row.Children.Add(body);
            list.Children.Add(Kit.Card(row));
        }
        root.Children.Add(Kit.Scroll(list, 420));

        bool restart = results.Any(r => r.Success && r.Change is { NeedsRestart: true });
        var buttons = new List<Button>();
        if (restart)
        {
            root.Children.Add(Kit.Text(Lang.T("Часть изменений заработает после перезагрузки компьютера.",
                "Some changes take effect after a restart."), null, new Thickness(0, 8, 0, 0), Kit.Res("Yellow")));
            buttons.Add(Kit.Button(Loc.Get("dlg.result.restart"), "Btn", (_, _) =>
            {
                if (!MessageDialog.ConfirmAsync(Lang.T("Перезагрузить компьютер?", "Restart the computer?"),
                        Lang.T("Сохраните открытые документы. Перезагрузка начнётся через 10 секунд.",
                            "Save your open documents. The restart begins in 10 seconds."),
                        Lang.T("Перезагрузить", "Restart")).Result) return;
                try
                {
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10") { UseShellExecute = false, CreateNoWindow = true });
                    w.Close();
                }
                catch (Exception ex)
                {
                    Log.Error("restart", ex);
                }
            }));
        }
        buttons.Add(Kit.Button(Loc.Get("common.ok"), "BtnPrimary", (_, _) => w.Close(), isDefault: true, isCancel: true));
        root.Children.Add(Kit.ButtonRow(buttons.ToArray()));
        Kit.Host(w, root);
        w.ShowDialog();
    }
}

/// <summary>«Оставить изменения?» с обратным отсчётом — если ответа нет, изменение отменяется.</summary>
public static class KeepDialog
{
    public static Task<bool> ShowAsync(string title, string message, int seconds)
    {
        var w = Kit.CreateWindow(title, 500);
        w.Topmost = true;
        int left = seconds;
        var countdown = Kit.Text("", null, new Thickness(0, 12, 0, 0), Kit.Res("Yellow"));
        void UpdateText() => countdown.Text = Lang.T($"Без ответа всё вернётся через {left} с.", $"Reverting automatically in {left} s.");
        UpdateText();

        var root = new StackPanel();
        root.Children.Add(Kit.Text(title, "H2", new Thickness(0, 0, 0, 8)));
        root.Children.Add(Kit.Text(message, "MutedText"));
        root.Children.Add(countdown);
        root.Children.Add(Kit.ButtonRow(
            Kit.Button(Loc.Get("dlg.keep.revert"), "Btn", (_, _) => w.DialogResult = false, isCancel: true),
            Kit.Button(Loc.Get("dlg.keep.keep"), "BtnPrimary", (_, _) => w.DialogResult = true, isDefault: true)));
        Kit.Host(w, root);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            if (left <= 0)
            {
                timer.Stop();
                if (w.IsLoaded) w.DialogResult = false;
                return;
            }
            UpdateText();
        };
        w.Loaded += (_, _) => timer.Start();
        w.Closed += (_, _) => timer.Stop();
        return Task.FromResult(w.ShowDialog() == true);
    }
}

/// <summary>Выбор игры из запущенных программ.</summary>
public static class ProcessPickerDialog
{
    public static async Task<string?> PickAsync()
    {
        var apps = await Task.Run(() => RunningApps.ListWithWindows());
        var w = Kit.CreateWindow(Loc.Get("wg.picker.title"), 620);
        var root = new StackPanel();
        root.Children.Add(Kit.Text(Loc.Get("wg.picker.title"), "H2", new Thickness(0, 0, 0, 6)));
        root.Children.Add(Kit.Text(Loc.Get("wg.picker.hint"), "MutedText", new Thickness(0, 0, 0, 12)));

        var listBox = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 420,
            ItemContainerStyle = Kit.StyleOf("PickerItem"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        foreach (var app in apps)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 2, 10, 0), Source = IconCache.Get(app.Path), VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(icon);
            var texts = new StackPanel();
            Grid.SetColumn(texts, 1);
            string title = string.IsNullOrWhiteSpace(app.WindowTitle) ? app.Name : $"{app.Name} — {app.WindowTitle}";
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            texts.Children.Add(new TextBlock { Text = app.Path, Foreground = Kit.Res("Faint"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(texts);
            listBox.Items.Add(new ListBoxItem { Content = grid, Tag = app.Path });
        }
        if (apps.Count == 0)
            root.Children.Add(Kit.Text(Lang.T("Программ с окнами не найдено. Запустите игру и откройте список снова.",
                "No programs with windows found. Start the game and open the list again."), "FaintText"));
        else
            root.Children.Add(listBox);

        string? result = null;
        var select = Kit.Button(Loc.Get("common.select"), "BtnPrimary", (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is string path)
            {
                result = path;
                w.DialogResult = true;
            }
        }, isDefault: true);
        listBox.MouseDoubleClick += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is string path)
            {
                result = path;
                w.DialogResult = true;
            }
        };
        root.Children.Add(Kit.ButtonRow(Kit.Button(Loc.Get("common.cancel"), "Btn", (_, _) => w.DialogResult = false, isCancel: true), select));
        Kit.Host(w, root);
        return w.ShowDialog() == true ? result : null;
    }
}
