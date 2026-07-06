using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Llm;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;

namespace ToolBox.Views.Chat;

public partial class ChatView : UserControl
{
    private readonly ChatManager chatManager;
    private readonly ToolRegistry toolRegistry;
    private ChatSession? currentSession;
    private Agent? currentAgent;
    private bool isStreaming;

    public static readonly DependencyProperty IsCompactModeProperty =
        DependencyProperty.Register(nameof(IsCompactMode), typeof(bool), typeof(ChatView),
            new PropertyMetadata(false, OnIsCompactModeChanged));

    public bool IsCompactMode
    {
        get => (bool)GetValue(IsCompactModeProperty);
        set => SetValue(IsCompactModeProperty, value);
    }

    private static void OnIsCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ChatView view) return;
        var isCompact = (bool)e.NewValue;
        view.SidebarColumn.Width = isCompact ? new GridLength(0) : new GridLength(220);
        view.SidebarBorder.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
    }

    public ChatView()
    {
        InitializeComponent();
        chatManager = ChatManager.Instance;
        toolRegistry = new ToolRegistry();
        toolRegistry.Register(typeof(FileTools));
        toolRegistry.Register(typeof(TodoTools));
        toolRegistry.Register(typeof(WebSearchTools));
        LoadSessions();
    }

    public IReadOnlyList<ChatSession> Sessions => chatManager.Sessions;

    public void LoadSessions()
    {
        SessionList.ItemsSource = chatManager.Sessions.ToList();
        if (chatManager.Sessions.Count > 0)
            SessionList.SelectedIndex = 0;
    }

    public void SelectSession(string sessionId)
    {
        var session = chatManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            currentSession = session;
            chatManager.SwitchSession(session.Id);
            currentAgent = CreateAgent(session);
            LoadMessages(session);
        }
    }

    // Sync wrapper
    public ChatSession CreateNewSession()
        => CreateNewSessionAsync().GetAwaiter().GetResult();
    public async Task<ChatSession> CreateNewSessionAsync()
    {
        var session = await chatManager.CreateSessionAsync();
        LoadSessions();
        SelectSession(session.Id);
        return session;
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is ChatSession session)
        {
            SelectSession(session.Id);
        }
    }

    private void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        CreateNewSession();
    }

    private Agent? CreateAgent(ChatSession session)
    {
        var provider = ProviderManager.Instance.CreateActiveProvider();
        if (provider == null)
        {
            StatusText.Text = "请先在设置中配置 LLM 供应商";
            return null;
        }
        var promptBuilder = new DefaultSystemPromptBuilder();
        return new Agent(provider, toolRegistry, promptBuilder, session);
    }

    private void LoadMessages(ChatSession session)
    {
        MessagePanel.Children.Clear();
        chatManager.LoadSessionMessages(session);
        if (session.Messages == null) return;
        foreach (var msg in session.Messages)
        {
            if (msg.Role == "system") continue;
            AddMessageBubble(msg.Role, msg.Content ?? "", msg.ToolCalls);
        }
        ScrollToBottom();
    }

    private void AddMessageBubble(string role, string content, IList<ToolCallInfo>? toolCalls = null)
    {
        var bubble = new MessageBubble();
        bubble.SetMessage(role, content);
        MessagePanel.Children.Add(bubble);
        if (toolCalls != null)
        {
            foreach (var tc in toolCalls)
            {
                var card = new ToolCallCard();
                card.SetToolCall(tc.Name, tc.Arguments, tc.Result, tc.IsError);
                MessagePanel.Children.Add(card);
            }
        }
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendMessage();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendMessage();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text) && !isStreaming;
    }

    private async Task SendMessage()
    {
        if (currentSession == null || currentAgent == null || isStreaming) return;
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputBox.Text = "";
        AddMessageBubble("user", text);
        var userMsg = new ChatMessage { Role = "user", Content = text };
        currentSession.Messages.Add(userMsg);
        await chatManager.SaveSessionMessagesAsync(currentSession);
        ScrollToBottom();

        isStreaming = true;
        SendBtn.IsEnabled = false;
        StatusText.Text = "思考中...";

        var assistantBubble = new MessageBubble();
        assistantBubble.SetMessage("assistant", "");
        assistantBubble.SetStreaming(true);
        MessagePanel.Children.Add(assistantBubble);
        ScrollToBottom();

        try
        {
            await foreach (var chunk in currentAgent.RunAsync(text))
            {
                if (chunk.Text != null) assistantBubble.AppendContent(chunk.Text);
                if (chunk.ToolCall != null)
                {
                    var card = new ToolCallCard();
                    card.SetToolCall(chunk.ToolCall.Name, chunk.ToolCall.Arguments, null, false);
                    MessagePanel.Children.Add(card);
                }
                ScrollToBottom();
            }
            await chatManager.SaveSessionMessagesAsync(currentSession);
        }
        catch (Exception ex)
        {
            assistantBubble.AppendContent("\n[错误: " + ex.Message + "]");
        }

        assistantBubble.SetStreaming(false);
        isStreaming = false;
        SendBtn.IsEnabled = true;
        StatusText.Text = "就绪";
        ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        MessageScroller.Dispatcher.BeginInvoke(() => MessageScroller.ScrollToEnd());
    }
}
