using System.IO;
using System.Text;

namespace ToolBox.Core.Scheduling;

public class ReportGenerator
{
    private static ReportGenerator? instance;
    public static ReportGenerator Instance => instance ??= new ReportGenerator();

    private readonly string reportsDir;

    private ReportGenerator()
    {
        reportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "Data", "reports");
        Directory.CreateDirectory(Path.Combine(reportsDir, "daily"));
        Directory.CreateDirectory(Path.Combine(reportsDir, "weekly"));
    }

    public string GenerateDailyReport(DateTime? date = null)
    {
        var targetDate = date ?? DateTime.UtcNow;
        var dateStr = targetDate.ToString("yyyy-MM-dd");
        var records = ScreenshotTracker.Instance.GetTodayRecords();

        var sb = new StringBuilder();
        sb.AppendLine($"# 日报 - {dateStr}");
        sb.AppendLine();

        if (records.Count == 0)
        {
            sb.AppendLine("今日无活动记录。");
            return sb.ToString();
        }

        var grouped = records.GroupBy(r => r.App)
            .OrderByDescending(g => g.Sum(r => r.DurationSeconds));

        sb.AppendLine("## 应用使用统计");
        sb.AppendLine();
        sb.AppendLine("| 应用 | 时长 | 分类 |");
        sb.AppendLine("|------|------|------|");

        foreach (var group in grouped)
        {
            var totalSeconds = group.Sum(r => r.DurationSeconds);
            var hours = totalSeconds / 3600.0;
            var category = group.First().Category;
            sb.AppendLine($"| {group.Key} | {hours:F1}h | {category} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 活动时间线");
        sb.AppendLine();

        var sorted = records.OrderBy(r => r.Timestamp).ToList();
        for (int i = 0; i < Math.Min(sorted.Count, 20); i++)
        {
            var r = sorted[i];
            var time = r.Timestamp.ToLocalTime().ToString("HH:mm");
            sb.AppendLine($"- **{time}** {r.App}: {r.Title} ({r.DurationSeconds}s)");
        }

        if (sorted.Count > 20)
            sb.AppendLine($"- ... 共 {sorted.Count} 条记录");

        return sb.ToString();
    }

    public string GenerateWeeklyReport()
    {
        var sb = new StringBuilder();
        var today = DateTime.UtcNow;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);

        sb.AppendLine($"# 周报 - {weekStart:yyyy-MM-dd} ~ {today:yyyy-MM-dd}");
        sb.AppendLine();

        var dailyStats = new Dictionary<string, int>();
        for (int i = 0; i < 7; i++)
        {
            var date = weekStart.AddDays(i);
            var dateStr = date.ToString("yyyy-MM-dd");
            var filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolBox", "Data", "activity", $"{dateStr}.jsonl");

            if (!File.Exists(filePath)) continue;
            var lines = File.ReadLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).Count();
            dailyStats[dateStr] = lines;
        }

        sb.AppendLine("## 每日活动量");
        sb.AppendLine();
        foreach (var kv in dailyStats)
        {
            sb.AppendLine($"- {kv.Key}: {kv.Value} 条记录");
        }

        return sb.ToString();
    }

    public void SaveReport(string content, string type = "daily")
    {
        var fileName = $"{DateTime.UtcNow:yyyy-MM-dd}.md";
        var dir = Path.Combine(reportsDir, type);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
