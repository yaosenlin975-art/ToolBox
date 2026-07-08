using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ToolBox.Core.Markdown;

public static class MarkdownDocument
{
    private static readonly SolidColorBrush TextPrimary = new((Color)ColorConverter.ConvertFromString("#0F1117"));
    private static readonly SolidColorBrush Secondary = new((Color)ColorConverter.ConvertFromString("#5C6370"));
    private static readonly SolidColorBrush Tertiary = new((Color)ColorConverter.ConvertFromString("#9CA3AF"));
    private static readonly SolidColorBrush Code = new((Color)ColorConverter.ConvertFromString("#6A1B9A"));
    private static readonly SolidColorBrush CodeBg = new((Color)ColorConverter.ConvertFromString("#F0F1F4"));

    public static FlowDocument Parse(string markdown)
    {
        var doc = new FlowDocument { Background = Brushes.Transparent };
        if (string.IsNullOrWhiteSpace(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Code block
            if (line.TrimStart().StartsWith("```"))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                doc.Blocks.Add(BuildParagraph(string.Join("\n", code), Code, CodeBg, true));
                continue;
            }

            // HR
            if (Regex.IsMatch(line.Trim(), "^[-*_]{3,}$"))
            {
                doc.Blocks.Add(new Paragraph(new Run("--------") { Foreground = Tertiary }) { Margin = new Thickness(0, 6, 0, 6) });
                i++;
                continue;
            }

            // Heading
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
            {
                var lvl = h.Groups[1].Value.Length;
                var size = lvl == 1 ? 20.0 : (lvl == 2 ? 17.0 : (lvl == 3 ? 15.0 : 13.0));
                var p = ParseInline(h.Groups[2].Value);
                p.FontSize = size;
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 8, 0, 4);
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            // Unordered list
            if (Regex.IsMatch(line, @"^\s*[-*+]\s+"))
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, 2) };
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*+]\s+"))
                {
                    var content = Regex.Replace(lines[i], @"^\s*[-*+]\s+", "");
                    var li = new ListItem();
                    li.Blocks.Add(ParseInline(content));
                    list.ListItems.Add(li);
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // Ordered list
            if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(0, 2, 0, 2) };
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+\.\s+"))
                {
                    var content = Regex.Replace(lines[i], @"^\s*\d+\.\s+", "");
                    var li = new ListItem();
                    li.Blocks.Add(ParseInline(content));
                    list.ListItems.Add(li);
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // Quote
            if (line.TrimStart().StartsWith(">"))
            {
                var qLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    qLines.Add(Regex.Replace(lines[i].TrimStart(), "^>\\s*", ""));
                    i++;
                }
                var qp = new Paragraph {
                    Margin = new Thickness(8, 2, 0, 2),
                    Padding = new Thickness(8, 0, 0, 0),
                    BorderBrush = Tertiary,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                };
                qp.Inlines.Add(new Run(string.Join(" ", qLines)) { Foreground = Tertiary });
                doc.Blocks.Add(qp);
                continue;
            }

            // Paragraph (may span multiple lines)
            var para = new List<string> { line };
            i++;
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].TrimStart().StartsWith('#')
                   && !lines[i].TrimStart().StartsWith("```")
                   && !lines[i].TrimStart().StartsWith(">")
                   && !Regex.IsMatch(lines[i], @"^\s*[-*+]\s+")
                   && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s+")
                   && !Regex.IsMatch(lines[i].Trim(), "^[-*_]{3,}$"))
            {
                para.Add(lines[i]);
                i++;
            }

            if (para.Any(l => !string.IsNullOrWhiteSpace(l)))
            {
                var pp = ParseInline(string.Join(" ", para));
                pp.Margin = new Thickness(0, 0, 0, 4);
                doc.Blocks.Add(pp);
            }
        }

        return doc;
    }

    private static Paragraph BuildParagraph(string text, SolidColorBrush fg, SolidColorBrush? bg, bool mono)
    {
        var p = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
        if (bg != null) p.Background = bg;
        if (bg != null) p.Padding = new Thickness(8);
        var r = new Run(text) { Foreground = fg };
        if (mono) r.FontFamily = new FontFamily("Consolas, Global Monospace");
        p.Inlines.Add(r);
        return p;
    }

    private static Paragraph ParseInline(string text)
    {
        var p = new Paragraph();
        if (string.IsNullOrEmpty(text)) return p;
        int i = 0;
        var sb = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            // inline code
            if (text[i] == '`')
            {
                if (sb.Length > 0) { p.Inlines.Add(new Run(sb.ToString()) { Foreground = TextPrimary }); sb.Clear(); }
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    p.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                    {
                        Foreground = Code,
                        Background = CodeBg,
                        FontFamily = new FontFamily("Consolas, Global Monospace"),
                    });
                    i = end + 1;
                    continue;
                }
            }
            // bold
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                if (sb.Length > 0) { p.Inlines.Add(new Run(sb.ToString()) { Foreground = TextPrimary }); sb.Clear(); }
                var end = text.IndexOf("**", i + 2);
                if (end > i) { p.Inlines.Add(new Run(text.Substring(i + 2, end - i - 2)) { FontWeight = FontWeights.Bold, Foreground = TextPrimary }); i = end + 2; continue; }
            }
            // italic
            if (text[i] == '*' && (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                if (sb.Length > 0) { p.Inlines.Add(new Run(sb.ToString()) { Foreground = TextPrimary }); sb.Clear(); }
                var end = text.IndexOf('*', i + 1);
                if (end > i) { p.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1)) { FontStyle = FontStyles.Italic, Foreground = TextPrimary }); i = end + 1; continue; }
            }
            sb.Append(text[i]);
            i++;
        }
        if (sb.Length > 0) p.Inlines.Add(new Run(sb.ToString()) { Foreground = TextPrimary });
        return p;
    }
}