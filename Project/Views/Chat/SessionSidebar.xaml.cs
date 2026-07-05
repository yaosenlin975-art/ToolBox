using System.Windows;
using System.Windows.Controls;
using ToolBox.Core.Llm;

namespace ToolBox.Views.Chat;

public partial class SessionSidebar : UserControl
{
    public event EventHandler<ChatSession>? SessionSelected;
    public event EventHandler? NewSessionRequested;

    public SessionSidebar()
    {
        InitializeComponent();
    }

    public void LoadSessions(IReadOnlyList<ChatSession> sessions)
    {
        SessionList.ItemsSource = sessions.ToList();
        if (sessions.Count > 0)
            SessionList.SelectedIndex = 0;
    }

    public void SelectSession(string sessionId)
    {
        for (int i = 0; i < SessionList.Items.Count; i++)
        {
            if (SessionList.Items[i] is ChatSession s && s.Id == sessionId)
            {
                SessionList.SelectedIndex = i;
                break;
            }
        }
    }

    public void RefreshSessions()
    {
        var current = SessionList.ItemsSource as System.Collections.IList;
        var sessions = ChatManager.Instance.Sessions.ToList();
        SessionList.ItemsSource = sessions;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is ChatSession session)
            SessionSelected?.Invoke(this, session);
    }

    private void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        NewSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SessionItem_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatSession session)
        {
            var cm = new ContextMenu
            {
                Background = (Brush)Application.Current.FindResource("ChatSidebarBgBrush"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
                BorderBrush = (Brush)Application.Current.FindResource("ChatSidebarBorderBrush"),
                Padding = new Thickness(4)
            };

            var pinItem = new MenuItem
            {
                Header = session.IsPinned ? "取消置顶" : "置顶",
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")
            };
            pinItem.Click += (s, args) =>
            {
                ChatManager.Instance.TogglePin(session.Id);
                RefreshSessions();
            };

            var deleteItem = new MenuItem
            {
                Header = "删除",
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 13,
                Foreground = (Brush)Application.Current.FindResource("DangerBrush")
            };
            deleteItem.Click += (s, args) =>
            {
                ChatManager.Instance.DeleteSession(session.Id);
                RefreshSessions();
            };

            cm.Items.Add(pinItem);
            cm.Items.Add(new Separator());
            cm.Items.Add(deleteItem);
            cm.IsOpen = true;
            e.Handled = true;
        }
    }
}
