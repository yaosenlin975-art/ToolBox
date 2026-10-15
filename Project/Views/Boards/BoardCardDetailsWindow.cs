using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Boards;

internal readonly record struct BoardDetailsSnapshot(string BoardId, long Revision, long CommentRevision);

internal sealed class BoardCardDetailsWindow : Window
{
    private const string LocalActor = "local-user";
    private const string LocalHost = "toolbox-ui";
    private readonly IBoardDialogService dialogService;
    private readonly BoardCommandService commands = BoardCommandService.Instance;
    private readonly BoardAgentService agents = BoardAgentService.Instance;
    private readonly ProjectBoardStore store = ProjectBoardStore.Instance;
    private readonly ProjectBoardRegistration registration;
    private readonly ProjectBoard initialBoard;
    private readonly Guid rootCardId;
    private readonly StackPanel treePanel = new();
    private readonly StackPanel selectionPanel = new();
    private readonly StackPanel attachmentPanel = new();
    private readonly StackPanel commentPanel = new();
    private readonly TextBlock errorText = new();
    private readonly TextBlock statusText = new();
    private TextBlock summaryText = new();
    private readonly DispatcherTimer leaseTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<Guid> collapsedCards = [];
    private IReadOnlyList<ProjectBoardCard> cards;
    private IReadOnlyList<ProjectBoardComment> comments = [];
    private IReadOnlyList<BoardAttachmentView> attachments = [];
    private readonly Dictionary<Guid, BoardAgentDelivery?> deliveries = [];
    private BoardDetailsSnapshot snapshot;
    private Guid activeCardId;
    private bool loading;
    private bool loaded;
    private bool mutationInFlight;
    private bool selectionLoading = true;
    private TextBlock? claimSummaryText;
    private WrapPanel? statusActionRow;

    public BoardCardDetailsWindow(Window? owner, ProjectBoard board, ProjectBoardRegistration registration, ProjectBoardCard card, BoardDetailsSnapshot snapshot, IBoardDialogService? dialogService = null)
    {
        Owner = owner;
        this.dialogService = dialogService ?? new WpfBoardDialogService();
        this.registration = registration;
        initialBoard = board;
        cards = board.Cards;
        rootCardId = card.Id;
        activeCardId = card.Id;
        this.snapshot = snapshot;
        Title = card.Title;
        Width = 960;
        Height = 780;
        MinWidth = 780;
        MinHeight = 600;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgBrush");
        BuildContent(card);
        leaseTimer.Tick += (_, _) => RefreshLeaseUi();
        Loaded += OnLoaded;
        Closed += (_, _) => { leaseTimer.Stop(); store.Changed -= StoreChanged; agents.Changed -= AgentChanged; App.LanguageChanged -= LanguageChanged; };
    }

    private void BuildContent(ProjectBoardCard selected)
    {
        var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        shell.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader());

