using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolBox.Core.Markdown;

namespace ToolBox.Views.Chat;

public partial class MessageBubble : UserControl
{
    private string _rawText = "";
    private bool _needsRender;
    private int cachedChatFontSize = -1;

    public MessageBubble()
    {
        InitializeComponent();
        ContentDocument.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var parentScroller = FindParentScrollViewer(this);
        if (parentScroller != null)
            parentScroller.ScrollToVerticalOffset(parentScroller.VerticalOffset - e.Delta / 3.0);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is ScrollViewer sc) return sc;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    public event Action<string>? QuoteRequested;

    private void QuoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        QuoteRequested?.Invoke(_rawText);
    }

    public void SetMessage(string role, string content, bool showRole = false)
    {
        _rawText = content ?? "";
        _needsRender = false;
        RoleLabel.Text = role switch
        {
            "user" => "你",
            "assistant" => "助手",
            "system" => "系统",
            "tool" => "工具",
            _ => role
        };
        RoleLabel.Visibility = showRole ? Visibility.Visible : Visibility.Collapsed;

        Brush bg;
        Brush fg;
        HorizontalAlignment align;
        switch (role)
        {
            case "user":
                bg = (Brush)FindResource("ChatBgUserBrush");
                fg = (Brush)FindResource("ChatTextUserBrush");
                align = HorizontalAlignment.Right;
                break;
            case "assistant":
                bg = Brushes.Transparent;
                fg = (Brush)FindResource("ChatTextAssistantBrush");
                align = HorizontalAlignment.Left;
                break;
            case "system":
                bg = (Brush)FindResource("ChatBgSystemBrush");
                fg = (Brush)FindResource("ChatTextSystemBrush");
                align = HorizontalAlignment.Left;
                break;
            case "tool":
                bg = (Brush)FindResource("ChatBgToolBrush");
                fg = (Brush)FindResource("ChatTextToolBrush");
                align = HorizontalAlignment.Left;
                break;
            default:
                bg = Brushes.White;
                fg = Brushes.Black;
                align = HorizontalAlignment.Left;
                break;
        }

        BubbleBorder.Background = bg;
        BubbleBorder.BorderThickness = role == "assistant" ? new Thickness(0) : new Thickness(1);
        RoleLabel.Foreground = fg;
        HorizontalAlignment = align;
        ContentDocument.Foreground = fg;
        Margin = role == "assistant" ? new Thickness(16, 2, 16, 2) : new Thickness(16, 4, 16, 4);

        RenderMarkdown();
    }

    public void SetStreaming(bool isStreaming)
    {
        StreamingDot.Visibility = isStreaming ? Visibility.Visible : Visibility.Collapsed;
        if (!isStreaming && _needsRender)
        {
            RenderMarkdown();
        }
    }

    public void AppendContent(string text)
    {
        _rawText += text ?? "";
        _needsRender = true;
    }

    public void RenderMarkdown()
    {
        _needsRender = false;
        try
        {
            var doc = MarkdownDocument.Parse(_rawText);
            ContentDocument.Document = doc;
        }
        catch
        {
            // Fallback: plain text
            var doc = new System.Windows.Documents.FlowDocument();
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(_rawText)));
            ContentDocument.Document = doc;
        }

        if (cachedChatFontSize < 0)
            cachedChatFontSize = ToolBox.Models.ToolBoxOption.Load().Data.ChatFontSize;
        if (cachedChatFontSize > 0)
            ContentDocument.FontSize = cachedChatFontSize;
    }
}

