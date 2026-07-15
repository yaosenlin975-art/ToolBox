using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace ToolBox.Core.Habits;

/// <summary>习惯打卡条目 (P3-05)</summary>
public class HabitEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Frequency { get; set; } = "daily"; // daily, weekly
    public int TargetPerWeek { get; set; } = 7;
    public List<string> CheckInDates { get; set; } = new(); // yyyy-MM-dd
    public string Color { get; set; } = "#409EFF";
    public string TodoTemplate { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public int CurrentStreak
    {
        get
        {
            var streak = 0;
            var date = DateTime.Today;
            while (CheckInDates.Contains(date.ToString("yyyy-MM-dd")))
            {
                streak++;
                date = date.AddDays(-1);
            }
            return streak;
        }
    }

    public int TotalCheckIns => CheckInDates.Count;

    public bool IsCheckedInToday => CheckInDates.Contains(DateTime.Today.ToString("yyyy-MM-dd"));
}

/// <summary>习惯打卡存储</summary>
public class HabitStore
{
    public static HabitStore Instance { get; } = new();

    private readonly string _path;
    private List<HabitEntry> _habits = new();

    public event Action? HabitsChanged;

    private HabitStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "habits.json");
        Load();
    }

    public IReadOnlyList<HabitEntry> Habits => _habits;

    public void Add(HabitEntry habit)
    {
        _habits.Add(habit);
        Save();
        HabitsChanged?.Invoke();
    }

    public void Update(HabitEntry habit)
    {
        var idx = _habits.FindIndex(h => h.Id == habit.Id);
        if (idx >= 0) _habits[idx] = habit;
        Save();
        HabitsChanged?.Invoke();
    }

    public void Delete(string id)
    {
        _habits.RemoveAll(h => h.Id == id);
        Save();
        HabitsChanged?.Invoke();
    }

    public void CheckIn(string id)
    {
        var habit = _habits.FirstOrDefault(h => h.Id == id);
        if (habit == null) return;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!habit.CheckInDates.Contains(today))
        {
            habit.CheckInDates.Add(today);
            Save();
            HabitsChanged?.Invoke();
        }
    }

    public void UncheckIn(string id)
    {
        var habit = _habits.FirstOrDefault(h => h.Id == id);
        if (habit == null) return;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (habit.CheckInDates.Remove(today))
        {
            Save();
            HabitsChanged?.Invoke();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _habits = JsonSerializer.Deserialize<List<HabitEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { _habits = new(); }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_habits, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
