using SQLite;

namespace TodoListApp.Models;

[Table("Tasks")]
public class TaskItem
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Priority { get; set; } = "Medium";
    public DateTime? DueDate { get; set; }

    // NEW: minutes after midnight for the first reminder (null = default 9:00 AM)
    public int? ReminderMinutes { get; set; }

    // NEW: 0 = remind once, 1..4 = repeat every N hours on the due date
    public int RepeatEveryHours { get; set; }
}