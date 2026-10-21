using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TodoListApp.Models;
using TodoListApp.Services;
namespace TodoListApp;

public partial class MainPage : ContentPage
{
    // Material Icons glyphs
    private const string IcCheck = "\ue5ca", IcEdit = "\ue3c9", IcDelete = "\ue872", IcCalendar = "\ue935",
                         IcWarning = "\ue002", IcAlarm = "\ue855", IcSun = "\ue518", IcMoon = "\ue51c",
                         IcTaskAlt = "\ue2e6";

    private readonly TaskDatabase _database;
    private readonly Dictionary<int, Border> _cards = new();
    private bool _hasDueDate = true;
    private bool _suppressDateEvent;
    private bool _isSaving;
    private bool _firstLoad = true;
    private bool _statusAdjusted;
    private TaskItem? _editingTask;
    private string _currentFilter = "All";
    private string _currentDateFilter = "All";
    private string _searchText = string.Empty;

    private static readonly string[] Priorities = { "Low", "Medium", "High" };
    private static readonly Color Active = Color.FromArgb("#16834A");

    private bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;
    private Color Inactive => Color.FromArgb(IsDark ? "#26322B" : "#E6ECE8");
    private Color InactiveText => Color.FromArgb(IsDark ? "#B7C4BC" : "#435148");

    public MainPage(TaskDatabase database)
    {
        // restore saved theme before the UI is built
        string saved = Preferences.Default.Get("theme", "");
        if (Application.Current != null)
            Application.Current.UserAppTheme = saved == "dark" ? AppTheme.Dark
                                             : saved == "light" ? AppTheme.Light
                                             : AppTheme.Unspecified;

        InitializeComponent();
        _database = database;

        ApplyPalette();
        if (Application.Current != null)
            Application.Current.RequestedThemeChanged += async (s, e) =>
            {
                ApplyPalette();
                await LoadTasksAsync();
            };

        NotificationService.Hook();
        NotificationService.TaskTapped += id =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                NotificationService.ConsumePendingTask();
                await FocusTaskAsync(id);
            });

        int h = DateTime.Now.Hour;
        GreetingLabel.Text = h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";

        // Hidden diagnostics: triple-tap the greeting text
        var diagTap = new TapGestureRecognizer { NumberOfTapsRequired = 3 };
        diagTap.Tapped += async (s, e) =>
            await DisplayAlertAsync("Notification check", await NotificationService.DiagnoseAsync(), "OK");
        GreetingLabel.GestureRecognizers.Add(diagTap);

        ClearDueDateButton.Clicked += ClearDueDateButton_Clicked;
        DueDatePicker.DateSelected += (s, e) =>
        {
            if (_suppressDateEvent) return;
            _hasDueDate = true;
            ClearDueDateButton.Text = "No date";
            UpdateReminderState();
        };

        Loaded += MainPage_Loaded;
        SortPicker.SelectedIndexChanged += async (s, e) => await LoadTasksAsync();
        SearchEntry.TextChanged += async (s, e) =>
        {
            _searchText = e.NewTextValue?.Trim() ?? string.Empty;
            await LoadTasksAsync();
        };
    }

    // ---------- Theme ----------
    private void ApplyPalette()
    {
        bool d = IsDark;
        Resources["PageBg"] = Color.FromArgb(d ? "#0D1310" : "#F4F7F5");
        Resources["CardBg"] = Color.FromArgb(d ? "#17211B" : "#FFFFFF");
        Resources["FieldBg"] = Color.FromArgb(d ? "#212D26" : "#F1F5F3");
        Resources["TextMain"] = Color.FromArgb(d ? "#ECF3EE" : "#17211B");
        Resources["TextMuted"] = Color.FromArgb(d ? "#93A39A" : "#718078");
        Resources["LineColor"] = Color.FromArgb(d ? "#2A372F" : "#D5DDD8");
        Resources["PrimaryColor"] = Color.FromArgb(d ? "#1FA05C" : "#16834A");

        ThemeIcon.Text = d ? IcSun : IcMoon;
        UpdateFilterButtons();
        UpdateDateFilterButtons();
    }

    private async void OnThemeToggleTapped(object? sender, TappedEventArgs e)
    {
        bool toDark = !IsDark;
        if (Application.Current != null)
            Application.Current.UserAppTheme = toDark ? AppTheme.Dark : AppTheme.Light;
        Preferences.Default.Set("theme", toDark ? "dark" : "light");
        ApplyPalette();
        await LoadTasksAsync();
    }

    // Adds only the extra top padding needed when Android draws under the status bar
    private void AdjustForStatusBar()
    {
        if (_statusAdjusted) return;
#if ANDROID
        try
        {
            if (Handler?.PlatformView is not Android.Views.View view) return;
            var decor = Platform.CurrentActivity?.Window?.DecorView;
            if (decor == null) return;

            var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(decor);
            int topPx = insets?.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars()).Top ?? 0;

            var loc = new int[2];
            view.GetLocationOnScreen(loc);

            int extraPx = Math.Max(0, topPx - loc[1]);
            double density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1;
            HeaderBorder.Padding = new Thickness(22, 20 + extraPx / density, 22, 26);
            _statusAdjusted = true;
        }
        catch { /* keep default padding */ }
