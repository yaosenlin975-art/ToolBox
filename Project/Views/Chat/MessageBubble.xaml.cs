using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ToolBox.Views.Chat;

public partial class MessageBubble : UserControl
{
    public MessageBubble()
    {
        InitializeComponent();
    }

    public void SetMessage(string role, string content, bool showRole = false)
    {
        RoleLabel.Text = role switch
        {
            "user" => "你",
            "assistant" => "助手",
            "system" => "系统",
            "tool" => "工具",
            _ => role
        };

        RoleLabel.Visibility = showRole ? Visibility.Visible : Visibility.Collapsed;
        ContentText.Text = content;

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
                bg = (Brush)FindResource("ChatBgAssistantBrush");
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
        ContentText.Foreground = fg;
        RoleLabel.Foreground = fg;
        HorizontalAlignment = align;
    }

    public void SetStreaming(bool isStreaming)
    {
        StreamingDot.Visibility = isStreaming ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AppendContent(string text)
    {
        ContentText.Text += text;
    }
}
