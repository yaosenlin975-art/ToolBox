using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ToolBox.Core.Boards;
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Boards;

public sealed class BoardsView : UserControl
{
    private const string CardDragFormat = "ToolBox.ProjectBoard.Card";
    private readonly ProjectBoardStore store = ProjectBoardStore.Instance;
    private readonly BoardCommandService commandService = BoardCommandService.Instance;
    private readonly BoardAgentService agentService = BoardAgentService.Instance;
    private readonly IBoardDialogService dialogService;
    private readonly ComboBox projectSelector = new() { MinWidth = 250, Height = 34, DisplayMemberPath = nameof(ProjectBoardRegistration.Name), SelectedValuePath = nameof(ProjectBoardRegistration.BoardId) };
    private readonly Button unregisterButton;
    private readonly Button addColumnButton;
    private readonly Button addCardButton;
    private readonly Button trashButton;
    private readonly ScrollViewer columnsScroller;
    private readonly StackPanel columnsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock emptyText;
    private readonly TextBlock statusText;
    private readonly Dictionary<Guid, int> commentCounts = [];
    private readonly Dictionary<Guid, int> attachmentCounts = [];
    private readonly Dictionary<Guid, int> descendantCounts = [];
    private readonly Dictionary<Guid, int> closedDescendantCounts = [];
    private readonly List<(TextBlock Label, ProjectBoardClaim Claim)> leaseLabels = [];
    private readonly DispatcherTimer leaseTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<ProjectBoardRegistration> projects = [];
    private ProjectBoard? board;
    private string? selectedBoardId;
    private bool showRecycleBin;
    private bool isLoaded;
    private bool isRendering;
    private bool isBoardLoading;
    private bool refreshRunning;
    private bool refreshQueued;
    private int lifecycleVersion;
    private TextBlock? dragHandle;
    private Point dragStart;
    private Guid? dragCardId;
    private BoardMutationSnapshot? dragSnapshot;
    private ProjectBoardCard? dragCardSnapshot;
    private ProjectBoard? dragBoardSnapshot;
    private bool isCardDragging;

    public BoardsView() : this(new WpfBoardDialogService()) { }

    internal BoardsView(IBoardDialogService dialogService)
    {
        this.dialogService = dialogService;
        SetResourceReference(BackgroundProperty, "BgBrush");
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Border { Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
        toolbar.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var toolbarPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toolbarPanel.Children.Add(Label(Text("Lang_Board_Project", "Project")));
        projectSelector.Margin = new Thickness(8, 0, 8, 0);
        projectSelector.SelectionChanged += ProjectSelector_SelectionChanged;
        toolbarPanel.Children.Add(projectSelector);
        var addProjectButton = ResourceButton("Lang_Board_AddProject", "Add project", ClickAddProject);
        toolbarPanel.Children.Add(addProjectButton);
        unregisterButton = ResourceButton("Lang_Board_Unregister", "Remove registration", ClickUnregister);
        unregisterButton.Margin = new Thickness(6, 0, 0, 0);
        toolbarPanel.Children.Add(unregisterButton);
        addColumnButton = ResourceButton("Lang_Board_AddColumn", "New column", ClickAddColumn);
        addColumnButton.Margin = new Thickness(18, 0, 0, 0);
        toolbarPanel.Children.Add(addColumnButton);
        addCardButton = ResourceButton("Lang_Board_AddCard", "New card", ClickAddCard, "BtnPrimary");
        addCardButton.Margin = new Thickness(6, 0, 0, 0);
        toolbarPanel.Children.Add(addCardButton);
        trashButton = ResourceButton("Lang_Board_ShowTrash", "Recycle bin", ClickToggleRecycleBin);
        trashButton.Margin = new Thickness(6, 0, 0, 0);
        toolbarPanel.Children.Add(trashButton);
        toolbar.Child = toolbarPanel;
        root.Children.Add(toolbar);

        statusText = Label("", tertiary: true);
        statusText.Margin = new Thickness(2, 0, 0, 8);
        statusText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(statusText, 1);
        root.Children.Add(statusText);

        var content = new Grid();
        columnsScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = columnsPanel
        };
        columnsScroller.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        content.Children.Add(columnsScroller);
        var empty = new Border { Padding = new Thickness(28), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
        emptyText = Label(Text("Lang_Board_Empty", "Select a project or register a project folder."));
        emptyText.FontSize = 14;
        emptyText.TextWrapping = TextWrapping.Wrap;
        empty.Child = emptyText;
        content.Children.Add(empty);
        empty.Tag = emptyText;
        Grid.SetRow(content, 2);
        root.Children.Add(content);
        Content = root;
        leaseTimer.Tick += (_, _) => RefreshLeaseDisplays();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Refresh() => QueueRefresh();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (isLoaded) return;
        isLoaded = true;
        lifecycleVersion++;
        leaseTimer.Start();
        store.Changed += Store_Changed;
        agentService.Changed += AgentService_Changed;
        App.LanguageChanged += OnLanguageChanged;
        QueueRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!isLoaded) return;
        isLoaded = false;
        lifecycleVersion++;
        leaseTimer.Stop();
        refreshQueued = false;
        store.Changed -= Store_Changed;
        agentService.Changed -= AgentService_Changed;
        App.LanguageChanged -= OnLanguageChanged;
        ClearDragState();
    }

    private void Store_Changed()
    {
        if (Dispatcher.CheckAccess()) QueueRefresh();
        else Dispatcher.BeginInvoke(new Action(QueueRefresh));
    }

    private void AgentService_Changed()
    {
        if (Dispatcher.CheckAccess()) RenderBoard();
        else Dispatcher.BeginInvoke(new Action(RenderBoard));
    }

