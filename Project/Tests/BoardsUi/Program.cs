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
        VerifyLeaseRefresh(view, registration, column);
        VerifyDetailEditUsesOpeningSnapshot(store, registration, column);
        VerifyConfirmationDefaults(registration, column, card);
        VerifyAttachmentPath(registration, column, card);
        VerifyConfirmCancelDoesNotSend(store, registration, column);
        VerifyAgentActions(registration, column, card);
        VerifyThemeAndLanguageResources();

        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
        Assert(!GetLeaseTimer(view).IsEnabled, "The board lease timer continued after the view was unloaded.");
        Application.Current?.Shutdown();
        Console.WriteLine("Board UI checks passed.");
        return 0;
    }

    private static void VerifyLeaseRefresh(BoardsView view, ProjectBoardRegistration registration, ProjectBoardColumn column)
    {
        DateTime now = DateTime.UtcNow;
        Guid boardId = registration.BoardId;
        var claim = new ProjectBoardClaim("other-host", "other-user", "session-1", now, now, now.AddMilliseconds(300));
        var card = CreateCard(Guid.NewGuid(), column.Id, "Lease expiry");
        card = card with { Status = "inProgress", Claim = claim };
        var board = CreateBoard(boardId, column, card, 10);
        typeof(BoardsView).GetField("board", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, board);
        typeof(BoardsView).GetMethod("RenderBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);

        DispatcherTimer boardTimer = GetLeaseTimer(view);
        Assert(boardTimer.IsEnabled, "The board lease timer did not start when the view loaded.");
        boardTimer.Stop();
        boardTimer.Interval = TimeSpan.FromMilliseconds(10);
        boardTimer.Start();
        var leaseLabels = (System.Collections.IList)typeof(BoardsView).GetField("leaseLabels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        object leaseEntry = leaseLabels[0]!;
        var label = (TextBlock)leaseEntry.GetType().GetField("Item1")!.GetValue(leaseEntry)!;
        Assert(!label.Text.Contains("expired", StringComparison.OrdinalIgnoreCase), "The root card lease was already expired before the timer test began.");
        PumpDispatcher(TimeSpan.FromMilliseconds(500));
        Assert(label.Text.Contains("expired", StringComparison.OrdinalIgnoreCase), "The root card lease label did not change to expired after timer ticks.");
        boardTimer.Stop();
        boardTimer.Interval = TimeSpan.FromSeconds(30);
        boardTimer.Start();

        DateTime detailNow = DateTime.UtcNow;
        ProjectBoardCard detailCard = card with
        {
            Claim = claim with { ClaimedAt = detailNow, HeartbeatAt = detailNow, ExpiresAt = detailNow.AddMinutes(5) }
        };
        ProjectBoard detailBoard = CreateBoard(boardId, column, detailCard, 11);
        var details = new BoardCardDetailsWindow(null, detailBoard, registration, detailCard,
            new(boardId.ToString("D"), detailBoard.Revision, detailBoard.CommentRevision));
        DispatcherTimer detailTimer = GetLeaseTimer(details);
        detailTimer.Interval = TimeSpan.FromMilliseconds(10);
        TextBlock previousSummary = (TextBlock)typeof(BoardCardDetailsWindow).GetField("summaryText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
        try
        {
            details.Show();
            ToolBox.App.RaiseLanguageChanged();
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            TextBlock refreshedSummary = (TextBlock)typeof(BoardCardDetailsWindow).GetField("summaryText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
            Assert(!ReferenceEquals(previousSummary, refreshedSummary), "The language change reused a summary TextBlock with an existing logical parent.");
            var summary = (TextBlock)typeof(BoardCardDetailsWindow).GetField("claimSummaryText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
            Assert(!summary.Text.Contains("expired", StringComparison.OrdinalIgnoreCase), "The active detail claim was shown as expired before its deadline.");
            VerifyRootParentSelector(details);
            var actionRow = (Panel)typeof(BoardCardDetailsWindow).GetField("statusActionRow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
            Assert(actionRow.Children.Count == 0, "An active foreign claim exposed a claim action before its deadline.");
            ProjectBoardCard expiredCard = detailCard with { Claim = detailCard.Claim! with { ExpiresAt = DateTime.UtcNow.AddSeconds(-1) } };
            typeof(BoardCardDetailsWindow).GetField("cards", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(details, new[] { expiredCard });
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(summary.Text.Contains("expired", StringComparison.OrdinalIgnoreCase), "The details lease summary did not change to expired after a timer tick.");
            Button[] actions = actionRow.Children.OfType<Button>().ToArray();
            Assert(actions.Length == 1, "An expired foreign claim did not expose only the claim action.");
        }
        finally { details.Close(); }
        Assert(!detailTimer.IsEnabled, "The details lease timer continued after the window closed.");
    }

    private static DispatcherTimer GetLeaseTimer(object owner) => (DispatcherTimer)owner.GetType()
        .GetField("leaseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = duration };
        timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
    }

    private static void VerifyRootParentSelector(BoardCardDetailsWindow details)
    {
        var selectionPanel = (StackPanel)typeof(BoardCardDetailsWindow).GetField("selectionPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
        ComboBox selector = FindVisualChildren<ComboBox>(selectionPanel).Single();
        Assert(selector.SelectedItem != null, "A root card's parent selector did not show its No parent choice.");
        object choice = selector.SelectedItem!;
        Assert(choice.GetType().GetProperty("Id")?.GetValue(choice) == null, "A root card's parent selector did not select the null-parent choice.");
    }

    private static void VerifyDetailEditUsesOpeningSnapshot(ProjectBoardStore store, ProjectBoardRegistration registration, ProjectBoardColumn column)
    {
        ProjectBoardCard original = CreateCard(Guid.NewGuid(), column.Id, "Original detail") with { DueDate = DateTime.UtcNow.Date.AddDays(1) };
        ProjectBoard board = CreateBoard(registration.BoardId, column, original, 20) with { CommentRevision = 4 };
        ProjectBoard externalBoard = board with { Revision = 21, Cards = [original with { Title = "External update" }] };
        ProjectBoardCard edited = original with
        {
            Title = "Edited card",
            Description = "Edited description",
            Priority = 2,
            Tags = ["edited"],
            DueDate = null,
            UpdatedAt = DateTime.UtcNow
        };
        Func<TaskRequest, TaskResponse>? priorHandler = store.RequestHandler;
        ProjectBoard? priorBoard = store.CurrentBoard;
        var dialogs = new SimulatedBoardDialogs();
        var details = new BoardCardDetailsWindow(null, board, registration, original,
            new(registration.BoardId.ToString("D"), board.Revision, board.CommentRevision), dialogs);
        store.CurrentBoard = board;
        store.RequestHandler = request => request.Operation == "board.card.update"
            ? Success(new { revision = 21, boardRevision = 21, card = edited })
            : priorHandler?.Invoke(request) ?? Success(null);
        dialogs.DuringEdit = () => PublishChangedDuringModal(store, details.Dispatcher, externalBoard);
        try
        {
            details.Show();
            var selectionPanel = (StackPanel)typeof(BoardCardDetailsWindow).GetField("selectionPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
            string[] beforeEditText = FindVisualChildren<TextBlock>(selectionPanel).Select(text => text.Text).ToArray();
            Assert(beforeEditText.Any(text => text.Contains("Priority", StringComparison.OrdinalIgnoreCase) && text.Contains("Important", StringComparison.OrdinalIgnoreCase)), "The detail summary did not show priority.");
            Assert(beforeEditText.Any(text => text.Contains("Tags", StringComparison.OrdinalIgnoreCase) && text.Contains("tag", StringComparison.OrdinalIgnoreCase)), "The detail summary did not show tags.");
            Assert(beforeEditText.Any(text => text.Contains("Due date", StringComparison.OrdinalIgnoreCase)), "The detail summary did not show the due date.");

            Task edit = (Task)typeof(BoardCardDetailsWindow).GetMethod("EditSelectedCardAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(details, [original])!;
            edit.GetAwaiter().GetResult();
            Assert(FindRevision(store, "board.card.update") == 20, "Detail edit used the refreshed revision instead of its pre-dialog snapshot.");
            Assert(FindProject(store, "board.card.update") == registration.BoardId.ToString("D"), "Detail edit changed projects while the editor was open.");
            using JsonDocument request = JsonDocument.Parse(store.Requests.Last(request => request.Operation == "board.card.update").Payload!.Value.GetRawText());
            Assert(request.RootElement.GetProperty("id").GetString() == original.Id.ToString("D"), "Detail edit changed card identity while the editor was open.");
            var cards = (IReadOnlyList<ProjectBoardCard>)typeof(BoardCardDetailsWindow).GetField("cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
            Assert(cards.Single(card => card.Id == original.Id).Title == "Edited card", "Detail edit did not update the selected card summary.");
        }
        finally
        {
            details.Close();
            store.RequestHandler = priorHandler;
            store.CurrentBoard = priorBoard;
        }
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
        var descendants = ((int Total, int Closed))typeof(BoardsView).GetMethod("CountDescendants", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [cards[0].Id])!;
        Assert(descendants.Total == depth - 1, "The card progress walk did not handle a 3,000-level tree.");
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

    private static void VerifyAgentActions(ProjectBoardRegistration registration, ProjectBoardColumn column, ProjectBoardCard card)
    {
        var agents = BoardAgentService.Instance;
        var details = new BoardCardDetailsWindow(null, CreateBoard(registration.BoardId, column, card, 1), registration, card,
            new(registration.BoardId.ToString("D"), 1, 0));
        var candidateRow = (Panel)typeof(BoardCardDetailsWindow).GetField("candidatePathRow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;
        var toggle = (CheckBox)typeof(BoardCardDetailsWindow).GetField("policyToggle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(details)!;

        agents.Hosts =
        [
            new BoardAgentHostInfo("codex", "Codex", "app", "C:\\Program Files\\Codex", null, "common-root", false, true, "C:\\Program Files\\Codex", false, true, null),
            new BoardAgentHostInfo("trae", "Trae", "app", "C:\\trae.exe", null, "uninstall-registry", true, false, null, false, true, "trae-cn")
        ];
        InvokeAsync(details, "RefreshCandidatePathsAsync", false).GetAwaiter().GetResult();
        Assert(candidateRow.Children.Count == 1, "Only hosts awaiting path confirmation should expose a confirm button.");
        var confirmButton = (Button)candidateRow.Children[0];
        string content = confirmButton.Content?.ToString() ?? "";
        Assert(content.Contains("Codex", StringComparison.Ordinal) && content.Contains("Program Files", StringComparison.Ordinal), "The confirm button did not name the host and candidate path.");
        Assert(candidateRow.Visibility == Visibility.Visible, "The candidate row stayed hidden while a confirmation was pending.");

        agents.Hosts = [];
        InvokeAsync(details, "RefreshCandidatePathsAsync", false).GetAwaiter().GetResult();
        Assert(candidateRow.Children.Count == 0 && candidateRow.Visibility == Visibility.Collapsed, "The candidate row did not hide without pending confirmations.");

        agents.DeliveryPolicy = "always";
        InvokeAsync(details, "RefreshDeliveryPolicyToggleAsync").GetAwaiter().GetResult();
        Assert(toggle.IsChecked == true, "The delivery policy toggle did not reflect the stored always policy.");
        toggle.IsChecked = false;
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert(agents.DeliveryPolicy == "confirm" && agents.DeliveryPolicySets.Contains("confirm"), "Unchecking the toggle did not persist the confirm policy.");
        agents.DeliveryPolicy = "confirm";
        agents.Hosts = [];
    }

    private static Task InvokeAsync(object target, string method, params object?[] args) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

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
            foreach (string key in new[] { "BgSecondaryBrush", "BgTertiaryBrush", "BgElevatedBrush", "TextPrimaryBrush", "TextTertiaryBrush", "AccentBrush", "SuccessBrush", "WarningSoftBrush", "ErrorBrush", "SuccessSoftBrush", "BorderBrush", "BorderDefaultBrush" })
                Assert(keys.Contains(key), $"{file} is missing the {key} resource used by the board UI.");
            VerifyStatusBadgeContrast(Path.Combine(theme, file));
        }
    }

    private static void VerifyStatusBadgeContrast(string themePath)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var colors = XDocument.Load(themePath).Descendants(presentation + "SolidColorBrush")
            .Where(element => element.Attribute(xaml + "Key") != null)
            .ToDictionary(element => element.Attribute(xaml + "Key")!.Value,
                element => (Color)ColorConverter.ConvertFromString(element.Attribute("Color")!.Value), StringComparer.Ordinal);
        Color foreground = colors["TextPrimaryBrush"];
        Color surface = colors["BgSecondaryBrush"];
        Assert(Contrast(foreground, Composite(colors["AccentSoftBrush"], surface)) >= 4.5, $"Status badge text contrast is too low in {Path.GetFileName(themePath)} over AccentSoftBrush.");
        Assert(Contrast(foreground, Composite(colors["WarningSoftBrush"], surface)) >= 4.5, $"Status badge text contrast is too low in {Path.GetFileName(themePath)} over WarningSoftBrush.");
        Assert(Contrast(foreground, colors["BgTertiaryBrush"]) >= 4.5, $"Status badge text contrast is too low in {Path.GetFileName(themePath)} over BgTertiaryBrush.");
        Assert(Contrast(foreground, Composite(colors["SuccessSoftBrush"], surface)) >= 4.5, $"Status badge text contrast is too low in {Path.GetFileName(themePath)} over SuccessSoftBrush.");
    }

    private static Color Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color first, Color second)
    {
        double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                double linear = value / 255d;
                return linear <= 0.04045 ? linear / 12.92 : Math.Pow((linear + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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
