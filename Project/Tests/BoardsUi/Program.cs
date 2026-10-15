using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;
using ToolBox.Views.Boards;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
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

        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, view));
        Console.WriteLine("Board mutation snapshots passed.");
        return 0;
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