#endif
    }

    protected override bool OnBackButtonPressed()
    {
        if (FormOverlay.IsVisible)
        {
            CloseForm();
            return true;
        }
        return base.OnBackButtonPressed();
    }

    // ---------- Bottom sheet (add + edit) ----------
    private void UpdateReminderState()
    {
        ReminderSection.IsEnabled = _hasDueDate;
        ReminderSection.Opacity = _hasDueDate ? 1 : 0.4;
    }

    private void SetDueDate(DateTime? date)
    {
        _suppressDateEvent = true;
        _hasDueDate = date.HasValue;
        DueDatePicker.Date = date ?? DateTime.Today;
        _suppressDateEvent = false;
        ClearDueDateButton.Text = _hasDueDate ? "No date" : "No date ✓";
        UpdateReminderState();
    }

    private void OpenForm(TaskItem? task = null)
    {
        _editingTask = task;

        if (task == null)
        {
            TaskEntry.Text = string.Empty;
            PriorityPicker.SelectedIndex = 1;
            ReminderTimePicker.Time = TimeSpan.FromMinutes(NotificationService.DefaultMinutes);
            RepeatPicker.SelectedIndex = 0;
            SetDueDate(DateTime.Today);
        }
        else
        {
            TaskEntry.Text = task.Title;
            int idx = Array.IndexOf(Priorities, task.Priority);
            PriorityPicker.SelectedIndex = idx < 0 ? 1 : idx;
            ReminderTimePicker.Time = TimeSpan.FromMinutes(task.ReminderMinutes ?? NotificationService.DefaultMinutes);
            RepeatPicker.SelectedIndex = Math.Clamp(task.RepeatEveryHours, 0, 4);
            SetDueDate(task.DueDate);
        }

        FormTitleLabel.Text = task == null ? "New task" : "Edit task";
        AddTaskButton.Text = task == null ? "Add task" : "Save changes";
        FormOverlay.IsVisible = true;
        FabButton.IsVisible = false;
        TaskEntry.Focus();
    }

    private void CloseForm()
    {
        TaskEntry.Unfocus();
        FormOverlay.IsVisible = false;
        FabButton.IsVisible = true;
        _editingTask = null;
    }

    private void OnFabTapped(object? sender, TappedEventArgs e) => OpenForm();
    private void OnCloseFormTapped(object? sender, TappedEventArgs e) => CloseForm();

    private void ClearDueDateButton_Clicked(object? sender, EventArgs e)
    {
        _hasDueDate = !_hasDueDate; // tap again to bring the date back
        ClearDueDateButton.Text = _hasDueDate ? "No date" : "No date ✓";
        UpdateReminderState();
    }

    private async void MainPage_Loaded(object? sender, EventArgs e)
    {
        Loaded -= MainPage_Loaded;
        AdjustForStatusBar();
        try
        {
            await _database.InitializeAsync();
            await LoadTasksAsync();
            await NotificationService.InitializeAsync(await _database.GetTasksAsync());

            if (NotificationService.ConsumePendingTask() is int id)
                await FocusTaskAsync(id);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Database Error", ex.Message, "OK");
        }
    }

    // Jump to a task opened from a notification
    private async Task FocusTaskAsync(int taskId)
    {
        _currentFilter = "All";
        _currentDateFilter = "All";
        _searchText = string.Empty;
        if (!string.IsNullOrEmpty(SearchEntry.Text)) SearchEntry.Text = string.Empty;
        UpdateDateFilterButtons();
        await LoadTasksAsync();

        if (!_cards.TryGetValue(taskId, out var card)) return;

        await Task.Delay(200);
        await MainScroll.ScrollToAsync(card, ScrollToPosition.Center, true);

        card.Stroke = new SolidColorBrush(Active);
        card.StrokeThickness = 3;
        await Task.Delay(2000);
        card.StrokeThickness = 0;
    }

    private async Task LoadTasksAsync()
    {
        // Fetch first; everything after the await runs without yielding,
        // so rapid searches can never interleave and duplicate cards.
        var tasks = await _database.GetTasksAsync();

        TaskList.Children.Clear();
        _cards.Clear();

        int totalTasks = tasks.Count;
        int completedTasks = tasks.Count(t => t.IsCompleted);

        TotalTasksLabel.Text = totalTasks.ToString();
        PendingTasksLabel.Text = (totalTasks - completedTasks).ToString();
        CompletedTasksLabel.Text = completedTasks.ToString();

        double progress = totalTasks == 0 ? 0 : (double)completedTasks / totalTasks;
        TaskProgressBar.Progress = progress;
        ProgressPercentageLabel.Text = $"{progress:P0}";
        ProgressSummaryLabel.Text = $"{completedTasks} of {totalTasks} tasks completed";

        int dueToday = tasks.Count(t => !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today);
        int overdue = tasks.Count(t => !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Today);
        var parts = new List<string>();
        if (dueToday > 0) parts.Add($"{dueToday} due today");
        if (overdue > 0) parts.Add($"{overdue} overdue");
        TodaySummaryLabel.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : "You're all caught up";

        var filtered = _currentFilter switch
        {
            "Pending" => tasks.Where(t => !t.IsCompleted),
            "Completed" => tasks.Where(t => t.IsCompleted),
            _ => tasks.AsEnumerable()
        };

        filtered = _currentDateFilter switch
        {
            "Today" => filtered.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == DateTime.Today),
            "Upcoming" => filtered.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date > DateTime.Today && !t.IsCompleted),
            "Overdue" => filtered.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date < DateTime.Today && !t.IsCompleted),
            _ => filtered
        };

        filtered = filtered.Where(t => t.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

        IEnumerable<TaskItem> sorted = SortPicker.SelectedIndex switch
        {
            1 => filtered.OrderBy(t => t.IsCompleted)
                         .ThenBy(t => t.Priority == "High" ? 0 : t.Priority == "Medium" ? 1 : 2),
            2 => filtered.OrderBy(t => t.IsCompleted)
                         .ThenBy(t => t.DueDate.HasValue ? 0 : 1).ThenBy(t => t.DueDate),
            _ => filtered.OrderBy(t => t.IsCompleted) // pending first, newest first (stable)
        };

        var list = sorted.ToList();
        if (list.Count == 0)
        {
            var empty = new VerticalStackLayout { Padding = new Thickness(0, 46), Spacing = 8 };
            empty.Add(new Label
            {
                Text = IcTaskAlt,
                FontFamily = "MaterialIcons",
                FontSize = 56,
                TextColor = Color.FromArgb("#5BBF8A"),
                HorizontalOptions = LayoutOptions.Center
            });
            var t1 = new Label
            {
                Text = "No tasks here",
                FontSize = 17,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.Center
            };
            t1.SetDynamicResource(Label.TextColorProperty, "TextMain");
            var t2 = new Label { Text = "Tap New task to add one", FontSize = 13, HorizontalOptions = LayoutOptions.Center };
            t2.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            empty.Add(t1);
            empty.Add(t2);
            TaskList.Children.Add(empty);
            return;
        }

        bool animate = _firstLoad;
        _firstLoad = false;
        int i = 0;
        foreach (var task in list)
            AddTaskToUI(task, animate ? i++ : -1);
    }

    // Handles both "Add task" and "Save changes"
    private async void OnAddTaskClicked(object? sender, EventArgs e)
    {
        if (_isSaving) return; // blocks double-tap duplicates

        string title = TaskEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            await DisplayAlertAsync("Validation", "Please enter a task.", "OK");
            return;
        }

        string priority = PriorityPicker.SelectedItem?.ToString() ?? "Medium";
        DateTime? due = _hasDueDate ? DueDatePicker.Date : null;
        int? reminder = _hasDueDate
            ? (int)(ReminderTimePicker.Time?.TotalMinutes ?? NotificationService.DefaultMinutes)
            : null;
        int repeat = _hasDueDate ? Math.Max(0, RepeatPicker.SelectedIndex) : 0;

        _isSaving = true;
        AddTaskButton.IsEnabled = false;
        try
        {
            if (_editingTask == null)
            {
                var task = new TaskItem
                {
                    Title = title,
                    IsCompleted = false,
                    CreatedAt = DateTime.Now,
                    Priority = priority,
                    DueDate = due,
                    ReminderMinutes = reminder,
                    RepeatEveryHours = repeat
                };
                await _database.SaveTaskAsync(task);
                await NotificationService.SyncAsync(task);
            }
            else
            {
                var task = _editingTask;
                string oldTitle = task.Title, oldPriority = task.Priority;
                DateTime? oldDue = task.DueDate;
                int? oldReminder = task.ReminderMinutes;
                int oldRepeat = task.RepeatEveryHours;

                task.Title = title;
                task.Priority = priority;
                task.DueDate = due;
                task.ReminderMinutes = reminder;
                task.RepeatEveryHours = repeat;
                try
                {
                    await _database.SaveTaskAsync(task);
                    await NotificationService.SyncAsync(task);
                }
                catch
                {
                    task.Title = oldTitle;
                    task.Priority = oldPriority;
                    task.DueDate = oldDue;
                    task.ReminderMinutes = oldReminder;
                    task.RepeatEveryHours = oldRepeat;
                    throw;
                }
            }

            CloseForm();
            await LoadTasksAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            _isSaving = false;
            AddTaskButton.IsEnabled = true;
        }
    }

    // ---------- Card building blocks ----------
    private static Border Chip(string glyph, string text, string bg, string fg)
    {
        var row = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        row.Add(new Label
        {
            Text = glyph,
            FontFamily = "MaterialIcons",
            FontSize = 13,
            TextColor = Color.FromArgb(fg),
            VerticalOptions = LayoutOptions.Center
        });
        row.Add(new Label
        {
            Text = text,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb(fg),
            VerticalOptions = LayoutOptions.Center
        });
        return new Border
        {
            Content = row,
            BackgroundColor = Color.FromArgb(bg),
            StrokeThickness = 0,
            Padding = new Thickness(10, 5),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            VerticalOptions = LayoutOptions.Center
        };
    }

    private static Border IconButton(string glyph, string bg, string fg, double size, Func<Task> onTap)
    {
        var b = new Border
        {
            WidthRequest = size,
            HeightRequest = size,
            BackgroundColor = Color.FromArgb(bg),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = size / 2 },
            Content = new Label
            {
                Text = glyph,
                FontFamily = "MaterialIcons",
                FontSize = 18,
                TextColor = Color.FromArgb(fg),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (s, e) => await onTap();
        b.GestureRecognizers.Add(tap);
        return b;
    }

    private void AddTaskToUI(TaskItem task, int animateIndex)
    {
        bool d = IsDark;
        bool isOverdue = task.DueDate.HasValue && task.DueDate.Value.Date < DateTime.Today && !task.IsCompleted;

        string stripe = task.Priority == "High" ? "#EF4444" : task.Priority == "Low" ? "#22C55E" : "#F59E0B";
        string pBg = task.Priority == "High" ? (d ? "#3B1D1D" : "#FEE2E2") : task.Priority == "Low" ? (d ? "#14301F" : "#DCFCE7") : (d ? "#3A2F14" : "#FEF3C7");
        string pFg = task.Priority == "High" ? (d ? "#FCA5A5" : "#B91C1C") : task.Priority == "Low" ? (d ? "#86EFAC" : "#166534") : (d ? "#FCD34D" : "#92400E");
        string dBg = isOverdue ? (d ? "#3B1D1D" : "#FEE2E2") : (d ? "#26312B" : "#EEF2EF");
        string dFg = isOverdue ? (d ? "#FCA5A5" : "#B91C1C") : (d ? "#B7C4BC" : "#5B6A61");

        // round check button
        var check = new Border
        {
            WidthRequest = 34,
            HeightRequest = 34,
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(task.IsCompleted ? Active : Color.FromArgb("#9AA9A0")),
            BackgroundColor = task.IsCompleted ? Active : Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 17 },
            VerticalOptions = LayoutOptions.Start,
            Content = new Label
            {
                Text = task.IsCompleted ? IcCheck : "",
                FontFamily = "MaterialIcons",
                FontSize = 20,
                TextColor = Colors.White,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }
        };
        var checkTap = new TapGestureRecognizer();
        checkTap.Tapped += async (s, e) =>
        {
            bool previous = task.IsCompleted;
            task.IsCompleted = !task.IsCompleted;
            try
            {
                try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
                await _database.SaveTaskAsync(task);
                await NotificationService.SyncAsync(task);
                await LoadTasksAsync();
            }
            catch (Exception ex)
            {
                task.IsCompleted = previous;
                await DisplayAlertAsync("Error", "Could not update task: " + ex.Message, "OK");
            }
        };
        check.GestureRecognizers.Add(checkTap);

        var titleLabel = new Label
        {
            Text = task.Title,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : TextDecorations.None,
            LineBreakMode = LineBreakMode.WordWrap
        };
        if (task.IsCompleted) titleLabel.TextColor = Color.FromArgb("#8A978F");
        else titleLabel.SetDynamicResource(Label.TextColorProperty, "TextMain");

        var chips = new HorizontalStackLayout { Spacing = 8 };
        chips.Add(Chip(IcCheck, task.Priority.ToUpperInvariant(), pBg, pFg));
        if (task.DueDate.HasValue)
        {
            string dueText = (isOverdue ? "Overdue · " : "") + task.DueDate.Value.ToString("dd MMM");
            chips.Add(Chip(isOverdue ? IcWarning : IcCalendar, dueText, dBg, dFg));

            int mins = task.ReminderMinutes ?? NotificationService.DefaultMinutes;
            string time = DateTime.Today.AddMinutes(mins).ToString("h:mm tt");
            string rep = task.RepeatEveryHours > 0 ? $" ×{task.RepeatEveryHours}h" : "";
            if (!task.IsCompleted)
                chips.Add(Chip(IcAlarm, time + rep, d ? "#26312B" : "#EEF2EF", d ? "#B7C4BC" : "#5B6A61"));
        }
        else
        {
            chips.Add(Chip(IcCalendar, "No due date", dBg, dFg));
        }

        var chipScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = chips
        };

        var editButton = IconButton(IcEdit, d ? "#26294A" : "#EEF2FF", d ? "#A5B4FC" : "#4338CA", 36, () =>
        {
            OpenForm(task);
            return Task.CompletedTask;
        });

        var deleteButton = IconButton(IcDelete, d ? "#3B1D24" : "#FFF1F2", d ? "#FDA4AF" : "#BE123C", 36, async () =>
        {
            bool confirmed = await DisplayAlertAsync("Delete Task", $"Delete '{task.Title}'?", "Delete", "Cancel");
            if (!confirmed) return;
            try
            {
                await _database.DeleteTaskAsync(task);
                NotificationService.Cancel(task.Id);
                await LoadTasksAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Error", "Could not delete task: " + ex.Message, "OK");
            }
        });

        var actions = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        actions.Add(editButton);
        actions.Add(deleteButton);

        var bottomRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        bottomRow.Add(chipScroll, 0, 0);
        bottomRow.Add(actions, 1, 0);

        var body = new VerticalStackLayout { Spacing = 10, Margin = new Thickness(12, 14, 14, 14) };
        body.Add(titleLabel);
        body.Add(bottomRow);
        check.Margin = new Thickness(14, 14, 0, 14);

        var content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = 5 },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 0
        };
        content.Add(new BoxView { Color = Color.FromArgb(task.IsCompleted ? "#8A978F" : stripe) }, 0, 0);
        content.Add(check, 1, 0);
        content.Add(body, 2, 0);

        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Opacity = task.IsCompleted ? 0.8 : 1,
            Content = content,
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Colors.Black),
                Offset = new Point(0, 3),
                Radius = 10,
                Opacity = d ? 0.35f : 0.08f
            }
        };
        card.SetDynamicResource(VisualElement.BackgroundColorProperty, "CardBg");

        _cards[task.Id] = card;
        TaskList.Children.Add(card);

        if (animateIndex >= 0)
        {
            double target = card.Opacity;
            card.Opacity = 0;
            card.TranslationY = 18;
            _ = Task.Delay(Math.Min(animateIndex, 8) * 60).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.WhenAll(card.FadeToAsync(target, 280),
                                       card.TranslateToAsync(0, 0, 280, Easing.CubicOut));
                }));
        }
    }

    // ---------- Filters ----------
    private async void OnAllTasksClicked(object? sender, EventArgs e)
    {
        _currentFilter = "All"; _currentDateFilter = "All";
        UpdateDateFilterButtons();
        await LoadTasksAsync();
    }

    private async void OnPendingTasksClicked(object? sender, EventArgs e)
    {
        _currentFilter = "Pending"; _currentDateFilter = "All";
        UpdateDateFilterButtons();
        await LoadTasksAsync();
    }

    private async void OnCompletedTasksClicked(object? sender, EventArgs e)
    {
        _currentFilter = "Completed"; _currentDateFilter = "All";
        UpdateDateFilterButtons();
        await LoadTasksAsync();
    }

    // Tapping an active date chip again turns it off
    private async Task ToggleDateFilter(string name)
    {
        _currentDateFilter = _currentDateFilter == name ? "All" : name;
        UpdateDateFilterButtons();
        await LoadTasksAsync();
    }

    private async void OnTodayTasksClicked(object? sender, EventArgs e) => await ToggleDateFilter("Today");
    private async void OnUpcomingTasksClicked(object? sender, EventArgs e) => await ToggleDateFilter("Upcoming");
    private async void OnOverdueTasksClicked(object? sender, EventArgs e) => await ToggleDateFilter("Overdue");

    private void SetChip(Button b, bool on)
    {
        b.BackgroundColor = on ? Active : Inactive;
        b.TextColor = on ? Colors.White : InactiveText;
    }

    private void UpdateFilterButtons()
    {
        SetChip(AllTasksButton, _currentFilter == "All" && _currentDateFilter == "All");
        SetChip(PendingTasksButton, _currentFilter == "Pending");
        SetChip(CompletedTasksButton, _currentFilter == "Completed");
    }

    private void UpdateDateFilterButtons()
    {
        SetChip(TodayTasksButton, _currentDateFilter == "Today");
        SetChip(UpcomingTasksButton, _currentDateFilter == "Upcoming");
        SetChip(OverdueTasksButton, _currentDateFilter == "Overdue");
        UpdateFilterButtons();
    }
}