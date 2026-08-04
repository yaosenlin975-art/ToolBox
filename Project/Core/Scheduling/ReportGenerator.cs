using System.IO;
using System.Text;
using System.Text.Json;
using ToolBox.Core.Llm;
using ToolBox.Core.Providers;

namespace ToolBox.Core.Scheduling;

public class ReportGenerator
{
    private static ReportGenerator? instance;
    public static ReportGenerator Instance => instance ??= new ReportGenerator();

    private readonly string reportsDir;
    private const int MaxAppsInSummary = 10;
    private const int MaxTimelineEntries = 30;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private ReportGenerator()
    {
        reportsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "Data", "reports");
        Directory.CreateDirectory(Path.Combine(reportsDir, "daily"));
        Directory.CreateDirectory(Path.Combine(reportsDir, "weekly"));
    }

    public async Task<string> GenerateDailyReportAsync(DateTime? date = null)
    {
        var targetDate = date ?? DateTime.UtcNow;
        var records = ScreenshotTracker.Instance.GetTodayRecords();

        if (records.Count == 0)
        {
            var dateStr = targetDate.ToString("yyyy-MM-dd");
            return $"# 日报 - {dateStr}\n\n今日无活动记录。\n";
        }

        var provider = ProviderManager.Instance.CreateActiveProvider();
        if (provider != null)
        {
            try
            {
                return await GenerateWithLlmAsync(provider, BuildDailyInput(targetDate, records), ReportType.Daily);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] ReportGenerator LLM failed, fallback: {ex.Message}");
            }
        }

        return GenerateDailyFallback(targetDate, records);
    }

    public async Task<string> GenerateWeeklyReportAsync()
    {
        var today = DateTime.UtcNow;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);

        var allRecords = LoadWeekRecords(weekStart);
        if (allRecords.Count == 0)
        {
            return $"# 周报 - {weekStart:yyyy-MM-dd} ~ {today:yyyy-MM-dd}\n\n本周无活动记录。\n";
        }

        var provider = ProviderManager.Instance.CreateActiveProvider();
        if (provider != null)
        {
            try
            {
                return await GenerateWithLlmAsync(provider, BuildWeeklyInput(weekStart, allRecords), ReportType.Weekly);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] ReportGenerator LLM failed, fallback: {ex.Message}");
            }
        }

        return GenerateWeeklyFallback(weekStart, allRecords);
    }

    private async Task<string> GenerateWithLlmAsync(ILlmProvider provider, object input, ReportType type)
    {
        var systemPrompt = type == ReportType.Daily ? DailySystemPrompt : WeeklySystemPrompt;
        var userContent = JsonSerializer.Serialize(input, JsonOpts);

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userContent }
        };

        var sb = new StringBuilder();
        await foreach (var chunk in provider.ChatAsync(messages))
        {
            if (chunk.Error != null)
                throw new InvalidOperationException($"LLM error: {chunk.Error}");
            if (chunk.Text != null)
                sb.Append(chunk.Text);
        }

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("LLM returned empty response");

        return result;
    }

    #region Daily

    private object BuildDailyInput(DateTime date, List<ActivityRecord> records)
    {
        var grouped = records.GroupBy(r => r.App)
            .OrderByDescending(g => g.Sum(r => r.DurationSeconds))
            .Take(MaxAppsInSummary)
            .ToList();

        var timeDistribution = new Dictionary<string, string>();
        foreach (var g in grouped)
        {
            var total = g.Sum(r => r.DurationSeconds);
            timeDistribution[g.Key] = $"{total / 3600.0:F1}h";
        }

        var sorted = records.OrderBy(r => r.Timestamp).Take(MaxTimelineEntries).ToList();

        return new
        {
            date = date.ToString("yyyy-MM-dd"),
            period = "daily",
            total_records = records.Count,
            unique_apps = grouped.Select(g => g.Key).ToList(),
            time_distribution = timeDistribution,
            timeline = sorted.Select(r => new
            {
                time = r.Timestamp.ToLocalTime().ToString("HH:mm"),
                app = r.App,
                title = Truncate(r.Title, 60),
                duration_seconds = r.DurationSeconds
            })
        };
    }

    private string GenerateDailyFallback(DateTime date, List<ActivityRecord> records)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine($"# 日报 - {dateStr}");
        sb.AppendLine();

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

    #endregion

    #region Weekly

    private List<ActivityRecord> LoadWeekRecords(DateTime weekStart)
    {
        var allRecords = new List<ActivityRecord>();
        for (int i = 0; i < 7; i++)
        {
            var date = weekStart.AddDays(i);
            var dateStr = date.ToString("yyyy-MM-dd");
            var filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToolBox", "Data", "activity", $"{dateStr}.jsonl");

            if (!File.Exists(filePath)) continue;
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<ActivityRecord>(line);
                    if (record != null) allRecords.Add(record);
                }
                catch { }
            }
        }
        return allRecords;
    }

    private object BuildWeeklyInput(DateTime weekStart, List<ActivityRecord> records)
    {
        var today = weekStart.AddDays(6);
        var grouped = records.GroupBy(r => r.App)
            .OrderByDescending(g => g.Sum(r => r.DurationSeconds))
            .Take(MaxAppsInSummary)
            .ToList();

        var dailyBreakdown = records.GroupBy(r => r.Timestamp.Date)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key.ToString("yyyy-MM-dd"),
                g => g.GroupBy(r => r.App)
                    .ToDictionary(a => a.Key, a => $"{a.Sum(r => r.DurationSeconds) / 3600.0:F1}h"));

        var timeDistribution = new Dictionary<string, string>();
        foreach (var g in grouped)
        {
            var total = g.Sum(r => r.DurationSeconds);
            timeDistribution[g.Key] = $"{total / 3600.0:F1}h";
        }

        return new
        {
            period = "weekly",
            week_start = weekStart.ToString("yyyy-MM-dd"),
            week_end = today.ToString("yyyy-MM-dd"),
            total_records = records.Count,
            active_days = records.Select(r => r.Timestamp.Date).Distinct().Count(),
            unique_apps = grouped.Select(g => g.Key).ToList(),
            time_distribution = timeDistribution,
            daily_breakdown = dailyBreakdown
        };
    }

    private string GenerateWeeklyFallback(DateTime weekStart, List<ActivityRecord> records)
    {
        var today = weekStart.AddDays(6);
        var sb = new StringBuilder();
        sb.AppendLine($"# 周报 - {weekStart:yyyy-MM-dd} ~ {today:yyyy-MM-dd}");
        sb.AppendLine();

        var dailyStats = records.GroupBy(r => r.Timestamp.Date)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString("yyyy-MM-dd"), g => g.Count());

        sb.AppendLine("## 每日活动量");
        sb.AppendLine();
        foreach (var kv in dailyStats)
        {
            sb.AppendLine($"- {kv.Key}: {kv.Value} 条记录");
        }

        sb.AppendLine();
        sb.AppendLine("## 应用使用统计");
        sb.AppendLine();
        sb.AppendLine("| 应用 | 时长 | 分类 |");
        sb.AppendLine("|------|------|------|");

        var grouped = records.GroupBy(r => r.App)
            .OrderByDescending(g => g.Sum(r => r.DurationSeconds));
        foreach (var group in grouped)
        {
            var totalSeconds = group.Sum(r => r.DurationSeconds);
            var hours = totalSeconds / 3600.0;
            var category = group.First().Category;
            sb.AppendLine($"| {group.Key} | {hours:F1}h | {category} |");
        }

        return sb.ToString();
    }

    #endregion

    public void SaveReport(string content, string type = "daily")
    {
        var fileName = $"{DateTime.UtcNow:yyyy-MM-dd}.md";
        var dir = Path.Combine(reportsDir, type);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    private enum ReportType { Daily, Weekly }

    private const string DailySystemPrompt = """
        你是一个工作行为分析助手。根据用户一天的应用使用行为数据，生成简洁的工作日报。

        规则:
        1. 只基于提供的数据总结，不要编造任何不存在的信息
        2. 将相似活动归类（如"浏览多个技术页面"归为"技术调研"）
        3. 识别主要工作主题和专注方向
        4. 输出结构化的 Markdown 日报，使用中文
        5. 控制在合理篇幅内，不要过长

        输出格式:
        ## YYYY-MM-DD 工作日报

        ### 主要工作
        - [活动类别]: 具体描述

        ### 时间分布
        | 活动 | 时长 | 占比 |
        |------|------|------|

        ### 工作亮点
        - 一句话总结今天最重要的事

        ### 备注
        - （如有异常活动或空白时段，简单提及）
        """;

    private const string WeeklySystemPrompt = """
        你是一个工作行为分析助手。根据用户一周的应用使用行为数据，生成工作总结周报。

        规则:
        1. 从每日数据中提炼周维度的关键活动
        2. 按项目/主题归类，而非按时间
        3. 识别趋势（如"本周后半段集中做 X"）
        4. 输出结构化的 Markdown 周报，使用中文
        5. 控制在合理篇幅内，不要过长

        输出格式:
        ## YYYY 第 WW 周 工作周报

        ### 本周概览
        - 总记录数: X 条
        - 活跃天数: X 天
        - 主要工具: ...

        ### 重点工作
        #### 1. [项目/主题名]
        - 活动描述
        - 涉及工具
        - 时间估算

        ### 趋势观察
        - ...

        ### 下周建议
        - ...
        """;
}
