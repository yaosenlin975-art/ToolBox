using System.Text.RegularExpressions;

namespace ToolBox.Core.Todo;

/// <summary>智能日期解析器（正则 + 可选 LLM 回退）</summary>
public static class SmartDateParser
{
    private static readonly List<DatePattern> Patterns = new()
    {
        // 相对日期
        new(@"今天", _ => DateTime.Today),
        new(@"明天", _ => DateTime.Today.AddDays(1)),
        new(@"后天", _ => DateTime.Today.AddDays(2)),
        new(@"大后天", _ => DateTime.Today.AddDays(3)),
        new(@"昨天", _ => DateTime.Today.AddDays(-1)),
        new(@"前天", _ => DateTime.Today.AddDays(-2)),

        // 星期表达
        new(@"下?周([一二三四五六日天])", m =>
        {
            var dayMap = new Dictionary<string, DayOfWeek>
            {
                ["一"] = DayOfWeek.Monday, ["二"] = DayOfWeek.Tuesday, ["三"] = DayOfWeek.Wednesday,
                ["四"] = DayOfWeek.Thursday, ["五"] = DayOfWeek.Friday, ["六"] = DayOfWeek.Saturday,
                ["日"] = DayOfWeek.Sunday, ["天"] = DayOfWeek.Sunday
            };
            if (!dayMap.TryGetValue(m.Groups[1].Value, out var targetDay)) return DateTime.Today;
            var today = DateTime.Today;
            var daysUntil = ((int)targetDay - (int)today.DayOfWeek + 7) % 7;
            if (daysUntil == 0) daysUntil = 7;
            var date = today.AddDays(daysUntil);
            if (m.Value.StartsWith("下")) date = date.AddDays(7);
            return date;
        }),

        // 绝对日期: 7月15日, 7.15, 2026-07-15
        new(@"(\d{4})[年\-/.](\d{1,2})[月\-/.](\d{1,2})[日号]?", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var y) &&
                int.TryParse(m.Groups[2].Value, out var mo) &&
                int.TryParse(m.Groups[3].Value, out var d))
                return new DateTime(y, mo, d);
            return DateTime.Today;
        }),
        new(@"(\d{1,2})[月\-/.](\d{1,2})[日号]?", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var mo) &&
                int.TryParse(m.Groups[2].Value, out var d))
            {
                var year = DateTime.Today.Year;
                var date = new DateTime(year, mo, d);
                if (date < DateTime.Today) date = date.AddYears(1);
                return date;
            }
            return DateTime.Today;
        }),

        // 时间表达: 下午3点, 15:00, 上午10点半
        new(@"(上午|下午|晚上|早上)?(\d{1,2})[点时:](\d{0,2})\s*(半|整)?", m =>
        {
            var hour = int.Parse(m.Groups[2].Value);
            var minute = 0;
            if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var min)) minute = min;
            if (m.Groups[4].Value == "半") minute = 30;
            var period = m.Groups[1].Value;
            if (period == "下午" || period == "晚上") { if (hour < 12) hour += 12; }
            else if (period == "上午" || period == "早上") { if (hour == 12) hour = 0; }
            return DateTime.Today.AddHours(hour).AddMinutes(minute);
        }),

        // 每天/每周/每月
        new(@"每天", _ => DateTime.Today.AddDays(1)),
        new(@"每周[一二三四五六日天]", _ => DateTime.Today.AddDays(7)),
        new(@"每月(\d{1,2})[日号]", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var day))
            {
                var date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, Math.Min(day, 28));
                if (date <= DateTime.Today) date = date.AddMonths(1);
                return date;
            }
            return DateTime.Today.AddMonths(1);
        }),
    };

    /// <summary>解析文本中的日期时间</summary>
    public static DateParseResult Parse(string text)
    {
        var result = new DateParseResult { OriginalText = text };

        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(text);
            if (match.Success)
            {
                try
                {
                    var dt = pattern.Extractor(match);
                    result.ParsedDate = dt;
                    result.MatchedText = match.Value;
                    result.IsRepeat = pattern.Regex.ToString().Contains("每");
                    result.Confidence = 0.9;
                    break;
                }
                catch { }
            }
        }

        return result;
    }

    /// <summary>高亮文本中的日期部分</summary>
    public static List<(int start, int length)> GetHighlightRanges(string text)
    {
        var ranges = new List<(int start, int length)>();
        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(text);
            if (match.Success)
                ranges.Add((match.Index, match.Length));
        }
        return ranges;
    }

    private record DatePattern(Regex Regex, Func<Match, DateTime> Extractor)
    {
        public DatePattern(string pattern, Func<Match, DateTime> extractor)
            : this(new Regex(pattern, RegexOptions.Compiled), extractor) { }
    }
}

public class DateParseResult
{
    public string OriginalText { get; set; } = string.Empty;
    public DateTime? ParsedDate { get; set; }
    public string? MatchedText { get; set; }
    public bool IsRepeat { get; set; }
    public double Confidence { get; set; }
    public bool HasDate => ParsedDate.HasValue;
}
