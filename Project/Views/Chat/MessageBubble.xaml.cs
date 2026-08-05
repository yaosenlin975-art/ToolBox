using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolBox.Core.Markdown;

namespace ToolBox.Views.Chat;

public partial class MessageBubble : UserControl
{
    private string _rawText = "";
    private string _role = "assistant";
    private bool _needsRender;
    private int cachedChatFontSize = -1;

    public static readonly DependencyProperty IsCompactModeProperty =
        DependencyProperty.Register(nameof(IsCompactMode), typeof(bool), typeof(MessageBubble),
            new PropertyMetadata(false, OnIsCompactModeChanged));

    public bool IsCompactMode
    {
        get => (bool)GetValue(IsCompactModeProperty);
        set => SetValue(IsCompactModeProperty, value);
    }

    private static void OnIsCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MessageBubble bubble)
            bubble.ApplyCompactStyles();
    }

    private void ApplyCompactStyles()
    {
        var compact = IsCompactMode;
        BubbleBorder.Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(12, 10, 12, 10);
        BubbleBorder.MaxWidth = compact ? double.PositiveInfinity : 500;
        RoleLabel.FontSize = compact ? 10 : 11;
        StreamingDot.FontSize = compact ? 9 : 10;
        Margin = _role == "assistant"
            ? new Thickness(compact ? 8 : 16, 2, compact ? 8 : 16, 2)
            : new Thickness(compact ? 8 : 16, compact ? 2 : 4, compact ? 8 : 16, compact ? 2 : 4);
        if (cachedChatFontSize >= 0)
            ContentDocument.FontSize = GetEffectiveFontSize();
    }

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
        _role = role;
        _needsRender = false;
        RoleLabel.Text = role switch
        {
            "user" => (string)FindResource("LLM.Message.User"),
            "assistant" => (string)FindResource("LLM.Message.Assistant"),
            "system" => (string)FindResource("LLM.Message.System"),
            "tool" => (string)FindResource("LLM.Message.Tool"),
            _ => role
        };
        RoleLabel.Visibility = showRole ? Visibility.Visible : Visibility.Collapsed;

        Brush bg;
        Brush fg;
        HorizontalAlignment align;
        switch (role)
        {
            case "user":
                bg = (Brush)FindResource("ChatUserBgBrush");
                fg = (Brush)FindResource("ChatUserTextBrush");
                align = HorizontalAlignment.Right;
                break;
            case "assistant":
                bg = Brushes.Transparent;
                fg = (Brush)FindResource("ChatAiTextBrush");
                align = HorizontalAlignment.Left;
                break;
            case "system":
                bg = (Brush)FindResource("ChatSystemBgBrush");
                fg = (Brush)FindResource("ChatSystemTextBrush");
                align = HorizontalAlignment.Left;
                break;
            case "tool":
                bg = (Brush)FindResource("ChatToolBgBrush");
                fg = (Brush)FindResource("ChatToolTextBrush");
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
        ApplyCompactStyles();

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
        ContentDocument.FontSize = GetEffectiveFontSize();
    }

    private double GetEffectiveFontSize()
    {
        var baseSize = cachedChatFontSize > 0 ? cachedChatFontSize : 13;
        return IsCompactMode ? Math.Max(baseSize - 1, 10) : baseSize;
    }
}

