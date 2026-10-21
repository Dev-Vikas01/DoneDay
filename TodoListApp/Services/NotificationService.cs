using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TodoListApp.Models;

namespace TodoListApp.Services;

/// <summary>
/// Per-task reminders: first reminder at the task's chosen time, optionally
/// repeating every N hours (until 9 PM) on the due date. Tapping a notification
/// raises TaskTapped so the UI can jump to that task.
/// Every method swallows exceptions so notifications can never break the app.
/// </summary>
public static class NotificationService
{
    public const int DefaultMinutes = 9 * 60;     // 9:00 AM
    private const int DayEndMinutes = 21 * 60;    // repeats stop at 9:00 PM
    private const int MaxSlots = 12;              // max notifications per task

    public static int? PendingTaskId { get; private set; }   // set if app was opened by a tap
    public static event Action<int>? TaskTapped;

    private static bool _hooked;

    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            LocalNotificationCenter.Current.NotificationActionTapped += e =>
            {
                try
                {
                    if (e.IsTapped && int.TryParse(e.Request?.ReturningData, out int id))
                    {
                        PendingTaskId = id;
                        TaskTapped?.Invoke(id);
                    }
                }
                catch { /* ignore */ }
            };
        }
        catch { /* ignore */ }
    }

    public static int? ConsumePendingTask()
    {
        int? id = PendingTaskId;
        PendingTaskId = null;
        return id;
    }

    public static async Task InitializeAsync(IEnumerable<TaskItem> tasks)
    {
        try
        {
            var permission = new NotificationPermission
            {
                Android = { RequestPermissionToScheduleExactAlarm = true }
            };

            if (!await LocalNotificationCenter.Current.AreNotificationsEnabled(permission))
                await LocalNotificationCenter.Current.RequestNotificationPermission(permission);

            foreach (var task in tasks)
                await SyncAsync(task);
        }
        catch { /* ignore */ }
    }

    public static async Task SyncAsync(TaskItem task)
    {
        try
        {
            if (task.Id == 0) return;

            Cancel(task.Id);

            if (task.IsCompleted || !task.DueDate.HasValue) return;

            DateTime day = task.DueDate.Value.Date;
            int start = task.ReminderMinutes ?? DefaultMinutes;

            var times = new List<DateTime> { day.AddMinutes(start) };
            if (task.RepeatEveryHours > 0)
            {
                int step = task.RepeatEveryHours * 60;
                for (int m = start + step; m <= DayEndMinutes && times.Count < MaxSlots; m += step)
                    times.Add(day.AddMinutes(m));
            }

            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] <= DateTime.Now) continue;

                await LocalNotificationCenter.Current.Show(new NotificationRequest
                {
                    NotificationId = task.Id * 100 + i,
                    Title = i == 0 ? "Task reminder" : $"Reminder {i + 1} of {times.Count}",
                    Description = $"{task.Title}  ·  {task.Priority} priority",
                    ReturningData = task.Id.ToString(),
                    Schedule = new NotificationRequestSchedule { NotifyTime = times[i] }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Reminder] Sync failed: {ex}");
        }
    }

    // Triple-tap the greeting text in the app to run this.
    public static async Task<string> DiagnoseAsync()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            bool enabled = await LocalNotificationCenter.Current.AreNotificationsEnabled();
            sb.AppendLine($"Notifications allowed: {enabled}");
            if (!enabled)
                await LocalNotificationCenter.Current.RequestNotificationPermission();

            bool shown = await LocalNotificationCenter.Current.Show(new NotificationRequest
            {
                NotificationId = 999999,
                Title = "Test reminder",
                Description = "If you see this, notifications work.",
                ReturningData = "0",
                Schedule = new NotificationRequestSchedule { NotifyTime = DateTime.Now.AddSeconds(20) }
            });
            sb.AppendLine($"Test scheduled for +20s: {shown}");

            var pending = await LocalNotificationCenter.Current.GetPendingNotificationList();
            sb.AppendLine($"Pending reminders: {pending.Count}");
            foreach (var p in pending.Take(8))
                sb.AppendLine($" • id {p.NotificationId} at {p.Schedule.NotifyTime:dd MMM h:mm tt}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERROR: " + ex.Message);
        }
        return sb.ToString();
    }

    public static void Cancel(int taskId)
    {
        try
        {
            LocalNotificationCenter.Current.Cancel(
                Enumerable.Range(0, MaxSlots).Select(i => taskId * 100 + i).ToArray());
        }
        catch { /* ignore */ }
    }
}