        var columns = new Grid { Margin = new Thickness(16, 8, 16, 12) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(selectionPanel);
        left.Children.Add(Section("Lang_Board_Subtasks", "Subtasks"));
        var treeScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 530, Content = treePanel };
        left.Children.Add(treeScroll);
        var right = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        right.Children.Add(Section("Lang_Board_Images", "Images"));
        var attachmentScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 230, Content = attachmentPanel };
        right.Children.Add(attachmentScroll);
        right.Children.Add(Section("Lang_Board_Comments", "Comments"));
        var commentScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 355, Content = commentPanel };
        right.Children.Add(commentScroll);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        var footer = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        statusText.TextWrapping = TextWrapping.Wrap;
        statusText.Margin = new Thickness(0, 0, 0, 4);
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        footer.Children.Add(statusText);
        errorText.TextWrapping = TextWrapping.Wrap;
        errorText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        footer.Children.Add(errorText);
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var config = Button("Lang_Board_AgentConfig", "Agent configuration", async (_, _) => await OpenAgentConfigAsync());
        footerButtons.Children.Add(config);
        var close = Button("Lang_Board_Close", "Close", (_, _) => Close(), "BtnPrimary");
        close.IsCancel = true;
        footerButtons.Children.Add(close);
        footer.Children.Add(footerButtons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        shell.Child = root;
        Content = shell;
        RenderTree();
        RenderSelection(selected);
    }

    private UIElement CreateHeader()
    {
        var grid = new Grid { Height = 46, Margin = new Thickness(6, 4, 6, 0), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label(initialBoard.Name, true);
        title.Margin = new Thickness(12, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(title);
        var close = Button("Lang_Board_Close", "Close", (_, _) => Close(), "IconButton");
        close.Content = "✕";
        close.Width = 30;
        close.Height = 30;
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        grid.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        return grid;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        leaseTimer.Start();
        store.Changed += StoreChanged;
        agents.Changed += AgentChanged;
        App.LanguageChanged += LanguageChanged;
        _ = LoadDetailsAsync();
    }

    private void StoreChanged()
    {
        if (mutationInFlight) return;
        Dispatcher.BeginInvoke(new Action(() => statusText.Text = Text("Lang_Board_DetailsSnapshotHint", "Board changed while this window is open. Save actions use its opening revision; close and reopen after a conflict.")));
    }

    private void AgentChanged() => Dispatcher.BeginInvoke(new Action(() => _ = RefreshDeliveryStatusesAsync()));
    private void LanguageChanged() => Dispatcher.BeginInvoke(new Action(() => { RenderTree(); RenderSelection(FindCard(activeCardId) ?? FindCard(rootCardId)!); RenderAttachments(); RenderComments(); }));

    private async Task LoadDetailsAsync()
    {
        if (loading) return;
        loading = true;
        selectionLoading = true;
        Guid selectedId = activeCardId;
        BoardDetailsSnapshot captured = snapshot;
        try
        {
            ProjectBoardCard selected = FindCard(selectedId) ?? throw new InvalidOperationException("The selected card is no longer available.");
            var loadedComments = await LoadCommentsAsync(selectedId, captured);
            var loadedAttachments = await LoadAttachmentsAsync(selected, captured);
            var loadedDeliveries = await LoadDeliveriesAsync(loadedComments, captured);
            if (selectedId != activeCardId || captured != snapshot) return;
            comments = loadedComments;
            attachments = loadedAttachments;
            ReplaceDeliveries(loadedDeliveries);
            RenderTree();
            RenderSelection(FindCard(selectedId) ?? FindCard(rootCardId)!);
            RenderAttachments();
            RenderComments();
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            loading = false;
            if (selectedId == activeCardId)
            {
                selectionLoading = false;
                RenderTree();
                RenderAttachments();
                RenderComments();
            }
        }
    }

    private void RenderTree()
    {
        treePanel.Children.Clear();
        var byParent = cards.Where(c => c.DeletedAt == null).GroupBy(c => c.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).ThenBy(c => c.Id).ToArray());
        var pending = new Stack<(ProjectBoardCard Card, int Depth)>();
        if (FindCard(rootCardId) is { DeletedAt: null } root) pending.Push((root, 0));
        var seen = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var (card, depth) = pending.Pop();
            if (!seen.Add(card.Id)) continue;
            bool hasChildren = byParent.TryGetValue(card.Id, out var children) && children.Length > 0;
        var row = new Grid { Margin = new Thickness(Math.Min(depth, 8) * 14, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var toggle = Button(null, hasChildren ? collapsedCards.Contains(card.Id) ? "+" : "−" : "·", (_, _) =>
            {
                if (!hasChildren) return;
                if (!collapsedCards.Add(card.Id)) collapsedCards.Remove(card.Id);
                RenderTree();
            });
            toggle.Width = 24;
            toggle.Padding = new Thickness(2);
            row.Children.Add(toggle);
            var select = Button(null, card.Title, (_, _) => SelectCard(card));
            select.HorizontalContentAlignment = HorizontalAlignment.Left;
            select.Padding = new Thickness(8, 4, 8, 4);
            if (card.Id == activeCardId) select.SetResourceReference(Control.BackgroundProperty, "AccentSoftBrush");
            Grid.SetColumn(select, 1);
            row.Children.Add(select);
            var badge = SmallStatus(card.Status);
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
            treePanel.Children.Add(row);
            if (!collapsedCards.Contains(card.Id) && hasChildren)
                for (int index = children!.Length - 1; index >= 0; index--) pending.Push((children[index], depth + 1));
        }
        var addChild = Button("Lang_Board_AddSubtask", "Add subtask", async (_, _) => await AddSubtaskAsync(), "BtnPrimary");
        addChild.Margin = new Thickness(4, 8, 4, 0);
        addChild.IsEnabled = !selectionLoading && FindCard(activeCardId)?.Status != "closed";
        if (FindCard(activeCardId)?.Status == "closed") addChild.ToolTip = Text("Lang_Board_ClosedParentHint", "Closed cards cannot contain open subtasks.");
        treePanel.Children.Add(addChild);
    }

    private void RenderSelection(ProjectBoardCard card)
    {
        activeCardId = card.Id;
        Title = card.Title;
        summaryText = new TextBlock { Text = string.Format(Text("Lang_Board_ActiveCardSummary", "{0} · {1}"), card.Title, StatusText(card.Status)) };
        var details = new StackPanel();
        details.Children.Add(summaryText);
        claimSummaryText = null;
        if (card.Claim is { } claim)
        {
            claimSummaryText = Label(ClaimSummary(claim), tertiary: true);
            details.Children.Add(claimSummaryText);
        }
        var actionRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        statusActionRow = new WrapPanel();
        AddStatusActions(statusActionRow, card);
        actionRow.Children.Add(statusActionRow);
        actionRow.Children.Add(Button("Lang_Board_EditCard", "Edit", async (_, _) => await EditSelectedCardAsync(card)));
        actionRow.Children.Add(Button("Lang_Board_DeleteCard", "Move to recycle bin", async (_, _) => await DeleteCardAsync(card)));
        var parentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        var parentSelector = new ComboBox { MinWidth = 210, Height = 32, DisplayMemberPath = nameof(ParentChoice.Label), SelectedValuePath = nameof(ParentChoice.Id) };
        var choices = GetParentChoices(card);
        parentSelector.ItemsSource = choices;
        parentSelector.SelectedItem = choices.FirstOrDefault(choice => choice.Id == card.ParentId);
        parentRow.Children.Add(parentSelector);
        var saveParent = Button("Lang_Board_ChangeParent", "Change parent", async (_, _) =>
        {
            Guid? parentId = parentSelector.SelectedValue is Guid value ? value : null;
            await ChangeParentAsync(card, parentId);
        });
        saveParent.Margin = new Thickness(6, 0, 0, 0);
        parentRow.Children.Add(saveParent);
        details.Children.Add(actionRow);
        details.Children.Add(parentRow);
        var body = Label(card.Description);
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 0, 0, 10);
        details.Children.Add(body);
        string[] priorities = [Text("Lang_Board_Normal", "Normal"), Text("Lang_Board_Important", "Important"), Text("Lang_Board_Urgent", "Urgent")];
        details.Children.Add(Label($"{Text("Lang_Board_Priority", "Priority")}: {priorities[Math.Clamp(card.Priority, 0, priorities.Length - 1)]}", tertiary: true));
        if (card.Tags.Count > 0) details.Children.Add(Label($"{Text("Lang_Board_Tags", "Tags")}: {string.Join(" · ", card.Tags)}", tertiary: true));
        if (card.DueDate.HasValue) details.Children.Add(Label($"{Text("Lang_Board_DueDate", "Due date")}: {card.DueDate.Value.ToLocalTime():d}", tertiary: true));
        selectionPanel.Children.Clear();
        selectionPanel.Children.Add(details);
        RenderTree();
    }

    private async Task LoadSelectedCardAsync(Guid id)
    {
        BoardDetailsSnapshot captured = snapshot;
        try
        {
            ProjectBoardCard? card = FindCard(id);
            if (card == null) return;
            var loadedAttachments = await LoadAttachmentsAsync(card, captured);
            var loadedComments = await LoadCommentsAsync(card.Id, captured);
            var loadedDeliveries = await LoadDeliveriesAsync(loadedComments, captured);
            if (activeCardId != id || captured != snapshot) return;
            attachments = loadedAttachments;
            comments = loadedComments;
            ReplaceDeliveries(loadedDeliveries);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (activeCardId == id)
            {
                selectionLoading = false;
                RenderTree();
                RenderAttachments();
                RenderComments();
            }
        }
    }

    private void SelectCard(ProjectBoardCard card)
    {
        activeCardId = card.Id;
        selectionLoading = true;
        attachments = [];
        comments = [];
        deliveries.Clear();
        RenderSelection(card);
        RenderAttachments();
        RenderComments();
        _ = LoadSelectedCardAsync(card.Id);
    }

    private void AddStatusActions(Panel panel, ProjectBoardCard card)
    {
        string action = card.Status switch
        {
            "notStarted" when CanClaim(card.Claim) => "claim",
            "inProgress" when IsOwnedActiveClaim(card.Claim) => "release",
            "pendingReview" => "close",
            "closed" => "reopen",
            _ => ""
        };
        if (card.Status == "inProgress" && IsOwnedActiveClaim(card.Claim))
        {
            panel.Children.Add(Button("Lang_Board_Submit", "Submit for review", (_, _) => _ = ApplyStatusAsync(card, "submit"), "BtnPrimary"));
        }
        if (!string.IsNullOrEmpty(action)) panel.Children.Add(Button(StatusKey(action), StatusFallback(action), (_, _) => _ = ApplyStatusAsync(card, action), "BtnDefault"));
        if (card.Status == "pendingReview") panel.Children.Add(Button("Lang_Board_Reopen", "Reopen", (_, _) => _ = ApplyStatusAsync(card, "reopen")));
        if (card.Status == "inProgress" && IsClaimExpired(card.Claim)) panel.Children.Add(Button("Lang_Board_Claim", "Claim", (_, _) => _ = ApplyStatusAsync(card, "claim"), "BtnPrimary"));
    }

    private void RefreshLeaseUi()
    {
        if (!loaded || Dispatcher.HasShutdownStarted || FindCard(activeCardId) is not { } card) return;
        if (card.Claim is { } claim && claimSummaryText != null) claimSummaryText.Text = ClaimSummary(claim);
        if (statusActionRow == null) return;
        statusActionRow.Children.Clear();
        AddStatusActions(statusActionRow, card);
    }

    private async Task EditSelectedCardAsync(ProjectBoardCard card)
    {
        BoardDetailsSnapshot captured = snapshot;
        Guid capturedCardId = card.Id;
        BoardCardDraft? draft = dialogService.EditCard(this, Text("Lang_Board_CardEditTitle", "Edit board card"), card);
        if (draft == null || captured.BoardId != snapshot.BoardId) return;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["project"] = captured.BoardId,
                ["revision"] = captured.Revision,
                ["id"] = capturedCardId.ToString("D"),
                ["title"] = draft.Title,
                ["description"] = draft.Description,
                ["priority"] = draft.Priority,
                ["tags"] = draft.Tags,
                ["dueDate"] = draft.DueDate,
                ["actor"] = LocalActor,
                ["host"] = LocalHost,
                ["session"] = null
            };
            JsonElement data = await ExecuteDataAsync("board.card.update", payload);
            AcceptMutation(data, isComment: false);
            ProjectBoardCard updated = ReadCard(data) ?? card with
            {
                Title = draft.Title,
                Description = draft.Description,
                Priority = draft.Priority,
                Tags = draft.Tags,
                DueDate = draft.DueDate,
                UpdatedAt = DateTime.UtcNow
            };
            cards = cards.Select(existing => existing.Id == capturedCardId ? updated : existing).ToArray();
            RenderTree();
            RenderSelection(updated);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ApplyStatusAsync(ProjectBoardCard card, string action)
    {
        if (action == "claim" && !CanClaim(card.Claim))
        {
            ShowError(new InvalidOperationException(Text("Lang_Board_ClaimHeld", "This card has another active claim.")));
            return;
        }
        try
        {
            var payload = BasePayload(card);
            payload["actor"] = LocalActor;
            payload["host"] = LocalHost;
            payload["session"] = null;
            if (action == "claim") payload["ttlMinutes"] = 30;
            AddMappedColumn(payload, card, action switch { "claim" => "inProgress", "release" or "reopen" => "notStarted", "submit" => "pendingReview", "close" => "closed", _ => card.Status });
            JsonElement data = await ExecuteDataAsync($"board.card.{action}", payload);
            AcceptMutation(data, isComment: false);
            UpdateCardFromMutation(data);
            RenderTree();
            RenderSelection(FindCard(card.Id) ?? card);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task AddSubtaskAsync()
    {
        ProjectBoardCard parent = FindCard(activeCardId)!;
        BoardDetailsSnapshot captured = snapshot;
        var draft = ProjectBoardDialogs.EditCard(this, Text("Lang_Board_CardAddTitle", "New board card"), null);
        if (draft == null || captured.BoardId != snapshot.BoardId) return;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["project"] = captured.BoardId, ["revision"] = captured.Revision, ["columnId"] = parent.ColumnId,
                ["parentId"] = parent.Id, ["title"] = draft.Title, ["description"] = draft.Description,
                ["priority"] = draft.Priority, ["tags"] = draft.Tags, ["dueDate"] = draft.DueDate,
                ["actor"] = LocalActor, ["host"] = LocalHost, ["session"] = null
            };
            JsonElement data = await ExecuteDataAsync("board.card.create", payload);
            AcceptMutation(data, isComment: false);
            if (ReadCard(data) is { } created) { cards = cards.Append(created).ToArray(); activeCardId = created.Id; }
            RenderTree();
            if (FindCard(activeCardId) is { } next) { RenderSelection(next); _ = LoadSelectedCardAsync(next.Id); }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ChangeParentAsync(ProjectBoardCard card, Guid? parentId)
    {
        if (card.ParentId == parentId) return;
        try
        {
            var payload = BasePayload(card);
            payload["id"] = card.Id;
            payload["parentId"] = parentId;
            payload["actor"] = LocalActor;
            payload["host"] = LocalHost;
            payload["session"] = null;
            JsonElement data = await ExecuteDataAsync("board.card.update", payload);
            AcceptMutation(data, isComment: false);
            UpdateCardFromMutation(data);
            RenderTree();
            RenderSelection(FindCard(card.Id) ?? card);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task DeleteCardAsync(ProjectBoardCard card)
    {
        BoardDetailsSnapshot captured = snapshot;
        if (!ProjectBoardDialogs.Confirm(this, Text("Lang_Board_DeleteCard", "Move to recycle bin"),
            string.Format(Text("Lang_Board_DeleteSubtreePrompt", "Move “{0}” and its subtasks to the recycle bin?"), card.Title),
            Text("Lang_Board_DeleteCard", "Move to recycle bin"), danger: true)) return;
        try
        {
            JsonElement data = await ExecuteDataAsync("board.card.delete", new
            {
                project = captured.BoardId, revision = captured.Revision, id = card.Id,
                actor = LocalActor, host = LocalHost, session = (string?)null
            });
            AcceptMutation(data, isComment: false);
            Close();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private IReadOnlyList<ParentChoice> GetParentChoices(ProjectBoardCard card)
    {
        var blocked = DescendantIds(card.Id);
        return [new ParentChoice(null, Text("Lang_Board_NoParent", "No parent")), .. cards.Where(c => c.DeletedAt == null && c.Status != "closed" && c.Id != card.Id && !blocked.Contains(c.Id))
            .OrderBy(c => c.Title, StringComparer.CurrentCulture).Select(c => new ParentChoice(c.Id, c.Title))];
    }

    private HashSet<Guid> DescendantIds(Guid cardId)
    {
        var children = cards.Where(c => c.DeletedAt == null && c.ParentId.HasValue).GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>(children.TryGetValue(cardId, out var roots) ? roots : []);
        while (pending.Count > 0)
        {
            Guid id = pending.Pop();
            if (!result.Add(id)) continue;
            if (children.TryGetValue(id, out var next)) foreach (Guid child in next) pending.Push(child);
        }
        return result;
    }

    private async Task<IReadOnlyList<BoardAttachmentView>> LoadAttachmentsAsync(ProjectBoardCard card, BoardDetailsSnapshot? capturedSnapshot = null)
    {
        BoardDetailsSnapshot captured = capturedSnapshot ?? snapshot;
        JsonElement data = await ExecuteDataAsync("board.attachment.list", new { project = captured.BoardId, id = card.Id });
        CheckReadSnapshot(data, captured, isComment: false);
        if (data.ValueKind == JsonValueKind.Array) return data.Deserialize<BoardAttachmentView[]>(TaskProtocol.Options) ?? [];
        return GetArray(data, "attachments").Select(element => element.Deserialize<BoardAttachmentView>(TaskProtocol.Options)!).Where(item => item != null).ToArray();
    }

    private void RenderAttachments()
    {
        attachmentPanel.Children.Clear();
        foreach (var item in attachments)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            string path = "";
            bool invalidPath = false;
            try { path = AttachmentPath(item.RelativePath); }
            catch (InvalidDataException) { invalidPath = true; }
            bool exists = !invalidPath && (item.Exists ?? File.Exists(path));
            bool hashMatch = !invalidPath && (item.HashMatch ?? MatchesHash(path, item.Sha256));
            if (exists && hashMatch && TryLoadImage(path, out var image))
            {
                var preview = ProjectBoardDialogs.CreateButton("");
                preview.Content = new Image { Source = image, Width = 92, Height = 66, Stretch = Stretch.Uniform };
                preview.Padding = new Thickness(0);
                preview.Margin = new Thickness(0, 0, 8, 0);
                preview.Click += (_, _) => ShowImagePreview(item.FileName, path);
                row.Children.Add(preview);
            }
            else row.Children.Add(Label(Text(invalidPath ? "Lang_Board_ImagePathInvalid" : !exists ? "Lang_Board_ImageMissing" : !hashMatch ? "Lang_Board_ImageChanged" : "Lang_Board_ImageUnreadable",
                invalidPath ? "Image path is outside the project directory" : !exists ? "Image file is missing" : !hashMatch ? "Image hash does not match" : "Image cannot be opened"), tertiary: true));
            row.Children.Add(Label(item.FileName, tertiary: true));
            var remove = Button("Lang_Board_RemoveImage", "Remove", async (_, _) => await RemoveAttachmentAsync(item));
            remove.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(remove);
            attachmentPanel.Children.Add(row);
        }
        var add = Button("Lang_Board_AddImage", "Add image", async (_, _) => await AddAttachmentAsync(), "BtnDefault");
        add.IsEnabled = !selectionLoading;
        attachmentPanel.Children.Add(add);
    }

    private async Task AddAttachmentAsync()
    {
        BoardDetailsSnapshot captured = snapshot;
        Guid cardId = activeCardId;
        var dialog = new OpenFileDialog { Title = Text("Lang_Board_AddImage", "Add image"), Filter = Text("Lang_Board_ImageFilter", "Image files|*.png;*.jpg;*.jpeg;*.gif;*.bmp") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            JsonElement data = await ExecuteDataAsync("board.attachment.add", new
            {
                project = captured.BoardId, revision = captured.Revision, id = cardId, file = dialog.FileName,
                actor = LocalActor, host = LocalHost, session = (string?)null
            });
            AcceptMutation(data, isComment: false);
            attachments = await LoadAttachmentsAsync(FindCard(cardId)!, snapshot);
            RenderAttachments();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RemoveAttachmentAsync(BoardAttachmentView item)
    {
        BoardDetailsSnapshot captured = snapshot;
        if (!ProjectBoardDialogs.Confirm(this, Text("Lang_Board_RemoveImage", "Remove image"),
            string.Format(Text("Lang_Board_RemoveImagePrompt", "Remove {0}? The stored image will be deleted."), item.FileName),
            Text("Lang_Board_RemoveImage", "Remove"), danger: true)) return;
        try
        {
            JsonElement data = await ExecuteDataAsync("board.attachment.remove", new
            {
                project = captured.BoardId, revision = captured.Revision, attachmentId = item.Id,
                actor = LocalActor, host = LocalHost, session = (string?)null
            });
            AcceptMutation(data, isComment: false);
            attachments = await LoadAttachmentsAsync(FindCard(activeCardId)!, snapshot);
            RenderAttachments();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task<IReadOnlyList<ProjectBoardComment>> LoadCommentsAsync(Guid? cardId = null, BoardDetailsSnapshot? capturedSnapshot = null)
    {
        BoardDetailsSnapshot captured = capturedSnapshot ?? snapshot;
        var result = new List<ProjectBoardComment>();
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        long expected = captured.CommentRevision;
        do
        {
            var payload = new Dictionary<string, object?>
            {
                ["project"] = captured.BoardId, ["id"] = cardId ?? activeCardId, ["limit"] = 100, ["commentRevision"] = expected
            };
            if (cursor != null) payload["cursor"] = cursor;
            JsonElement data = await ExecuteDataAsync("board.comment.list", payload);
            CheckReadSnapshot(data, captured, isComment: true);
            foreach (JsonElement element in GetArray(data, "comments", "items"))
                if (element.Deserialize<ProjectBoardComment>(TaskProtocol.Options) is { } comment) result.Add(comment);
            string? nextCursor = ReadString(data, "cursor");
            cursor = string.IsNullOrEmpty(nextCursor) ? null : nextCursor;
            if (cursor != null && !cursors.Add(cursor)) throw new InvalidOperationException(Text("Lang_Board_CommentCursorLoop", "The comment list returned a repeated page cursor."));
        } while (cursor != null);
        return result;
    }

    private async Task<Dictionary<Guid, BoardAgentDelivery?>> LoadDeliveriesAsync(IEnumerable<ProjectBoardComment> source, BoardDetailsSnapshot captured)
    {
        var loaded = new Dictionary<Guid, BoardAgentDelivery?>();
        foreach (var comment in source.Where(c => c.Author.Kind == "user"))
        {
            try
            {
                JsonElement data = await ExecuteDataAsync("board.comment.delivery", new
                {
                    project = captured.BoardId, commentId = comment.Id, revision = captured.Revision, commentRevision = captured.CommentRevision
                });
                CheckReadSnapshot(data, captured, isComment: true);
                loaded[comment.Id] = ReadDelivery(data);
            }
            catch { loaded[comment.Id] = null; }
        }
        return loaded;
    }

    private async Task RefreshDeliveryStatusesAsync()
    {
        Guid selectedId = activeCardId;
        BoardDetailsSnapshot captured = snapshot;
        try
        {
            var loaded = await LoadDeliveriesAsync(comments, captured);
            if (selectedId != activeCardId || captured != snapshot) return;
            ReplaceDeliveries(loaded);
            RenderComments();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ReplaceDeliveries(IReadOnlyDictionary<Guid, BoardAgentDelivery?> loaded)
    {
        deliveries.Clear();
        foreach (var item in loaded) deliveries[item.Key] = item.Value;
    }

    private void RenderComments()
    {
        commentPanel.Children.Clear();
        foreach (var row in FlattenComments(comments))
        {
            ProjectBoardComment comment = row.Comment;
            var card = new Border { Padding = new Thickness(8), Margin = new Thickness(Math.Min(row.Depth, 8) * 12, 0, 0, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var stack = new StackPanel();
            string author = string.Equals(comment.Author.Name, comment.Author.Id, StringComparison.Ordinal)
                ? comment.Author.Name : $"{comment.Author.Name} · {comment.Author.Id}";
            string source = comment.Origin == null ? Text("Lang_Board_Unbound", "Unbound session") : $"{comment.Origin.Host}{(string.IsNullOrWhiteSpace(comment.Origin.SessionId) ? " · " + Text("Lang_Board_ProjectPull", "Host + project pull") : " · " + comment.Origin.SessionId)}";
            stack.Children.Add(Label($"{author} · {comment.CreatedAt.ToLocalTime():g}", tertiary: true));
            stack.Children.Add(Label($"{Text("Lang_Board_Source", "Source")}: {source}", tertiary: true));
            if (comment.Routing != null)
            {
                string target = $"{comment.Routing.Host}{(string.IsNullOrWhiteSpace(comment.Routing.SessionId) ? " · " + Text("Lang_Board_ProjectPull", "Host + project pull") : " · " + comment.Routing.SessionId)}";
                stack.Children.Add(Label($"{Text("Lang_Board_ReplyTarget", "This reply's recipient")}: {target}", tertiary: true));
            }
            if (comment.Origin != null && comment.AcknowledgedAt == null && comment.Author.Kind == "agent")
                stack.Children.Add(Label(Text("Lang_Board_Unread", "Unread"), tertiary: true));
            var body = Label(comment.DeletedAt.HasValue ? Text("Lang_Board_CommentDeleted", "Comment deleted") : comment.Body);
            body.TextWrapping = TextWrapping.Wrap;
            body.Margin = new Thickness(0, 2, 0, 4);
            stack.Children.Add(body);
            var actions = new WrapPanel();
            if (!comment.DeletedAt.HasValue)
                actions.Children.Add(Button("Lang_Board_Reply", "Reply", async (_, _) => await ReplyAsync(comment)));
            if (!comment.DeletedAt.HasValue)
                actions.Children.Add(Button("Lang_Board_BindSource", "Bind source", async (_, _) => await BindCommentAsync(comment)));
            if (comment.Routing != null)
            {
                if (deliveries.TryGetValue(comment.Id, out var delivery) && delivery != null)
                {
                    actions.Children.Add(Label($" · {DeliveryText(delivery.Status)}", tertiary: true));
                    if (delivery.Status is "failed" or "unknown") actions.Children.Add(Button("Lang_Board_RetryDelivery", "Retry", async (_, _) => await ConfirmAndDeliverAsync(comment, delivery, delivery.Status == "unknown")));
                    if (delivery.Status == "needsConfirmation") actions.Children.Add(Button("Lang_Board_Send", "Confirm send", async (_, _) => await ConfirmAndDeliverAsync(comment, delivery, false)));
                }
                actions.Children.Add(Button("Lang_Board_OpenHost", "Open host", (_, _) => OpenHost(comment.Routing.Host)));
            }
            if (!comment.DeletedAt.HasValue)
            {
                actions.Children.Add(Button("Lang_Board_Copy", "Copy", (_, _) => { Clipboard.SetText(comment.Body); statusText.Text = Text("Lang_Board_Copied", "Comment copied to clipboard."); }));
                if (comment.Author.Kind == "user" && comment.Author.Id == LocalActor)
                    actions.Children.Add(Button("Lang_Board_DeleteComment", "Delete comment", async (_, _) => await DeleteCommentAsync(comment)));
            }
            stack.Children.Add(actions);
            card.Child = stack;
            commentPanel.Children.Add(card);
        }
        if (comments.Count == 0) commentPanel.Children.Add(Label(Text("Lang_Board_NoComments", "No comments yet."), tertiary: true));
        var add = Button("Lang_Board_AddComment", "Add comment", async (_, _) => await AddCommentAsync(null), "BtnDefault");
        add.IsEnabled = !selectionLoading;
        commentPanel.Children.Add(add);
    }

    private static IReadOnlyList<CommentRow> FlattenComments(IReadOnlyList<ProjectBoardComment> items)
    {
        var byParent = items.GroupBy(c => c.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToArray());
        var pending = new Stack<(ProjectBoardComment Comment, int Depth)>();
        foreach (var root in byParent.GetValueOrDefault(Guid.Empty, []).Reverse()) pending.Push((root, 0));
        var rows = new List<CommentRow>();
        var seen = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var (comment, depth) = pending.Pop();
            if (!seen.Add(comment.Id)) continue;
            rows.Add(new CommentRow(comment, depth));
            if (byParent.TryGetValue(comment.Id, out var children))
                for (int i = children.Length - 1; i >= 0; i--) pending.Push((children[i], depth + 1));
        }
        foreach (var comment in items.Where(c => !seen.Contains(c.Id))) rows.Add(new CommentRow(comment, 0));
        return rows;
    }

    private async Task AddCommentAsync(ProjectBoardComment? parent, BoardDetailsSnapshot? capturedSnapshot = null, Guid? targetCardId = null)
    {
        BoardDetailsSnapshot captured = capturedSnapshot ?? snapshot;
        Guid cardId = targetCardId ?? activeCardId;
        string? body = ProjectBoardDialogs.PromptText(this, Text(parent == null ? "Lang_Board_AddComment" : "Lang_Board_Reply", parent == null ? "Add comment" : "Reply"),
            Text("Lang_Board_CommentPrompt", "Write a comment:"));
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["project"] = captured.BoardId, ["revision"] = captured.Revision, ["commentRevision"] = captured.CommentRevision,
                ["id"] = cardId, ["body"] = body, ["actor"] = LocalActor, ["host"] = LocalHost, ["session"] = null
            };
            if (parent != null) payload["parentId"] = parent.Id;
            JsonElement data = await ExecuteDataAsync("board.comment.add", payload);
            AcceptMutation(data, isComment: true);
            ProjectBoardComment? added = ReadComment(data);
            if (added != null) comments = comments.Append(added).ToArray();
            else comments = await LoadCommentsAsync(cardId);
            RenderComments();
            if (added != null)
            {
                JsonElement deliveryData = await ExecuteDataAsync("board.comment.delivery", new
                {
                    project = captured.BoardId, commentId = added.Id, revision = snapshot.Revision, commentRevision = snapshot.CommentRevision
                });
                var delivery = ReadDelivery(deliveryData);
                if (delivery != null)
                {
                    deliveries[added.Id] = delivery;
                    if (delivery.Status == "needsConfirmation") await ConfirmAndDeliverAsync(added, delivery, false);
                    RenderComments();
                }
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task DeleteCommentAsync(ProjectBoardComment comment)
    {
        BoardDetailsSnapshot captured = snapshot;
        if (!ProjectBoardDialogs.Confirm(this, Text("Lang_Board_DeleteComment", "Delete comment"),
            Text("Lang_Board_DeleteCommentPrompt", "Delete this comment? Replies will remain."),
            Text("Lang_Board_DeleteComment", "Delete comment"), danger: true)) return;
        try
        {
            JsonElement data = await ExecuteDataAsync("board.comment.delete", new
            {
                project = captured.BoardId, commentId = comment.Id, revision = captured.Revision, commentRevision = captured.CommentRevision,
                actor = LocalActor, host = LocalHost, session = (string?)null
            });
            AcceptMutation(data, isComment: true);
            comments = await LoadCommentsAsync(activeCardId);
            ReplaceDeliveries(await LoadDeliveriesAsync(comments, snapshot));
            RenderComments();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ReplyAsync(ProjectBoardComment parent)
    {
        BoardDetailsSnapshot captured = snapshot;
        Guid cardId = activeCardId;
        try
        {
            ProjectBoardCommentContext context = await GetCommentContextAsync(parent.Id, captured);
            if (cardId != activeCardId || captured != snapshot) throw new BoardCallException("CONFLICT", Text("Lang_Board_CommentsChanged", "Comments changed while they were loading. Close and reopen the details."), "commentRevision");
            await AddCommentAsync(context.Comment, captured, cardId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task<ProjectBoardCommentContext> GetCommentContextAsync(Guid commentId, BoardDetailsSnapshot captured)
    {
        JsonElement data = await ExecuteDataAsync("board.comment.get", new { project = captured.BoardId, commentId });
        if (!TryProperty(data, "registration", out var registrationData) ||
            !TryProperty(data, "comment", out var commentData) ||
            !TryProperty(data, "cardId", out var cardIdData) || !cardIdData.TryGetGuid(out Guid cardId))
            throw new InvalidDataException("The board returned an incomplete comment context.");
        var registration = registrationData.Deserialize<ProjectBoardRegistration>(TaskProtocol.Options)
            ?? throw new InvalidDataException("The board returned no project registration.");
        var comment = commentData.Deserialize<ProjectBoardComment>(TaskProtocol.Options)
            ?? throw new InvalidDataException("The board returned no comment.");
        long boardRevision = ReadLong(data, "boardRevision") ?? ReadLong(data, "revision") ?? -1;
        long commentRevision = ReadLong(data, "commentRevision") ?? -1;
        if (!Guid.TryParse(captured.BoardId, out Guid boardId) || registration.BoardId != boardId || cardId != activeCardId ||
            boardRevision != captured.Revision || commentRevision != captured.CommentRevision)
            throw new BoardCallException("CONFLICT", Text("Lang_Board_CommentsChanged", "Comments changed while they were loading. Close and reopen the details."), "commentRevision");
        return new ProjectBoardCommentContext(registration, cardId, comment, boardRevision, commentRevision);
    }

    internal async Task ConfirmAndDeliverAsync(ProjectBoardComment comment, BoardAgentDelivery delivery, bool unknown)
    {
        BoardDetailsSnapshot captured = snapshot;
        try
        {
            ProjectBoardCommentContext context = await GetCommentContextAsync(comment.Id, captured);
            comment = context.Comment;
        }
        catch (Exception ex) { ShowError(ex); return; }
        if (comment.Routing == null) return;
        bool verifiedUnknown = false;
        bool exactSession = !string.IsNullOrWhiteSpace(comment.Routing.SessionId);
        string message = exactSession
            ? string.Format(Text("Lang_Board_DeliveryConfirmationExact", "Send to {0} session {1}. This will continue that exact session and may execute in project directory {2}.\n\nReply body:\n{3}"),
                comment.Routing.Host, comment.Routing.SessionId, registration.Path, comment.Body)
            : string.Format(Text("Lang_Board_DeliveryConfirmationPull", "Send to {0}'s project inbox for {1}. No exact session will be continued; the host can pull this reply.\n\nReply body:\n{2}"),
                comment.Routing.Host, registration.Path, comment.Body);
        if (unknown)
        {
            if (!ProjectBoardDialogs.Confirm(this, Text("Lang_Board_RetryDelivery", "Retry delivery"),
                Text("Lang_Board_UnknownRetryPrompt", "The previous attempt has an unknown result. Verify in the target session that this reply did not arrive before retrying."),
                Text("Lang_Board_VerifiedRetry", "I verified; retry"), danger: true)) return;
            verifiedUnknown = true;
        }
        if (!ProjectBoardDialogs.Confirm(this, Text("Lang_Board_ConfirmDelivery", "Confirm reply delivery"), message,
            Text("Lang_Board_Send", "Send"))) return;
        try
        {
            JsonElement data = await ExecuteDataAsync("board.comment.deliver", new
            {
                project = captured.BoardId, commentId = comment.Id, confirm = true, verifiedUnknown,
                confirmationToken = delivery.ConfirmationHash, revision = captured.Revision, commentRevision = captured.CommentRevision
            });
            deliveries[comment.Id] = ReadDelivery(data) ?? delivery;
            statusText.Text = DeliveryText(ReadDelivery(data)?.Status ?? delivery.Status);
            RenderComments();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task BindCommentAsync(ProjectBoardComment comment)
    {
        BoardDetailsSnapshot captured = snapshot;
        try
        {
            Guid cardId = activeCardId;
            ProjectBoardCommentContext context = await GetCommentContextAsync(comment.Id, captured);
            if (cardId != activeCardId || captured != snapshot) throw new BoardCallException("CONFLICT", Text("Lang_Board_CommentsChanged", "Comments changed while they were loading. Close and reopen the details."), "commentRevision");
            comment = context.Comment;
            var hosts = await agents.DiscoverAsync();
            var window = new Window
            {
                Owner = this, Width = 440, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Title = Text("Lang_Board_BindSource", "Bind source")
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            var selector = new ComboBox { ItemsSource = hosts, DisplayMemberPath = nameof(BoardAgentHostInfo.DisplayName), SelectedValuePath = nameof(BoardAgentHostInfo.Host), Height = 32, Margin = new Thickness(0, 8, 0, 4) };
            selector.SetResourceReference(Control.BackgroundProperty, "BgSunkenBrush");
            selector.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            selector.SetResourceReference(Control.BorderBrushProperty, "BorderDefaultBrush");
            selector.SelectedValue = comment.Origin?.Host;
            var session = new TextBox { Text = comment.Origin?.SessionId ?? "", MinHeight = 32, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(8, 6, 8, 6) };
            session.SetResourceReference(Control.BackgroundProperty, "BgSunkenBrush");
            session.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            session.SetResourceReference(Control.BorderBrushProperty, "BorderDefaultBrush");
            session.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");
            var error = Label(""); error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            panel.Children.Add(Section("Lang_Board_BindSource", "Bind source"));
            panel.Children.Add(Label(Text("Lang_Board_SourceHost", "Source host"), tertiary: true)); panel.Children.Add(selector);
            panel.Children.Add(Label(Text("Lang_Board_SourceSessionOptional", "Source session ID (optional)"), tertiary: true)); panel.Children.Add(session);
            panel.Children.Add(error);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancel = ProjectBoardDialogs.CreateButton(Text("Lang_Board_Cancel", "Cancel")); cancel.IsCancel = true; footer.Children.Add(cancel);
            var clear = ProjectBoardDialogs.CreateButton(Text("Lang_Board_ClearBinding", "Clear binding")); clear.Click += (_, _) => { window.Tag = "clear"; window.DialogResult = true; }; footer.Children.Add(clear);
            var save = ProjectBoardDialogs.CreateButton(Text("Lang_Board_Save", "Save"), "BtnPrimary"); save.IsDefault = true;
            save.Click += async (_, _) =>
            {
                if (selector.SelectedValue is not string host || string.IsNullOrWhiteSpace(host)) { error.Text = Text("Lang_Board_HostRequired", "Select a host."); return; }
                try
                {
                    JsonElement data = await ExecuteDataAsync("board.comment.bind", new
                    {
                        project = captured.BoardId, commentId = comment.Id, targetHost = host, targetSession = string.IsNullOrWhiteSpace(session.Text) ? null : session.Text.Trim(),
                        actor = LocalActor, host = LocalHost, session = (string?)null, revision = captured.Revision, commentRevision = captured.CommentRevision
                    });
                    AcceptMutation(data, isComment: true);
                    window.DialogResult = true;
                }
                catch (Exception ex) { error.Text = ex.Message; }
            };
            footer.Children.Add(save); panel.Children.Add(footer);
            var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
            shell.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush"); shell.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush"); window.Content = shell;
            if (window.ShowDialog() == true)
            {
                if (window.Tag as string == "clear")
                {
                    JsonElement data = await ExecuteDataAsync("board.comment.bind", new
                    {
                        project = captured.BoardId, commentId = comment.Id, clear = true, actor = LocalActor, host = LocalHost, session = (string?)null,
                        revision = captured.Revision, commentRevision = captured.CommentRevision
                    });
                    AcceptMutation(data, isComment: true);
                }
                comments = await LoadCommentsAsync(activeCardId);
                ReplaceDeliveries(await LoadDeliveriesAsync(comments, snapshot));
                RenderComments();
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task OpenAgentConfigAsync()
    {
        try
        {
            await agents.DiscoverAsync();
            Process.Start(new ProcessStartInfo(agents.GetConfigPath()) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenHost(string host)
    {
        try
        {
            var target = agents.ResolveLaunch(host, registration.Path);
            if (target == null) { statusText.Text = Text("Lang_Board_HostUnavailable", "Host is unavailable."); return; }
            string? path = target.Path;
            string? uri = target.Uri;
            Process.Start(new ProcessStartInfo(uri ?? path ?? "") { UseShellExecute = true, WorkingDirectory = target.ProjectRoot });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowImagePreview(string title, string path)
    {
        if (!TryLoadImage(path, out var image, decodePixelWidth: null)) { ShowError(new IOException(Text("Lang_Board_ImageMissing", "Image file is missing"))); return; }
        var window = new Window
        {
            Owner = this, Title = title, Width = 900, Height = 700, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ResizeMode = ResizeMode.CanResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12) };
        shell.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush"); shell.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel();
        var close = Button("Lang_Board_Close", "Close", (_, _) => window.Close()); close.Content = "✕"; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(Label(title, true)); grid.Children.Add(header);
        var preview = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(preview, 1); grid.Children.Add(preview);
        shell.Child = grid; window.Content = shell; window.ShowDialog();
    }

    private string AttachmentPath(string relativePath)
    {
        string root = Path.GetFullPath(registration.Path);
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Attachment path is outside the registered project directory.");
        return path;
    }

    private static bool TryLoadImage(string path, out BitmapImage image, int? decodePixelWidth = 240)
    {
        image = new BitmapImage();
        if (!File.Exists(path)) return false;
        try
        {
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path);
            if (decodePixelWidth.HasValue) image.DecodePixelWidth = decodePixelWidth.Value;
            image.EndInit(); image.Freeze();
            return true;
        }
        catch { return false; }
    }

    private static bool MatchesHash(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private Dictionary<string, object?> BasePayload(ProjectBoardCard card) => new()
    {
        ["project"] = snapshot.BoardId, ["revision"] = snapshot.Revision, ["id"] = card.Id
    };

    private void AddMappedColumn(Dictionary<string, object?> payload, ProjectBoardCard card, string targetStatus)
    {
        ProjectBoardColumn? column = initialBoard.Columns.FirstOrDefault(c => c.Status == targetStatus);
        if (column == null) return;
        int order = cards.Count(c => c.ColumnId == column.Id && c.ParentId == card.ParentId && c.DeletedAt == null && c.Id != card.Id);
        payload["columnId"] = column.Id;
        payload["order"] = order;
    }

    private bool IsOwnedActiveClaim(ProjectBoardClaim? claim) => claim is { } value && value.HostId == LocalHost && value.ActorId == LocalActor && value.SessionId == null && value.ExpiresAt > DateTime.UtcNow;
    private static bool IsClaimExpired(ProjectBoardClaim? claim) => claim == null || claim.ExpiresAt <= DateTime.UtcNow;
    private bool CanClaim(ProjectBoardClaim? claim) => IsClaimExpired(claim) || IsOwnedActiveClaim(claim);

    private void AcceptMutation(JsonElement data, bool isComment)
    {
        long? revision = ReadLong(data, "boardRevision") ?? ReadLong(data, "revision");
        long? commentRevision = ReadLong(data, "commentRevision");
        snapshot = snapshot with
        {
            Revision = revision ?? snapshot.Revision,
            CommentRevision = commentRevision ?? (isComment ? snapshot.CommentRevision + 1 : snapshot.CommentRevision)
        };
        errorText.Text = "";
    }

    private void UpdateCardFromMutation(JsonElement data)
    {
        ProjectBoardCard? updated = ReadCard(data);
        if (updated == null) return;
        cards = cards.Select(c => c.Id == updated.Id ? updated : c).ToArray();
    }

    private ProjectBoardCard? ReadCard(JsonElement data)
    {
        if (TryProperty(data, "card", out var card)) return card.Deserialize<ProjectBoardCard>(TaskProtocol.Options);
        return data.TryGetProperty("id", out _) && data.TryGetProperty("title", out _) ? data.Deserialize<ProjectBoardCard>(TaskProtocol.Options) : null;
    }

    private ProjectBoardComment? ReadComment(JsonElement data)
    {
        if (TryProperty(data, "comment", out var comment)) return comment.Deserialize<ProjectBoardComment>(TaskProtocol.Options);
        return data.TryGetProperty("body", out _) && data.TryGetProperty("author", out _) ? data.Deserialize<ProjectBoardComment>(TaskProtocol.Options) : null;
    }

    private static BoardAgentDelivery? ReadDelivery(JsonElement data)
    {
        if (!TryProperty(data, "delivery", out var delivery)) return null;
        if (delivery.ValueKind == JsonValueKind.String)
            return new BoardAgentDelivery("", "", "", "", null, delivery.GetString() ?? "", ReadString(data, "confirmationHash") ?? "", ReadString(data, "error"), DateTimeOffset.UtcNow);
        try { return delivery.Deserialize<BoardAgentDelivery>(TaskProtocol.Options); } catch { return null; }
    }

    private void CheckReadSnapshot(JsonElement data, BoardDetailsSnapshot captured, bool isComment)
    {
        long? boardRevision = ReadLong(data, "boardRevision") ?? ReadLong(data, "revision");
        long? commentRevision = ReadLong(data, "commentRevision");
        if (boardRevision.HasValue && boardRevision.Value != captured.Revision ||
            isComment && commentRevision.HasValue && commentRevision.Value != captured.CommentRevision)
            throw new BoardCallException("CONFLICT", Text(isComment ? "Lang_Board_CommentsChanged" : "Lang_Board_SnapshotConflict", isComment
                ? "Comments changed while they were loading. Close and reopen the details."
                : "The board changed. Close and reopen the details before retrying."), isComment ? "commentRevision" : "revision");
    }

    private async Task<JsonElement> ExecuteDataAsync(string operation, object payload)
    {
        var request = new TaskRequest
        {
            RequestId = Guid.NewGuid().ToString("D"), Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
        };
        bool mutation = operation is not ("board.comment.list" or "board.comment.delivery" or "board.attachment.list" or "board.comment.get");
        if (mutation) mutationInFlight = true;
        try
        {
            TaskResponse response = await commands.ExecuteAsync(request);
            if (!response.Ok) throw new BoardCallException(response.Error?.Code ?? "BOARD_ERROR", response.Error?.Message ?? Text("Lang_Board_OperationFailed", "The board operation failed."), response.Error?.Field);
            if (response.Warnings is { Count: > 0 } warnings)
                statusText.Text = warnings.Any(warning => warning.StartsWith("manualPullRequired:", StringComparison.OrdinalIgnoreCase))
                    ? Text("Lang_Board_ManualPullRequired", "No saved recipient; the host must pull this comment manually.")
                    : string.Join(Environment.NewLine, warnings);
            return response.Data == null ? JsonDocument.Parse("{}").RootElement.Clone() : JsonSerializer.SerializeToElement(response.Data, TaskProtocol.Options);
        }
        finally { if (mutation) mutationInFlight = false; }
    }

    private void ShowError(Exception ex)
    {
        errorText.Text = ex.Message;
        statusText.Text = ex is BoardCallException { Code: "CONFLICT" } ? Text("Lang_Board_SnapshotConflict", "The board changed. Close and reopen the details before retrying.") : "";
    }

    private ProjectBoardCard? FindCard(Guid id) => cards.FirstOrDefault(c => c.Id == id);

    private string ClaimOwner(ProjectBoardClaim claim) => claim.ActorId == LocalActor && claim.HostId == LocalHost && claim.SessionId == null
        ? Text("Lang_Board_ClaimedByYou", "Claimed by you") : $"{claim.ActorId} · {claim.HostId}{(claim.SessionId == null ? "" : " · " + claim.SessionId)}";

    private string ClaimSummary(ProjectBoardClaim claim) => string.Format(
        Text("Lang_Board_ClaimSummary", "{0} · {1}"), ClaimOwner(claim), RemainingLease(claim.ExpiresAt));

    private string RemainingLease(DateTime expiresAt)
    {
        TimeSpan remaining = expiresAt - DateTime.UtcNow;
        return remaining <= TimeSpan.Zero ? Text("Lang_Board_LeaseExpired", "lease expired") : string.Format(Text("Lang_Board_LeaseRemaining", "{0}m left"), Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
    }

    private string StatusText(string status) => Text(StatusKey(status), StatusFallback(status));
    private static string StatusKey(string action) => action switch
    {
        "claim" => "Lang_Board_Claim", "release" => "Lang_Board_Release", "submit" => "Lang_Board_Submit",
        "close" => "Lang_Board_CloseCard", "reopen" => "Lang_Board_Reopen", _ => "Lang_Board_StatusNotStarted"
    };
    private static string StatusFallback(string action) => action switch
    {
        "claim" => "Claim", "release" => "Release", "submit" => "Submit for review", "close" => "Close card", "reopen" => "Reopen", _ => "Not started"
    };
    private string DeliveryText(string status) => Text($"Lang_Board_Delivery_{status}", status);

    private Border SmallStatus(string status)
    {
        var label = Label(StatusText(status), tertiary: true); label.Margin = new Thickness(0); label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var badge = new Border { Child = label, Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 0, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, status switch { "inProgress" => "AccentSoftBrush", "pendingReview" => "WarningSoftBrush", _ => "BgTertiaryBrush" });
        return badge;
    }

    private UIElement Section(string key, string fallback)
    {
        var label = Label(Text(key, fallback), true, true); label.SetResourceReference(TextBlock.TextProperty, key); label.Margin = new Thickness(2, 8, 2, 6); return label;
    }

    private TextBlock Label(string value, bool bold = false, bool tertiary = false)
    {
        var label = new TextBlock { Text = value, FontSize = tertiary ? 11 : 13, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
        label.SetResourceReference(TextBlock.ForegroundProperty, tertiary ? "TextTertiaryBrush" : "TextPrimaryBrush"); return label;
    }

    private Button Button(string? key, string fallback, RoutedEventHandler click, string style = "BtnDefault")
    {
        var button = ProjectBoardDialogs.CreateButton(key == null ? fallback : Text(key, fallback), style);
        if (key != null) button.SetResourceReference(ContentControl.ContentProperty, key);
        button.Click += click; return button;
    }

    private string Text(string key, string fallback) => ProjectBoardDialogs.GetText(this, key, fallback);

    private static IEnumerable<JsonElement> GetArray(JsonElement data, params string[] names)
    {
        if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().ToArray();
        foreach (string name in names)
            if (TryProperty(data, name, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        return [];
    }

    private static bool TryProperty(JsonElement data, string name, out JsonElement value)
    {
        value = default;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out value);
    }

    private static string? ReadString(JsonElement data, string name) => TryProperty(data, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? ReadLong(JsonElement data, string name) => TryProperty(data, name, out var value) && value.TryGetInt64(out long result) ? result : null;

    private sealed record ParentChoice(Guid? Id, string Label);
    private sealed record CommentRow(ProjectBoardComment Comment, int Depth);
    private sealed record BoardAttachmentView(Guid Id, string FileName, string RelativePath, long Size, string Sha256, DateTime AddedAt, string AddedBy, bool? Exists = null, bool? HashMatch = null);
}
