using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Reflection;
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;
using ToolBox.Views.Boards;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = ProjectBoardStore.Instance;
        Guid boardId = Guid.NewGuid();
        Guid columnId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        var registration = new ProjectBoardRegistration(boardId, "Test board", "C:\\boards\\test");
        var column = new ProjectBoardColumn(columnId, "Todo", 0);
        var card = CreateCard(cardId, columnId, "Original");
        store.Registrations = [registration];
        store.CurrentBoard = CreateBoard(boardId, column, card, 1);
        store.Requests.Clear();
        store.RequestHandler = request => request.Operation switch
        {
            "project.list" => Success(store.Registrations.ToArray()),
            "board.get" => Success(store.CurrentBoard),
            _ => Success(null)
        };

        var dialogs = new SimulatedBoardDialogs();
        var view = new BoardsView(dialogs);
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
        Assert(ReadBoard(view)?.Revision == 1, "The initial board revision was not loaded.");

        dialogs.DuringEdit = () => PublishChangedDuringModal(store, view.Dispatcher,
            CreateBoard(boardId, column, CreateCard(cardId, columnId, "Changed by CLI"), 2));
        view.EditCardAsync(card).GetAwaiter().GetResult();
        Assert(ReadBoard(view)?.Revision == 2, "The Changed event did not refresh the board while the editor was open.");
        Assert(FindRevision(store, "board.card.update") == 1, "Card edit used the refreshed revision instead of the editor snapshot.");

        Guid switchedBoardId = Guid.NewGuid();
        dialogs.DuringEdit = () =>
        {
            store.Registrations = [new ProjectBoardRegistration(switchedBoardId, "Switched board", "C:\\boards\\switched")];
            PublishChangedDuringModal(store, view.Dispatcher,
                CreateBoard(switchedBoardId, column, CreateCard(cardId, columnId, "Changed again by CLI"), 3));
        };
        view.CreateCardAsync(columnId).GetAwaiter().GetResult();
        Assert(ReadBoard(view)?.Revision == 3, "The second Changed event did not refresh the board while the create dialog was open.");
        Assert(FindRevision(store, "board.card.create") == 2, "Card create used the refreshed revision instead of the dialog snapshot.");
        Assert(FindProject(store, "board.card.create") == boardId.ToString("D"), "Card create used the newly selected board instead of the dialog snapshot.");

        VerifyDeepTreeAndStatusActions(view, registration, columnId);
        VerifyConfirmationDefaults(registration, column, card);
        VerifyAttachmentPath(registration, column, card);
        VerifyConfirmCancelDoesNotSend(store, registration, column);
        VerifyThemeAndLanguageResources();

        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
        Application.Current?.Shutdown();
        Console.WriteLine("Board UI checks passed.");
        return 0;
    }

    private static void VerifyDeepTreeAndStatusActions(BoardsView view, ProjectBoardRegistration registration, Guid columnId)
    {
        const int depth = 3000;
        var cards = new ProjectBoardCard[depth];
        var now = DateTime.UtcNow;
        Guid boardId = Guid.NewGuid();
        for (int index = 0; index < cards.Length; index++)
            cards[index] = new ProjectBoardCard(Guid.NewGuid(), $"Card {index}", "", columnId, index, 0, [], null, now, now, null,
                index == 0 ? "notStarted" : "closed", index == 0 ? null : cards[index - 1].Id);
        var deepBoard = new ProjectBoard(2, boardId, "Deep board", 1, [new ProjectBoardColumn(columnId, "Todo", 0, "notStarted")], cards);
        typeof(BoardsView).GetField("board", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, deepBoard);
        int descendants = (int)typeof(BoardsView).GetMethod("CountDescendants", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [cards[0].Id])!;
        Assert(descendants == depth - 1, "The card progress walk did not handle a 3,000-level tree.");
        var roots = (List<ProjectBoardCard>)typeof(BoardsView).GetMethod("BoardCards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [columnId])!;
        Assert(roots.Count == 1 && roots[0].Id == cards[0].Id, "The board column rendered child cards as root cards.");

        var transition = typeof(BoardsView).GetMethod("GetStatusTransition", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert((string?)transition.Invoke(null, ["notStarted", "inProgress"]) == "claim", "Moving into the mapped in-progress column did not claim the card.");
        Assert((string?)transition.Invoke(null, ["inProgress", "pendingReview"]) == "submit", "Moving into the review column bypassed submit.");
        Assert((string?)transition.Invoke(null, ["pendingReview", "closed"]) == "close", "Moving into the closed column bypassed close.");
        Assert(transition.Invoke(null, ["notStarted", "closed"]) == null, "An invalid status move was allowed.");
        Assert((string?)transition.Invoke(null, ["inProgress", "closed"]) == null, "A drag bypassed the submit and close transitions.");
        var canClaim = typeof(BoardCardDetailsWindow).GetMethod("CanClaim", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var detailWindow = new BoardCardDetailsWindow(null, deepBoard, registration, cards[0], new(boardId.ToString("D"), 1, 0));
        Assert(!(bool)canClaim.Invoke(detailWindow, [new ProjectBoardClaim("other-host", "other-user", "session", now, now, now.AddMinutes(5))])!, "A live claim owned by another identity was considered claimable.");
        Assert((bool)canClaim.Invoke(detailWindow, [new ProjectBoardClaim("toolbox-ui", "local-user", null, now, now, now.AddMinutes(5))])!, "The local identity could not use its own live claim.");

        var comments = new ProjectBoardComment[depth];
        for (int index = 0; index < comments.Length; index++)
            comments[index] = new ProjectBoardComment(Guid.NewGuid(), index == 0 ? null : comments[index - 1].Id, $"Comment {index}",
                new("user", "local-user", "You"), null, null, now, now, null);
        var flatten = typeof(BoardCardDetailsWindow).GetMethod("FlattenComments", BindingFlags.Static | BindingFlags.NonPublic)!;
        var flattened = (System.Collections.IEnumerable)flatten.Invoke(null, [comments])!;
        Assert(flattened.Cast<object>().Count() == depth, "Comment reply flattening did not handle a 3,000-level thread.");
    }

    private static void VerifyConfirmationDefaults(ProjectBoardRegistration registration, ProjectBoardColumn column, ProjectBoardCard card)
    {
        var board = CreateBoard(registration.BoardId, column, card, 1);
        bool? dangerResult = null;
        var dangerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        dangerTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current!.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "Danger confirmation");
            if (dialog == null) return;
            var buttons = FindVisualChildren<Button>(dialog).ToArray();
            Assert(!buttons.Any(button => button.Content?.ToString() == "Delete" && button.IsDefault), "Danger confirmation made Delete the Enter default.");
            Assert(buttons.Any(button => button.IsCancel), "Danger confirmation has no Escape/Cancel action.");
            buttons.First(button => button.IsCancel).GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(buttons.First(button => button.IsCancel), null);
            dangerTimer.Stop();
        };
        dangerTimer.Start();
        dangerResult = ProjectBoardDialogs.Confirm(null, "Danger confirmation", "Delete this card?", "Delete", danger: true);
        dangerTimer.Stop();
        Assert(dangerResult == false, "Danger confirmation did not cancel by default.");

        var normalTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        normalTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current!.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "Normal confirmation");
            if (dialog == null) return;
            Assert(FindVisualChildren<Button>(dialog).Any(button => button.Content?.ToString() == "Continue" && button.IsDefault), "Ordinary confirmation did not make its action the Enter default.");
            FindVisualChildren<Button>(dialog).First(button => button.IsCancel).GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(FindVisualChildren<Button>(dialog).First(button => button.IsCancel), null);
            normalTimer.Stop();
        };
        normalTimer.Start();
        ProjectBoardDialogs.Confirm(null, "Normal confirmation", "Continue?", "Continue");
        normalTimer.Stop();
    }

    private static void VerifyAttachmentPath(ProjectBoardRegistration registration, ProjectBoardColumn column, ProjectBoardCard card)
    {
        var details = new BoardCardDetailsWindow(null, CreateBoard(registration.BoardId, column, card, 1), registration, card,
            new(registration.BoardId.ToString("D"), 1, 0));
        var attachmentPath = typeof(BoardCardDetailsWindow).GetMethod("AttachmentPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string relative = Path.Combine(".toolbox", "board-assets", "image.png");
        string expected = Path.GetFullPath(Path.Combine(registration.Path, relative));
        string actual = (string)attachmentPath.Invoke(details, [relative.Replace(Path.DirectorySeparatorChar, '/')])!;
        Assert(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), "Attachment paths were not resolved relative to the project root.");
        bool rejectedEscape = false;
        try { _ = attachmentPath.Invoke(details, ["../outside.png"]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejectedEscape = true; }
        Assert(rejectedEscape, "An attachment path outside the project directory was accepted.");
    }

    private static void VerifyConfirmCancelDoesNotSend(ProjectBoardStore store, ProjectBoardRegistration registration, ProjectBoardColumn column)
    {
        Guid id = Guid.NewGuid();
        var card = CreateCard(id, column.Id, "Reply target");
        var board = CreateBoard(registration.BoardId, column, card, 5) with { CommentRevision = 7 };
        var details = new BoardCardDetailsWindow(null, board, registration, card, new(registration.BoardId.ToString("D"), 5, 7));
        var comment = new ProjectBoardComment(Guid.NewGuid(), null, "Reply body", new("user", "local-user", "You"), null,
            new("codex", "session-1"), DateTime.UtcNow, DateTime.UtcNow, null);
        Func<TaskRequest, TaskResponse>? priorHandler = store.RequestHandler;
        store.RequestHandler = request => request.Operation == "board.comment.get"
            ? Success(new ProjectBoardCommentContext(registration, id, comment, 5, 7))
            : priorHandler?.Invoke(request) ?? new TaskResponse();
        var delivery = new BoardAgentDelivery(comment.Id.ToString("D"), id.ToString("D"), registration.Path, "codex", "session-1",
            "needsConfirmation", "confirmation-hash", null, DateTimeOffset.UtcNow);
        int deliveryRequests = store.Requests.Count(request => request.Operation == "board.comment.deliver");
        string? clipboardBefore = ReadClipboardText();
        details.Show();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            var dialog = Application.Current!.Windows.OfType<Window>().FirstOrDefault(w => w != details && w.IsVisible && w.Title == "Confirm reply delivery");
            var cancel = dialog == null ? null : FindVisualChildren<Button>(dialog).FirstOrDefault(button => button.IsCancel);
            if (cancel != null)
            {
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cancel, null);
                timer.Stop();
            }
            else if (ticks >= 50)
            {
                timer.Stop();
                dialog?.Close();
            }
        };
        timer.Start();
        details.ConfirmAndDeliverAsync(comment, delivery, unknown: false).GetAwaiter().GetResult();
        timer.Stop();
        Assert(ticks < 50, "The delivery confirmation dialog was not found by the cancel test.");
        Assert(store.Requests.Count(request => request.Operation == "board.comment.deliver") == deliveryRequests, "Canceling delivery still sent a delivery command.");
        Assert(ReadClipboardText() == clipboardBefore, "Canceling delivery changed the clipboard.");
        details.Close();
        store.RequestHandler = priorHandler;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private static string? ReadClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    private static void VerifyThemeAndLanguageResources()
    {
        string root = FindWorkspaceRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> Keys(string path) => XDocument.Load(path).Descendants().Attributes(x + "Key")
            .Select(attribute => attribute.Value).Where(key => key.StartsWith("Lang_Board_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        string theme = Path.Combine(root, "Project", "Themes");
        var zh = Keys(Path.Combine(theme, "Lang zh-CN.xaml"));
        var en = Keys(Path.Combine(theme, "Lang en-US.xaml"));
        Assert(zh.SetEquals(en), "The Chinese and English board resource keys do not match.");
        HashSet<string> ThemeKeys(string path) => XDocument.Load(path).Descendants().Attributes(x + "Key").Select(attribute => attribute.Value).ToHashSet(StringComparer.Ordinal);
        foreach (string file in new[] { "Light.xaml", "Dark.xaml" })
        {
            var keys = ThemeKeys(Path.Combine(theme, file));
            foreach (string key in new[] { "BgSecondaryBrush", "BgTertiaryBrush", "BgElevatedBrush", "TextPrimaryBrush", "TextTertiaryBrush", "AccentBrush", "SuccessBrush", "WarningSoftBrush", "ErrorBrush", "BorderBrush", "BorderDefaultBrush" })
                Assert(keys.Contains(key), $"{file} is missing the {key} resource used by the board UI.");
        }
    }

    private static string FindWorkspaceRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? current = new(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Project", "Themes", "Lang zh-CN.xaml"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not find the ToolBox workspace root for resource checks.");
    }

    private static TaskResponse Success(object? data) => new() { Ok = true, Data = data };

    private static ProjectBoard CreateBoard(Guid boardId, ProjectBoardColumn column, ProjectBoardCard card, long revision) =>
        new(1, boardId, "Test board", revision, [column], [card]);

    private static ProjectBoardCard CreateCard(Guid cardId, Guid columnId, string title)
    {
        var now = DateTime.UtcNow;
        return new ProjectBoardCard(cardId, title, "Description", columnId, 0, 1, ["tag"], null, now, now, null);
    }

    private static void PublishChangedDuringModal(ProjectBoardStore store, Dispatcher dispatcher, ProjectBoard nextBoard)
    {
        store.CurrentBoard = nextBoard;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(store.RaiseChanged));
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static long FindRevision(ProjectBoardStore store, string operation)
    {
        TaskRequest request = store.Requests.LastOrDefault(r => r.Operation == operation)
            ?? throw new InvalidOperationException($"No request was recorded for {operation}.");
        using JsonDocument document = JsonDocument.Parse(request.Payload?.GetRawText() ?? throw new InvalidOperationException("The request had no payload."));
        return document.RootElement.GetProperty("revision").GetInt64();
    }

    private static string FindProject(ProjectBoardStore store, string operation)
    {
        TaskRequest request = store.Requests.LastOrDefault(r => r.Operation == operation)
            ?? throw new InvalidOperationException($"No request was recorded for {operation}.");
        using JsonDocument document = JsonDocument.Parse(request.Payload?.GetRawText() ?? throw new InvalidOperationException("The request had no payload."));
        return document.RootElement.GetProperty("project").GetString() ?? "";
    }

    private static ProjectBoard? ReadBoard(BoardsView view) =>
        typeof(BoardsView).GetField("board", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(view) as ProjectBoard;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SimulatedBoardDialogs : IBoardDialogService
    {
        public Action? DuringEdit { get; set; }

        public BoardCardDraft? EditCard(Window? owner, string title, ProjectBoardCard? card)
        {
            DuringEdit?.Invoke();
            return new BoardCardDraft(card == null ? "New card" : "Edited card", "Edited description", 2, ["edited"], null);
        }

        public string? PromptText(Window? owner, string title, string prompt, string initialValue = "") => "Changed title";

        public Guid? ChooseColumn(Window? owner, string title, IReadOnlyList<ProjectBoardColumn> columns) => columns.FirstOrDefault()?.Id;
    }
}
