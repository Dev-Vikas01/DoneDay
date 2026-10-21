
using SQLite;
using TodoListApp.Models;

namespace TodoListApp.Services;

public class TaskDatabase
{
    private readonly SQLiteAsyncConnection _database;

    public TaskDatabase()
    {
        string databasePath = Path.Combine(
            FileSystem.AppDataDirectory,
            "tasks.db3");

        _database = new SQLiteAsyncConnection(databasePath);
    }


    public async Task InitializeAsync()
    {
        await _database.CreateTableAsync<TaskItem>();

        var columns = await _database.QueryAsync<ColumnInfo>(
            "PRAGMA table_info(Tasks)");

        bool priorityExists = columns.Any(
            column => column.Name == "Priority");

        if (!priorityExists)
        {
            await _database.ExecuteAsync(
                "ALTER TABLE Tasks ADD COLUMN Priority TEXT NOT NULL DEFAULT 'Medium'");
        }

        var dueDateColumn = await _database.QueryAsync<ColumnInfo>(
            "PRAGMA table_info(Tasks)");

        bool dueDateExists = dueDateColumn.Any(
            column => column.Name == "DueDate");

        if (!dueDateExists)
        {
            await _database.ExecuteAsync(
                "ALTER TABLE Tasks ADD COLUMN DueDate TEXT NULL");
        }

    }

    private class ColumnInfo
    {
        public string Name { get; set; } = string.Empty;
    }


    public Task<List<TaskItem>> GetTasksAsync()
    {
        return _database.Table<TaskItem>()
            .OrderByDescending(task => task.CreatedAt)
            .ToListAsync();
    }

    public Task<int> SaveTaskAsync(TaskItem task)
    {
        if (task.Id == 0)
        {
            return _database.InsertAsync(task);
        }

        return _database.UpdateAsync(task);
    }

    public Task<int> DeleteTaskAsync(TaskItem task)
    {
        return _database.DeleteAsync(task);
    }
}