    private void OnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnLanguageChanged));
            return;
        }
        if (isLoaded) RenderBoard();
    }

    private void QueueRefresh()
    {
        if (!isLoaded || Dispatcher.HasShutdownStarted) return;
        refreshQueued = true;
        if (refreshRunning) return;
        refreshRunning = true;
        _ = DrainRefreshQueueAsync();
    }

    private async Task DrainRefreshQueueAsync()
    {
        try
        {
            while (isLoaded && refreshQueued)
            {
                refreshQueued = false;
                await RefreshOnceAsync(lifecycleVersion);
            }
        }
        finally
        {
            refreshRunning = false;
            if (isLoaded && refreshQueued) QueueRefresh();
        }
    }

    private async Task RefreshOnceAsync(int version)
    {
        isBoardLoading = true;
        UpdateToolbarState();
        try
        {
            SetStatus(Text("Lang_Loading", "Loading…"));
            var loadedProjects = await ExecuteAsync<ProjectBoardRegistration[]>("project.list", new { }) ?? [];
            if (!IsCurrent(version)) return;
            projects = loadedProjects;
            string? priorBoardId = selectedBoardId;
            var selected = projects.FirstOrDefault(p => string.Equals(p.BoardId.ToString("D"), priorBoardId, StringComparison.OrdinalIgnoreCase))
                ?? projects.FirstOrDefault();
            string? nextBoardId = selected?.BoardId.ToString("D");
            if (!string.Equals(priorBoardId, nextBoardId, StringComparison.OrdinalIgnoreCase)) board = null;
            selectedBoardId = nextBoardId;
            isRendering = true;
            projectSelector.ItemsSource = null;
            projectSelector.ItemsSource = projects;
            projectSelector.SelectedValue = selected?.BoardId;
            isRendering = false;
            UpdateToolbarState();

            if (selected == null)
            {
                board = null;
                SetStatus("");
                RenderBoard();
                return;
            }

            JsonElement boardData = await ExecuteElementAsync("board.get", new { project = selected.BoardId.ToString("D") });
            var loadedBoard = boardData.Deserialize<ProjectBoard>(TaskProtocol.Options);
            if (!IsCurrent(version) || !string.Equals(selectedBoardId, selected.BoardId.ToString("D"), StringComparison.OrdinalIgnoreCase)) return;
            board = loadedBoard;
            commentCounts.Clear();
            attachmentCounts.Clear();
            descendantCounts.Clear();
            closedDescendantCounts.Clear();
            if (boardData.TryGetProperty("cards", out var cardData) && cardData.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cardData.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idData) || !idData.TryGetGuid(out Guid id)) continue;
                    if (item.TryGetProperty("commentCount", out var countData) && countData.TryGetInt32(out int count)) commentCounts[id] = count;
                    else if (item.TryGetProperty("commentsCount", out countData) && countData.TryGetInt32(out count)) commentCounts[id] = count;
                    else
                    {
                        ProjectBoardCard? card = board?.Cards.FirstOrDefault(c => c.Id == id);
                        commentCounts[id] = card == null ? 0 : card.CommentCount > 0 ? card.CommentCount : card.Comments?.Count(c => c.DeletedAt == null) ?? 0;
                    }
                    if (item.TryGetProperty("attachmentCount", out countData) && countData.TryGetInt32(out count)) attachmentCounts[id] = count;
                    else
                    {
                        ProjectBoardCard? card = board?.Cards.FirstOrDefault(c => c.Id == id);
                        attachmentCounts[id] = card == null ? 0 : card.AttachmentCount > 0 ? card.AttachmentCount : card.Attachments?.Count ?? 0;
                    }
                    if (item.TryGetProperty("childCount", out countData) && countData.TryGetInt32(out count)) descendantCounts[id] = count;
                    if (item.TryGetProperty("childClosedCount", out countData) && countData.TryGetInt32(out count)) closedDescendantCounts[id] = count;
                }
            }
            SetStatus("");
            RenderBoard();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(version)) return;
            isRendering = false;
            board = null;
            SetStatus(ex.Message);
            RenderBoard();
        }
        finally
        {
            if (IsCurrent(version))
            {
                isBoardLoading = false;
                UpdateToolbarState();
            }
        }
    }

    private bool IsCurrent(int version) => isLoaded && lifecycleVersion == version && !Dispatcher.HasShutdownStarted;

    private BoardMutationSnapshot? CaptureBoardMutationSnapshot() => board is { } current
        ? new BoardMutationSnapshot(current.BoardId.ToString("D"), current.Revision)
        : null;

    private void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || projectSelector.SelectedItem is not ProjectBoardRegistration registration) return;
        selectedBoardId = registration.BoardId.ToString("D");
        board = null;
        showRecycleBin = false;
        UpdateToolbarState();
        SetStatus(Text("Lang_Loading", "Loading…"));
        RenderBoard();
        QueueRefresh();
    }

    private async void ClickAddProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Text("Lang_Board_ChooseFolder", "Choose project folder"), Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var initialize = ProjectBoardDialogs.ChooseInitialization(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"),
            Text("Lang_Board_InitQuestion", "Initialize a board here?"));
        if (initialize == BoardInitializationChoice.Cancel) return;
        bool init = initialize == BoardInitializationChoice.Initialize;
        try
        {
            ProjectBoardRegistration registration;
            try
            {
                registration = await ExecuteAsync<ProjectBoardRegistration>("project.register", new { path = dialog.FolderName, init, replacePath = false });
            }
            catch (BoardCallException ex) when (ex.Code == "CONFLICT" && ex.Field == "replacePath")
            {
                if (!ProjectBoardDialogs.Confirm(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"),
                    Text("Lang_Board_ReplacePathPrompt", "Switch the local registration to this folder?"), Text("Lang_Board_Confirm", "Continue"))) return;
                registration = await ExecuteAsync<ProjectBoardRegistration>("project.register", new { path = dialog.FolderName, init, replacePath = true });
            }
            selectedBoardId = registration.BoardId.ToString("D");
            board = null;
            showRecycleBin = false;
            SetStatus(Text("Lang_Loading", "Loading…"));
            RenderBoard();
            QueueRefresh();
        }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private async void ClickUnregister(object sender, RoutedEventArgs e)
    {
        if (projectSelector.SelectedItem is not ProjectBoardRegistration registration) return;
        string prompt = string.Format(Text("Lang_Board_UnregisterPrompt", "Remove the local registration for “{0}”? Files will remain."), registration.Name);
        if (!ProjectBoardDialogs.Confirm(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"), prompt,
            Text("Lang_Board_Unregister", "Remove registration"), danger: true)) return;
        try
        {
            await ExecuteAsync<JsonElement>("project.unregister", new { project = registration.BoardId.ToString("D") });
            selectedBoardId = null;
            board = null;
            SetStatus(Text("Lang_Loading", "Loading…"));
            RenderBoard();
            QueueRefresh();
        }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private void ClickToggleRecycleBin(object sender, RoutedEventArgs e)
    {
        showRecycleBin = !showRecycleBin;
        trashButton.SetResourceReference(ContentControl.ContentProperty, showRecycleBin ? "Lang_Board_BackToBoard" : "Lang_Board_ShowTrash");
        RenderBoard();
    }

    private async void ClickAddColumn(object sender, RoutedEventArgs e)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || showRecycleBin) return;
        string? title = dialogService.PromptText(Window.GetWindow(this), Text("Lang_Board_AddColumn", "New column"), Text("Lang_Board_ColumnNamePrompt", "Enter a column name:"));
        if (string.IsNullOrWhiteSpace(title)) return;
        await MutateAsync("board.column.create", new { project = snapshot.Value.Project, revision = snapshot.Value.Revision, title = title.Trim() });
    }

    private async void ClickAddCard(object sender, RoutedEventArgs e)
    {
        if (!isBoardLoading && board?.Columns.OrderBy(c => c.Order).FirstOrDefault() is { } column) await CreateCardAsync(column.Id);
    }

    private void UpdateToolbarState()
    {
        bool selected = selectedBoardId != null;
        unregisterButton.IsEnabled = selected;
        addColumnButton.IsEnabled = selected && board != null && !isBoardLoading && !showRecycleBin;
        addCardButton.IsEnabled = selected && board?.Columns.Count > 0 && !isBoardLoading && !showRecycleBin;
        trashButton.IsEnabled = selected && board != null && !isBoardLoading;
        columnsPanel.IsEnabled = board != null && !isBoardLoading;
        trashButton.SetResourceReference(ContentControl.ContentProperty, showRecycleBin ? "Lang_Board_BackToBoard" : "Lang_Board_ShowTrash");
    }

    private void RenderBoard()
    {
        double offset = columnsScroller.HorizontalOffset;
        leaseLabels.Clear();
        columnsPanel.Children.Clear();
        bool hasBoard = board != null;
        var content = (Grid)columnsScroller.Parent;
        var empty = (Border)content.Children[1];
        empty.Visibility = hasBoard ? Visibility.Collapsed : Visibility.Visible;
        emptyText.Text = statusText.Text.Length > 0 ? statusText.Text : projects.Count == 0 ? Text("Lang_Board_Empty", "Select a project or register a project folder.") : Text("Lang_Loading", "Loading…");
        UpdateToolbarState();
        if (board == null) return;
        var orderedColumns = board.Columns.OrderBy(c => c.Order).ToArray();
        foreach (var column in orderedColumns) columnsPanel.Children.Add(CreateColumn(column, orderedColumns));
        if (orderedColumns.Length == 0)
        {
            empty.Visibility = Visibility.Visible;
            emptyText.Text = Text("Lang_Board_NoColumns", "This board has no columns.");
        }
        Dispatcher.BeginInvoke(new Action(() => columnsScroller.ScrollToHorizontalOffset(offset)));
    }

    private UIElement CreateColumn(ProjectBoardColumn column, IReadOnlyList<ProjectBoardColumn> orderedColumns)
    {
        var panel = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        var title = Label(column.Title, bold: true);
        title.FontSize = 14;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titleStack.Children.Add(title);
        int count = BoardCards(column.Id).Count;
        var countText = Label(count.ToString(), tertiary: true);
        titleStack.Children.Add(countText);
        header.Children.Add(titleStack);
        var columnMenu = CreateButton("⋯", "IconButton");
        columnMenu.Width = 32;
        columnMenu.Height = 30;
        columnMenu.ContextMenu = BuildColumnMenu(column, orderedColumns);
        columnMenu.Click += (_, _) => columnMenu.ContextMenu.IsOpen = true;
        Grid.SetColumn(columnMenu, 1);
        header.Children.Add(columnMenu);
        panel.Children.Add(header);

        var cardsPanel = new StackPanel { Tag = column.Id, AllowDrop = !showRecycleBin };
        if (!showRecycleBin)
        {
            cardsPanel.DragOver += OnDragOver;
            cardsPanel.Drop += OnColumnDrop;
        }
        var visibleCards = BoardCards(column.Id);
        foreach (var card in visibleCards) cardsPanel.Children.Add(CreateCard(card));
        if (visibleCards.Count == 0)
            cardsPanel.Children.Add(Label(Text("Lang_Board_NoCards", "No cards"), tertiary: true));
        panel.Children.Add(cardsPanel);

        if (!showRecycleBin)
        {
            var add = ResourceButton("Lang_Board_AddCard", "New card", async (_, _) => await CreateCardAsync(column.Id));
            add.HorizontalAlignment = HorizontalAlignment.Stretch;
            add.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(add);
        }

        var border = new Border { Width = 285, MinHeight = 440, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel, Tag = column.Id, AllowDrop = !showRecycleBin };
        border.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        if (!showRecycleBin)
        {
            border.DragOver += OnDragOver;
            border.Drop += OnColumnDrop;
        }
        return border;
    }

    private Border CreateCard(ProjectBoardCard card)
    {
        var cardPanel = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label(card.Title, bold: true);
        title.Margin = new Thickness(0, 0, 4, 2);
        title.TextWrapping = TextWrapping.Wrap;
        title.ToolTip = showRecycleBin ? null : Text("Lang_Board_DragHint", "Drag cards to reorder or move between columns");
        if (!showRecycleBin)
        {
            title.Cursor = Cursors.SizeAll;
            title.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (CaptureBoardMutationSnapshot() is not { } snapshot || board is not { } capturedBoard || capturedBoard.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt == null) is not { } source) return;
                dragHandle = title;
                dragCardId = card.Id;
                dragSnapshot = snapshot;
                dragCardSnapshot = source;
                dragBoardSnapshot = capturedBoard;
                dragStart = e.GetPosition(title);
                Mouse.Capture(title);
            };
            title.PreviewMouseMove += DragHandle_MouseMove;
            title.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (Mouse.Captured == title) Mouse.Capture(null);
                if (!isCardDragging) ClearDragState();
            };
        }
        top.Children.Add(title);
        if (!showRecycleBin)
        {
            var cardActions = new StackPanel { Orientation = Orientation.Horizontal };
            var detailsButton = ResourceButton("Lang_Board_Details", "Details", async (_, _) => await OpenCardDetailsAsync(card));
            detailsButton.Padding = new Thickness(6, 3, 6, 3);
            detailsButton.MinHeight = 26;
            var edit = ResourceButton("Lang_Board_EditCard", "Edit", async (_, _) => await EditCardAsync(card));
            edit.Padding = new Thickness(7, 3, 7, 3);
            edit.MinHeight = 26;
            edit.Margin = new Thickness(2, 0, 0, 0);
            cardActions.Children.Add(detailsButton);
            cardActions.Children.Add(edit);
            Grid.SetColumn(cardActions, 1);
            top.Children.Add(cardActions);
        }
        cardPanel.Children.Add(top);

        var details = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        string[] priorities = [Text("Lang_Board_Normal", "Normal"), Text("Lang_Board_Important", "Important"), Text("Lang_Board_Urgent", "Urgent")];
        var priority = Label(priorities[Math.Clamp(card.Priority, 0, 2)], tertiary: true);
        priority.Margin = new Thickness(0, 0, 8, 0);
        details.Children.Add(priority);
        if (card.Tags.Count > 0)
        {
            var tags = Label(string.Join(" · ", card.Tags), tertiary: true);
            tags.TextTrimming = TextTrimming.CharacterEllipsis;
            tags.MaxWidth = 200;
            details.Children.Add(tags);
        }
        cardPanel.Children.Add(details);
        if (!showRecycleBin)
        {
            var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            meta.Children.Add(StatusBadge(card.Status));
            if (card.Claim is { } claim)
            {
                var leaseLabel = Label(ClaimSummary(claim), tertiary: true);
                leaseLabels.Add((leaseLabel, claim));
                meta.Children.Add(leaseLabel);
            }
            int descendantCount = descendantCounts.TryGetValue(card.Id, out int childCount) ? childCount : CountDescendants(card.Id);
            if (descendantCount > 0)
            {
                int closed = closedDescendantCounts.TryGetValue(card.Id, out int closedCount) ? closedCount : CountClosedDescendants(card.Id);
                meta.Children.Add(Label(string.Format(Text("Lang_Board_DescendantProgress", "Subtasks {0}/{1} closed"), closed, descendantCount), tertiary: true));
            }
            int attachmentCount = attachmentCounts.TryGetValue(card.Id, out int imageCount) ? imageCount : card.Attachments?.Count ?? 0;
            int commentCount = commentCounts.GetValueOrDefault(card.Id, card.Comments?.Count ?? 0);
            meta.Children.Add(Label(string.Format(Text("Lang_Board_AttachmentCommentCounts", "Images {0} · Comments {1}"), attachmentCount, commentCount), tertiary: true));
            cardPanel.Children.Add(meta);
        }
        if (card.DueDate.HasValue)
        {
            string date = card.DueDate.Value.ToLocalTime().ToString("d");
            var due = Label($"{Text("Lang_Board_DueDate", "Due date")}: {date}", tertiary: true);
            cardPanel.Children.Add(due);
        }
        if (!string.IsNullOrWhiteSpace(card.Description))
        {
            var description = Label(card.Description, tertiary: true);
            description.TextWrapping = TextWrapping.Wrap;
            description.MaxHeight = 56;
            cardPanel.Children.Add(description);
        }

        var menuButton = CreateButton("⋯", "IconButton");
        menuButton.Width = 28;
        menuButton.Height = 26;
        menuButton.ContextMenu = BuildCardMenu(card);
        menuButton.Click += (_, _) => menuButton.ContextMenu.IsOpen = true;
        var menuRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        var state = Label(showRecycleBin ? Text("Lang_Board_RecycleBinLabel", "In recycle bin") : "", tertiary: true);
        menuRow.Children.Add(state);
        DockPanel.SetDock(menuButton, Dock.Right);
        menuRow.Children.Add(menuButton);
        cardPanel.Children.Add(menuRow);

        var border = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = cardPanel, Tag = card, AllowDrop = !showRecycleBin };
        border.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        if (!showRecycleBin)
        {
            border.DragOver += OnDragOver;
            border.Drop += OnCardDrop;
        }
        return border;
    }

    private List<ProjectBoardCard> BoardCards(Guid columnId) => board?.Cards
        .Where(c => c.ColumnId == columnId && (showRecycleBin ? c.DeletedAt != null : c.DeletedAt == null && c.ParentId == null))
        .OrderBy(c => c.Order).ThenBy(c => c.Id).ToList() ?? [];

    private ContextMenu BuildColumnMenu(ProjectBoardColumn column, IReadOnlyList<ProjectBoardColumn> orderedColumns)
    {
        var menu = NewContextMenu();
        menu.Items.Add(MenuItem("Lang_Board_RenameColumn", "Rename column", async (_, _) => await RenameColumnAsync(column)));
        int index = Array.FindIndex(orderedColumns.ToArray(), c => c.Id == column.Id);
        menu.Items.Add(MenuItem("Lang_Board_MoveColumnLeft", "Move column left", async (_, _) => await MoveColumnAsync(column, index - 1), index > 0));
        menu.Items.Add(MenuItem("Lang_Board_MoveColumnRight", "Move column right", async (_, _) => await MoveColumnAsync(column, index + 1), index >= 0 && index < orderedColumns.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Lang_Board_DeleteColumn", "Delete column", async (_, _) => await DeleteColumnAsync(column)));
        return menu;
    }

    private ContextMenu BuildCardMenu(ProjectBoardCard card)
    {
        var menu = NewContextMenu();
        if (showRecycleBin)
        {
            menu.Items.Add(MenuItem("Lang_Board_RestoreCard", "Restore card", async (_, _) => await RestoreCardAsync(card)));
            return menu;
        }
        menu.Items.Add(MenuItem("Lang_Board_Details", "Details", async (_, _) => await OpenCardDetailsAsync(card)));
        menu.Items.Add(MenuItem("Lang_Board_EditCard", "Edit", async (_, _) => await EditCardAsync(card)));
        var siblings = BoardCards(card.ColumnId);
        int index = siblings.FindIndex(c => c.Id == card.Id);
        menu.Items.Add(MenuItem("Lang_Board_MoveCardEarlier", "Move card up", async (_, _) => await MoveCardAsync(card, card.ColumnId, Math.Max(0, card.Order - 1)), index > 0));
        menu.Items.Add(MenuItem("Lang_Board_MoveCardLater", "Move card down", async (_, _) => await MoveCardAsync(card, card.ColumnId, card.Order + 1), index >= 0 && index < siblings.Count - 1));
        var moveTo = new MenuItem { Header = Text("Lang_Board_MoveCardTo", "Move to column") };
        IEnumerable<ProjectBoardColumn> destinations = board == null ? Enumerable.Empty<ProjectBoardColumn>() : board.Columns.OrderBy(c => c.Order).Where(c => c.Id != card.ColumnId);
        foreach (var column in destinations)
        {
            var item = new MenuItem { Header = column.Title };
            item.Click += async (_, _) => await MoveCardAsync(card, column.Id, null);
            moveTo.Items.Add(item);
        }
        menu.Items.Add(moveTo);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Lang_Board_AddToTodo", "Copy to personal To-Do", async (_, _) => await AddToTodoAsync(card)));
        menu.Items.Add(MenuItem("Lang_Board_DeleteCard", "Move to recycle bin", async (_, _) => await DeleteCardAsync(card)));
        return menu;
    }

    private async void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not TextBlock handle || dragHandle != handle || dragCardId == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(handle);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (dragSnapshot == null || dragCardSnapshot == null) return;
        Guid cardId = dragCardId.Value;
        if (Mouse.Captured == handle) Mouse.Capture(null);
        dragHandle = null;
        dragCardId = null;
        var data = new DataObject(CardDragFormat, cardId.ToString("D"));
        isCardDragging = true;
        try { DragDrop.DoDragDrop(handle, data, DragDropEffects.Move); }
        finally
        {
            isCardDragging = false;
            ClearDragState();
        }
        await Task.CompletedTask;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        SetDragEffect(e);
    }

    private async void OnColumnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(CardDragFormat) || sender is not FrameworkElement target || target.Tag is not Guid targetColumn ||
            !TryGetDragSnapshot(e, out var snapshot, out var source)) return;
        if (dragBoardSnapshot is not { } capturedBoard) return;
        int order = RootCards(capturedBoard, targetColumn).Count(c => c.Id != source.Id);
        e.Handled = true;
        await MoveCardAsync(source, targetColumn, order, snapshot, capturedBoard);
    }

    private async void OnCardDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(CardDragFormat) || sender is not Border target || target.Tag is not ProjectBoardCard targetCard ||
            !TryGetDragSnapshot(e, out var snapshot, out var source) || source.Id == targetCard.Id) return;
        if (dragBoardSnapshot is not { } capturedBoard || capturedBoard.Cards.FirstOrDefault(c => c.Id == targetCard.Id && c.DeletedAt == null && c.ParentId == null) is not { } capturedTarget) return;
        var targetCards = RootCards(capturedBoard, capturedTarget.ColumnId).Where(c => c.Id != source.Id).ToList();
        int targetIndex = targetCards.FindIndex(c => c.Id == capturedTarget.Id);
        if (targetIndex < 0) return;
        if (e.GetPosition(target).Y >= target.ActualHeight / 2) targetIndex++;
        e.Handled = true;
        await MoveCardAsync(source, capturedTarget.ColumnId, targetIndex, snapshot, capturedBoard);
    }

    private bool TryGetDragSnapshot(DragEventArgs e, out BoardMutationSnapshot snapshot, out ProjectBoardCard source)
    {
        snapshot = default;
        source = null!;
        if (!Guid.TryParse(e.Data.GetData(CardDragFormat) as string, out Guid cardId) || dragSnapshot is not { } captured ||
            dragCardSnapshot is not { } capturedCard || dragBoardSnapshot == null || capturedCard.Id != cardId || capturedCard.DeletedAt != null) return false;
        snapshot = captured;
        source = capturedCard;
        return true;
    }

    private void ClearDragState()
    {
        dragHandle = null;
        dragCardId = null;
        dragSnapshot = null;
        dragCardSnapshot = null;
        dragBoardSnapshot = null;
    }

    internal async Task CreateCardAsync(Guid columnId)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || showRecycleBin || board == null || !board.Columns.Any(c => c.Id == columnId)) return;
        var draft = dialogService.EditCard(Window.GetWindow(this), Text("Lang_Board_CardAddTitle", "New board card"), null);
        if (draft == null) return;
        await MutateAsync("board.card.create", new
        {
            project = snapshot.Value.Project,
            revision = snapshot.Value.Revision,
            columnId = columnId.ToString("D"),
            draft.Title,
            draft.Description,
            draft.Priority,
            tags = draft.Tags,
            dueDate = draft.DueDate,
            actor = "local-user",
            host = "toolbox-ui",
            session = (string?)null
        });
    }

    internal async Task EditCardAsync(ProjectBoardCard card)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || card.DeletedAt != null) return;
        var currentCard = board?.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt == null);
        if (currentCard == null) return;
        var draft = dialogService.EditCard(Window.GetWindow(this), Text("Lang_Board_CardEditTitle", "Edit board card"), currentCard);
        if (draft == null) return;
        await MutateAsync("board.card.update", new
        {
            project = snapshot.Value.Project,
            revision = snapshot.Value.Revision,
            id = currentCard.Id.ToString("D"),
            draft.Title,
            draft.Description,
            draft.Priority,
            tags = draft.Tags,
            dueDate = draft.DueDate,
            actor = "local-user",
            host = "toolbox-ui",
            session = (string?)null
        });
    }

    private async Task RenameColumnAsync(ProjectBoardColumn column)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || showRecycleBin) return;
        string? title = dialogService.PromptText(Window.GetWindow(this), Text("Lang_Board_RenameColumn", "Rename column"), Text("Lang_Board_ColumnNamePrompt", "Enter a column name:"), column.Title);
        if (string.IsNullOrWhiteSpace(title)) return;
        await MutateAsync("board.column.update", new { project = snapshot.Value.Project, revision = snapshot.Value.Revision, id = column.Id.ToString("D"), title = title.Trim() });
    }

    private async Task MoveColumnAsync(ProjectBoardColumn column, int targetOrder)
    {
        if (board == null || isBoardLoading || showRecycleBin || targetOrder < 0 || targetOrder >= board.Columns.Count) return;
        await MutateAsync("board.column.move", new { project = selectedBoardId, revision = board.Revision, id = column.Id.ToString("D"), order = targetOrder });
    }

    private async Task DeleteColumnAsync(ProjectBoardColumn column)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || showRecycleBin || board == null) return;
        ProjectBoard currentBoard = board;
        var remaining = currentBoard.Columns.Where(c => c.Id != column.Id).OrderBy(c => c.Order).ToArray();
        if (remaining.Length == 0) return;
        if (!ProjectBoardDialogs.Confirm(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"),
            Text("Lang_Board_DeleteColumnPrompt", "Delete this column? Its cards will be transferred."), Text("Lang_Board_DeleteColumn", "Delete column"), danger: true)) return;
        var payload = new Dictionary<string, object?>
        {
            ["project"] = snapshot.Value.Project,
            ["revision"] = snapshot.Value.Revision,
            ["id"] = column.Id.ToString("D"),
            ["actor"] = "local-user",
            ["host"] = "toolbox-ui",
            ["session"] = (string?)null
        };
        if (currentBoard.Cards.Any(c => c.ColumnId == column.Id))
        {
            Guid? target = dialogService.ChooseColumn(Window.GetWindow(this), Text("Lang_Board_ChooseTargetColumn", "Choose a column for these cards"), remaining);
            if (target == null) return;
            payload["targetColumnId"] = target.Value.ToString("D");
        }
        await MutateAsync("board.column.delete", payload);
    }

    private async Task MoveCardAsync(ProjectBoardCard card, Guid targetColumnId, int? order, BoardMutationSnapshot? snapshot = null, ProjectBoard? sourceBoardSnapshot = null)
    {
        snapshot ??= CaptureBoardMutationSnapshot();
        ProjectBoard? capturedBoard = sourceBoardSnapshot ?? board;
        ProjectBoardCard? sourceCard = capturedBoard?.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt == null);
        if (snapshot == null || showRecycleBin || capturedBoard == null || sourceCard == null) return;
        if (!string.Equals(snapshot.Value.Project, capturedBoard.BoardId.ToString("D"), StringComparison.OrdinalIgnoreCase) || snapshot.Value.Revision != capturedBoard.Revision)
        {
            ShowOperationError(new BoardCallException("CONFLICT", Text("Lang_Board_SnapshotConflict", "The board changed. Close and reopen the details before retrying."), "revision"));
            return;
        }
        ProjectBoardColumn? targetColumn = capturedBoard.Columns.FirstOrDefault(c => c.Id == targetColumnId);
        if (targetColumn == null)
        {
            ShowOperationError(new BoardCallException("CONFLICT", Text("Lang_Board_SnapshotConflict", "The board changed. Close and reopen the details before retrying."), "columnId"));
            return;
        }
        string? transition = targetColumn.Status is { Length: > 0 } targetStatus && targetStatus != sourceCard.Status
            ? GetStatusTransition(sourceCard.Status, targetStatus)
            : null;
        if (targetColumn.Status is { Length: > 0 } mappedStatus && mappedStatus != sourceCard.Status && transition == null)
        {
            ShowOperationError(new InvalidOperationException(Text("Lang_Board_InvalidStatusMove", "This move cannot make that status transition.")));
            return;
        }
        if ((transition == "claim" && !CanClaim(sourceCard.Claim)) || ((transition is "release" or "submit") && !IsOwnedActiveClaim(sourceCard.Claim)))
        {
            ShowOperationError(new InvalidOperationException(Text("Lang_Board_ClaimHeld", "This card has another active claim.")));
            return;
        }
        var payload = new Dictionary<string, object?>
        {
            ["project"] = snapshot.Value.Project,
            ["revision"] = snapshot.Value.Revision,
            ["id"] = sourceCard.Id.ToString("D"),
            ["targetColumnId"] = targetColumnId.ToString("D"),
            ["actor"] = "local-user",
            ["host"] = "toolbox-ui",
            ["session"] = (string?)null
        };
        if (order.HasValue) payload["order"] = order.Value;
        if (transition != null)
        {
            payload["transition"] = transition;
            if (transition == "claim") payload["ttlMinutes"] = 30;
        }
        await MutateAsync("board.card.move", payload);
    }

    private async Task DeleteCardAsync(ProjectBoardCard card)
    {
        var snapshot = CaptureBoardMutationSnapshot();
        if (snapshot == null || isBoardLoading || showRecycleBin) return;
        var currentCard = board?.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt == null);
        if (currentCard == null) return;
        if (!ProjectBoardDialogs.Confirm(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"),
            string.Format(Text("Lang_Board_DeleteCardPrompt", "Move {0} to the recycle bin?"), currentCard.Title), Text("Lang_Board_DeleteCard", "Move to recycle bin"), danger: true)) return;
        await MutateAsync("board.card.delete", new { project = snapshot.Value.Project, revision = snapshot.Value.Revision, id = currentCard.Id.ToString("D"), actor = "local-user", host = "toolbox-ui", session = (string?)null });
    }

    private async Task RestoreCardAsync(ProjectBoardCard card)
    {
        if (board == null || isBoardLoading) return;
        var currentCard = board.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt != null);
        if (currentCard == null) return;
        await MutateAsync("board.card.restore", new { project = selectedBoardId, revision = board.Revision, id = currentCard.Id.ToString("D"), actor = "local-user", host = "toolbox-ui", session = (string?)null });
    }

    private async Task AddToTodoAsync(ProjectBoardCard card)
    {
        if (board == null || isBoardLoading || card.DeletedAt != null) return;
        try
        {
            var result = await ExecuteAsync<TodoLinkResult>("board.card.add-to-todo", new { project = selectedBoardId, revision = board.Revision, id = card.Id.ToString("D") });
            ProjectBoardDialogs.Notify(Window.GetWindow(this), Text("Lang_Page_Boards", "Project Board"),
                string.Format(Text("Lang_Board_AddedTodo", "Added to personal To-Do: {0}"), result.TodoId));
        }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private async Task MutateAsync(string operation, object payload)
    {
        if (!isLoaded) return;
        try
        {
            await ExecuteAsync<JsonElement>(operation, payload);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
            QueueRefresh();
        }
    }

    private async Task<T> ExecuteAsync<T>(string operation, object payload)
    {
        JsonElement data = await ExecuteElementAsync(operation, payload);
        return data.ValueKind == JsonValueKind.Undefined ? default! : data.Deserialize<T>(TaskProtocol.Options)
            ?? throw new InvalidOperationException("The project board returned an empty result.");
    }

    private async Task<JsonElement> ExecuteElementAsync(string operation, object payload)
    {
        var request = new TaskRequest
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
        };
        TaskResponse response = await commandService.ExecuteAsync(request);
        if (!response.Ok)
            throw new BoardCallException(response.Error?.Code ?? "BOARD_ERROR", response.Error?.Message ?? "The project board operation failed.", response.Error?.Field);
        return response.Data == null ? default : JsonSerializer.SerializeToElement(response.Data, TaskProtocol.Options);
    }

    private static string? GetStatusTransition(string current, string target) => (current, target) switch
    {
        ("notStarted", "inProgress") => "claim",
        ("inProgress", "notStarted") => "release",
        ("inProgress", "pendingReview") => "submit",
        ("pendingReview", "closed") => "close",
        ("pendingReview", "notStarted") or ("closed", "notStarted") => "reopen",
        _ => null
    };

    private void ShowOperationError(Exception exception)
    {
        SetStatus(exception.Message);
    }

    private void SetStatus(string text)
    {
        statusText.Text = text;
        statusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string Text(string key, string fallback) => ProjectBoardDialogs.GetText(this, key, fallback);

    private Button ResourceButton(string key, string fallback, RoutedEventHandler click, string style = "BtnDefault")
    {
        var button = CreateButton(Text(key, fallback), style);
        button.SetResourceReference(ContentControl.ContentProperty, key);
        button.Click += click;
        return button;
    }

    private Button CreateButton(string content, string style = "BtnDefault") => new()
    {
        Content = content,
        Style = TryFindResource(style) as Style,
        Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 30
    };

    private ContextMenu NewContextMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "BgElevatedBrush");
        menu.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        return menu;
    }

    private MenuItem MenuItem(string key, string fallback, RoutedEventHandler click, bool enabled = true)
    {
        var item = new MenuItem { Header = Text(key, fallback), IsEnabled = enabled };
        item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        item.Click += click;
        return item;
    }

    private TextBlock Label(string text, bool bold = false, bool tertiary = false)
    {
        var label = new TextBlock { Text = text, FontSize = tertiary ? 11 : 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, tertiary ? "TextTertiaryBrush" : "TextPrimaryBrush");
        return label;
    }

    private void SetDragEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(CardDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private int CountDescendants(Guid parentId)
    {
        if (board == null) return 0;
        var children = board.Cards.Where(c => c.DeletedAt == null && c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());
        var pending = new Stack<Guid>(children.TryGetValue(parentId, out var roots) ? roots : []);
        var seen = new HashSet<Guid>();
        int count = 0;
        while (pending.Count > 0)
        {
            Guid id = pending.Pop();
            if (!seen.Add(id)) continue;
            count++;
            if (children.TryGetValue(id, out var next)) foreach (Guid child in next) pending.Push(child);
        }
        return count;
    }

    private static List<ProjectBoardCard> RootCards(ProjectBoard sourceBoard, Guid columnId) => sourceBoard.Cards
        .Where(c => c.ColumnId == columnId && c.DeletedAt == null && c.ParentId == null)
        .OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();

    private static bool CanClaim(ProjectBoardClaim? claim) => claim == null || claim.ExpiresAt <= DateTime.UtcNow ||
        claim.ActorId == "local-user" && claim.HostId == "toolbox-ui" && claim.SessionId == null;

    private static bool IsOwnedActiveClaim(ProjectBoardClaim? claim) => claim != null && claim.ExpiresAt > DateTime.UtcNow &&
        claim.ActorId == "local-user" && claim.HostId == "toolbox-ui" && claim.SessionId == null;

    private int CountClosedDescendants(Guid parentId)
    {
        if (board == null) return 0;
        var byId = board.Cards.Where(c => c.DeletedAt == null).ToDictionary(c => c.Id);
        var children = board.Cards.Where(c => c.DeletedAt == null && c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());
        var pending = new Stack<Guid>(children.TryGetValue(parentId, out var roots) ? roots : []);
        var seen = new HashSet<Guid>();
        int closed = 0;
        while (pending.Count > 0)
        {
            Guid id = pending.Pop();
            if (!seen.Add(id)) continue;
            if (byId.TryGetValue(id, out var card) && card.Status == "closed") closed++;
            if (children.TryGetValue(id, out var next)) foreach (Guid child in next) pending.Push(child);
        }
        return closed;
    }

    private string ClaimOwner(ProjectBoardClaim claim) => claim.ActorId == "local-user" && claim.HostId == "toolbox-ui" && claim.SessionId == null
        ? Text("Lang_Board_ClaimedByYou", "Claimed by you")
        : $"{claim.ActorId} · {claim.HostId}{(claim.SessionId == null ? "" : " · " + claim.SessionId)}";

    private string ClaimSummary(ProjectBoardClaim claim) => string.Format(
        Text("Lang_Board_ClaimSummary", "{0} · {1}"), ClaimOwner(claim), RemainingLease(claim.ExpiresAt));

    private void RefreshLeaseDisplays()
    {
        if (!isLoaded || Dispatcher.HasShutdownStarted) return;
        foreach (var (label, claim) in leaseLabels) label.Text = ClaimSummary(claim);
    }

    private string RemainingLease(DateTime expiresAt)
    {
        TimeSpan remaining = expiresAt - DateTime.UtcNow;
        return remaining <= TimeSpan.Zero ? Text("Lang_Board_LeaseExpired", "lease expired") : string.Format(
            Text("Lang_Board_LeaseRemaining", "{0}m left"), Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
    }

    private Border StatusBadge(string status)
    {
        var (key, fallback, brush) = status switch
        {
            "inProgress" => ("Lang_Board_StatusInProgress", "In progress", "AccentSoftBrush"),
            "pendingReview" => ("Lang_Board_StatusPendingReview", "Pending review", "WarningSoftBrush"),
            "closed" => ("Lang_Board_StatusClosed", "Closed", "BgTertiaryBrush"),
            _ => ("Lang_Board_StatusNotStarted", "Not started", "BgTertiaryBrush")
        };
        var label = Label(Text(key, fallback), tertiary: true);
        label.FontWeight = FontWeights.SemiBold;
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        label.Margin = new Thickness(0);
        var badge = new Border { Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 8, 2), CornerRadius = new CornerRadius(4), Child = label };
        badge.SetResourceReference(Border.BackgroundProperty, brush);
        return badge;
    }

    private async Task OpenCardDetailsAsync(ProjectBoardCard card)
    {
        if (board is not { } current || showRecycleBin || selectedBoardId == null || isBoardLoading) return;
        var registration = projects.FirstOrDefault(p => p.BoardId == current.BoardId);
        if (registration == null) return;
        var currentCard = current.Cards.FirstOrDefault(c => c.Id == card.Id && c.DeletedAt == null && c.ParentId == null);
        if (currentCard == null) return;
        var snapshot = new BoardDetailsSnapshot(current.BoardId.ToString("D"), current.Revision, current.CommentRevision);
        var dialog = new BoardCardDetailsWindow(Window.GetWindow(this), current, registration, currentCard, snapshot);
        dialog.ShowDialog();
        await Task.CompletedTask;
        QueueRefresh();
    }

    private sealed record TodoLinkResult(string TodoId);
}

internal interface IBoardDialogService
{
    BoardCardDraft? EditCard(Window? owner, string title, ProjectBoardCard? card);
    string? PromptText(Window? owner, string title, string prompt, string initialValue = "");
    Guid? ChooseColumn(Window? owner, string title, IReadOnlyList<ProjectBoardColumn> columns);
}

internal sealed class WpfBoardDialogService : IBoardDialogService
{
    public BoardCardDraft? EditCard(Window? owner, string title, ProjectBoardCard? card) => ProjectBoardDialogs.EditCard(owner, title, card);
    public string? PromptText(Window? owner, string title, string prompt, string initialValue = "") => ProjectBoardDialogs.PromptText(owner, title, prompt, initialValue);
    public Guid? ChooseColumn(Window? owner, string title, IReadOnlyList<ProjectBoardColumn> columns) => ProjectBoardDialogs.ChooseColumn(owner, title, columns);
}

internal readonly record struct BoardMutationSnapshot(string Project, long Revision);

internal sealed class BoardCallException(string code, string message, string? field) : Exception(message)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;
}
