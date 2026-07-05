using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Scheduling;

public class ScreenshotTracker
{
    private static ScreenshotTracker? instance;
    public static ScreenshotTracker Instance => instance ??= new ScreenshotTracker();

    private readonly string activityDir;
    private readonly Dictionary<string, string> appCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "browsing", ["firefox"] = "browsing", ["edge"] = "browsing", ["brave"] = "browsing",
        ["code"] = "coding", ["rider"] = "coding", ["devenv"] = "coding", ["idea"] = "coding",
        ["wechat"] = "communication", ["dingtalk"] = "communication", ["feishu"] = "communication",
        ["slack"] = "communication", ["teams"] = "communication"
    };
    private string lastApp = "";
    private DateTime lastSwitchTime = DateTime.UtcNow;

    private ScreenshotTracker()
    {
        activityDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "Data", "activity");
        Directory.CreateDirectory(activityDir);
    }

    public void RecordWindowSwitch(string appName, string windowTitle)
    {
        var now = DateTime.UtcNow;
        var duration = (int)(now - lastSwitchTime).TotalSeconds;

        if (!string.IsNullOrEmpty(lastApp) && duration > 0)
        {
            var category = CategorizeApp(lastApp);
            var record = new ActivityRecord
            {
                Timestamp = lastSwitchTime,
                App = lastApp,
                Title = windowTitle,
                DurationSeconds = duration,
                Category = category
            };
            AppendRecord(record);
        }

        lastApp = appName;
        lastSwitchTime = now;
    }

    public List<ActivityRecord> GetTodayRecords()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(activityDir, $"{today}.jsonl");
        if (!File.Exists(filePath)) return [];

        var records = new List<ActivityRecord>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ActivityRecord>(line);
                if (record != null) records.Add(record);
            }
            catch { }
        }
        return records;
    }

    private string CategorizeApp(string appName)
    {
        foreach (var kvp in appCategoryMap)
        {
            if (appName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return "other";
    }

    private void AppendRecord(ActivityRecord record)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(activityDir, $"{today}.jsonl");
        var json = JsonSerializer.Serialize(record);
        File.AppendAllText(filePath, json + "\n");
    }
}

public class ActivityRecord
{
    public DateTime Timestamp { get; set; }
    public string App { get; set; } = "";
    public string Title { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string Category { get; set; } = "other";
}
