using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace ToolBox.Core.Pomodoro;

/// <summary>番茄钟服务 (P3-04)</summary>
public class PomodoroService
{
    public static PomodoroService Instance { get; } = new();

    public bool IsRunning { get; private set; }
    public bool IsFocusMode { get; private set; }
    public int RemainingSeconds { get; private set; }
    public int TotalSeconds { get; private set; }
    public string? AssociatedTodoId { get; set; }

    public event Action? Tick;
    public event Action<bool>? Completed; // true = focus completed
    public event Action? Started;
    public event Action? Stopped;

    private System.Threading.Timer? _timer;

    private PomodoroService() { }

    public void StartFocus(int minutes = 25)
    {
        Start(minutes, true);
    }

    public void StartBreak(int minutes = 5)
    {
        Start(minutes, false);
    }

    private void Start(int minutes, bool focus)
    {
        IsRunning = true;
        IsFocusMode = focus;
        TotalSeconds = minutes * 60;
        RemainingSeconds = TotalSeconds;
        Started?.Invoke();
        Tick?.Invoke();
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ =>
        {
            RemainingSeconds--;
            Tick?.Invoke();
            if (RemainingSeconds <= 0)
            {
                Complete();
            }
        }, null, 1000, 1000);
    }

    public void Pause()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    public void Resume()
    {
        if (RemainingSeconds <= 0) return;
        IsRunning = true;
        _timer = new System.Threading.Timer(_ =>
        {
            RemainingSeconds--;
            Tick?.Invoke();
            if (RemainingSeconds <= 0)
            {
                Complete();
            }
        }, null, 1000, 1000);
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        RemainingSeconds = 0;
        Stopped?.Invoke();
    }

    private void Complete()
    {
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        if (IsFocusMode)
        {
            LogPomodoro(DateTime.Now);
        }
        Completed?.Invoke(IsFocusMode);
    }

    public int GetTodayPomodoroCount()
    {
        var path = GetLogPath();
        try
        {
            if (!File.Exists(path)) return 0;
            var entries = JsonSerializer.Deserialize<List<PomodoroEntry>>(File.ReadAllText(path)) ?? new();
            return entries.Count(e => e.Timestamp.Date == DateTime.Today);
        }
        catch { return 0; }
    }

    private void LogPomodoro(DateTime timestamp)
    {
        var path = GetLogPath();
        var entries = new List<PomodoroEntry>();
        try
        {
            if (File.Exists(path))
                entries = JsonSerializer.Deserialize<List<PomodoroEntry>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        entries.Add(new PomodoroEntry { Timestamp = timestamp, DurationMinutes = TotalSeconds / 60, TodoId = AssociatedTodoId });
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetLogPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pomodoro_log.json");
    }
}

public class PomodoroEntry
{
    public DateTime Timestamp { get; set; }
    public int DurationMinutes { get; set; }
    public string? TodoId { get; set; }
}
