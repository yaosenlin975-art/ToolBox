using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Boards;

namespace ToolBox.Views.Boards;

internal sealed record BoardCardDraft(string Title, string Description, int Priority, IReadOnlyList<string> Tags, DateTime? DueDate);
internal enum BoardInitializationChoice { Cancel, RegisterOnly, Initialize }

internal static class ProjectBoardDialogs
{
    public static BoardCardDraft? EditCard(Window? owner, string title, ProjectBoardCard? card)
    {
        string Text(string key, string fallback) => GetText(owner, key, fallback);
        var window = CreateWindow(owner, title, 560);
        var root = NewGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));

        var form = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };
        var titleInput = TextInput(card?.Title ?? "", 34);
        var descriptionInput = TextInput(card?.Description ?? "", 110);
        descriptionInput.AcceptsReturn = true;
        descriptionInput.TextWrapping = TextWrapping.Wrap;
        descriptionInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var priorityInput = new ComboBox { Height = 32, SelectedIndex = card?.Priority ?? 0, Margin = new Thickness(0, 4, 0, 10) };
        priorityInput.Items.Add(Text("Lang_Board_Normal", "Normal"));
        priorityInput.Items.Add(Text("Lang_Board_Important", "Important"));
        priorityInput.Items.Add(Text("Lang_Board_Urgent", "Urgent"));
        var tagsInput = TextInput(card == null ? "" : string.Join(Environment.NewLine, card.Tags), 70);
        tagsInput.AcceptsReturn = true;
        tagsInput.TextWrapping = TextWrapping.Wrap;
        tagsInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var dueDate = new DatePicker { SelectedDate = card?.DueDate?.ToLocalTime().Date, Margin = new Thickness(0, 4, 8, 0) };
        dueDate.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        dueDate.SetResourceReference(Control.BackgroundProperty, "BgSunkenBrush");
        var clearDate = CreateButton(Text("Lang_Board_ClearDate", "Clear date"));
        clearDate.Click += (_, _) => dueDate.SelectedDate = null;
        var error = InlineError();

        AddField(form, Text("Lang_Board_Title", "Title"), titleInput);
        AddField(form, Text("Lang_Board_Description", "Description"), descriptionInput);
        AddField(form, Text("Lang_Board_Priority", "Priority"), priorityInput);
        AddField(form, Text("Lang_Board_Tags", "Tags"), tagsInput);
        form.Children.Add(Label(Text("Lang_Board_TagsHint", "One tag per line"), tertiary: true));
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        dateRow.Children.Add(dueDate);
        dateRow.Children.Add(clearDate);
        AddField(form, Text("Lang_Board_DueDate", "Due date"), dateRow);
        form.Children.Add(error);
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 590, Content = form };
        Grid.SetRow(formScroll, 1);
        root.Children.Add(formScroll);

        var footer = Footer();
        var cancel = CreateButton(Text("Lang_Board_Cancel", "Cancel"));
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = CreateButton(Text("Lang_Board_Save", "Save"), "BtnPrimary");
        save.IsDefault = true;
        void Submit()
        {
            if (string.IsNullOrWhiteSpace(titleInput.Text))
            {
                error.Text = Text("Lang_Board_TitleRequired", "Enter a title.");
                titleInput.Focus();
                return;
            }
            string[] tags = tagsInput.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            window.Tag = new BoardCardDraft(titleInput.Text.Trim(), descriptionInput.Text, priorityInput.SelectedIndex,
                tags, dueDate.SelectedDate?.Date.ToUniversalTime());
            window.DialogResult = true;
        }
        save.Click += (_, _) => Submit();
        titleInput.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        window.Loaded += (_, _) => titleInput.Focus();
        return window.ShowDialog() == true ? (BoardCardDraft?)window.Tag : null;
    }

    public static Guid? ChooseColumn(Window? owner, string title, IReadOnlyList<ProjectBoardColumn> columns)
    {
        var window = CreateWindow(owner, title, 420);
        var root = NewGrid();
        root.Margin = new Thickness(20);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var selector = new ComboBox { ItemsSource = columns, DisplayMemberPath = "Title", SelectedValuePath = "Id", Margin = new Thickness(0, 18, 0, 16), Height = 34 };
        if (columns.Count > 0) selector.SelectedValue = columns[0].Id;
        Grid.SetRow(selector, 1);
        root.Children.Add(selector);
        var footer = Footer();
        var cancel = CreateButton(GetText(owner, "Lang_Board_Cancel", "Cancel"));
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var choose = CreateButton(GetText(owner, "Lang_Board_Save", "Select"), "BtnPrimary");
        choose.IsDefault = true;
        choose.Click += (_, _) => { if (selector.SelectedValue is Guid) window.DialogResult = true; };
        footer.Children.Add(cancel);
        footer.Children.Add(choose);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        return window.ShowDialog() == true && selector.SelectedValue is Guid id ? id : null;
    }

    public static string? PromptText(Window? owner, string title, string prompt, string initialValue = "")
    {
        string Text(string key, string fallback) => GetText(owner, key, fallback);
        var window = CreateWindow(owner, title, 420);
        var root = NewGrid();
        root.Margin = new Thickness(18);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 10) };
        fields.Children.Add(Label(prompt));
        var input = TextInput(initialValue, 34);
        input.MinHeight = 34;
        fields.Children.Add(input);
        var error = InlineError();
        fields.Children.Add(error);
        Grid.SetRow(fields, 1);
        root.Children.Add(fields);
        var footer = Footer();
        var cancel = CreateButton(Text("Lang_Board_Cancel", "Cancel"));
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = CreateButton(Text("Lang_Board_Save", "Save"), "BtnPrimary");
        save.IsDefault = true;
        void Submit()
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                error.Text = Text("Lang_Board_TitleRequired", "Enter a value.");
                input.Focus();
                return;
            }
            window.Tag = input.Text.Trim();
            window.DialogResult = true;
        }
        save.Click += (_, _) => Submit();
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? window.Tag as string : null;
    }

    public static BoardInitializationChoice ChooseInitialization(Window? owner, string title, string message)
    {
        var window = CreateWindow(owner, title, 500);
        var root = NewGrid();
        root.Margin = new Thickness(20);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var text = Label(message);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 12, 0, 18);
        Grid.SetRow(text, 1);
        root.Children.Add(text);
        var footer = Footer();
        var cancel = CreateButton(GetText(owner, "Lang_Board_Cancel", "Cancel"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => window.Tag = BoardInitializationChoice.Cancel;
        var register = CreateButton(GetText(owner, "Lang_Board_RegisterOnly", "Register only"));
        register.Click += (_, _) => { window.Tag = BoardInitializationChoice.RegisterOnly; window.DialogResult = true; };
        var initialize = CreateButton(GetText(owner, "Lang_Board_Initialize", "Initialize"), "BtnPrimary");
        initialize.Click += (_, _) => { window.Tag = BoardInitializationChoice.Initialize; window.DialogResult = true; };
        footer.Children.Add(cancel);
        footer.Children.Add(register);
        footer.Children.Add(initialize);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        return window.ShowDialog() == true && window.Tag is BoardInitializationChoice choice ? choice : BoardInitializationChoice.Cancel;
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmLabel, bool danger = false)
    {
        var window = CreateWindow(owner, title, 500);
        var root = NewGrid();
        root.Margin = new Thickness(20);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var text = Label(message);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 12, 0, 18);
        Grid.SetRow(text, 1);
        root.Children.Add(text);
        var footer = Footer();
        var cancel = CreateButton(GetText(owner, "Lang_Board_Cancel", "Cancel"));
        cancel.IsCancel = true;
        cancel.IsDefault = false;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var confirm = CreateButton(confirmLabel, danger ? "BtnDefault" : "BtnPrimary");
        if (danger) confirm.SetResourceReference(Control.ForegroundProperty, "ErrorBrush");
        confirm.IsDefault = !danger;
        confirm.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        return window.ShowDialog() == true;
    }

    public static void Notify(Window? owner, string title, string message)
    {
        var window = CreateWindow(owner, title, 500);
        var root = NewGrid();
        root.Margin = new Thickness(20);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var text = Label(message);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 12, 0, 18);
        Grid.SetRow(text, 1);
        root.Children.Add(text);
        var footer = Footer();
        var ok = CreateButton(GetText(owner, "Lang_OK", "OK"), "BtnPrimary");
        ok.IsDefault = true;
        ok.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(ok);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        window.ShowDialog();
    }

    private static Window CreateWindow(Window? owner, string title, double width) => new()
    {
        Title = title,
        Owner = owner,
        Width = width,
        SizeToContent = SizeToContent.Height,
        MaxHeight = 760,
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false
    };

    private static Grid NewGrid() => new() { Background = Brushes.Transparent };

    private static Border CreateShell(UIElement content)
    {
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = content };
        border.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        return border;
    }

    private static UIElement CreateHeader(Window window, string title)
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label(title, bold: true);
        label.Margin = new Thickness(16, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        var close = CreateButton("✕", "IconButton");
        close.Width = 30;
        close.Height = 30;
        close.Margin = new Thickness(0, 0, 6, 0);
        close.IsCancel = true;
        close.Click += (_, _) => window.DialogResult = false;
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        grid.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) window.DragMove(); };
        return grid;
    }

    private static StackPanel Footer() => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };

    private static TextBlock InlineError()
    {
        var error = Label("");
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        error.TextWrapping = TextWrapping.Wrap;
        error.Margin = new Thickness(0, 4, 0, 2);
        return error;
    }

    private static void AddField(Panel panel, string label, UIElement input)
    {
        panel.Children.Add(Label(label));
        panel.Children.Add(input);
    }

    private static TextBlock Label(string text, bool bold = false, bool tertiary = false)
    {
        var label = new TextBlock { Text = text, FontSize = tertiary ? 11 : 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 8, 0, 2) };
        label.SetResourceReference(TextBlock.ForegroundProperty, tertiary ? "TextTertiaryBrush" : "TextPrimaryBrush");
        return label;
    }

    private static TextBox TextInput(string value, double height)
    {
        var input = new TextBox { Text = value, MinHeight = height, Padding = new Thickness(8, 6, 8, 6), FontSize = 13, BorderThickness = new Thickness(1) };
        input.SetResourceReference(Control.BackgroundProperty, "BgSunkenBrush");
        input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        input.SetResourceReference(Control.BorderBrushProperty, "BorderDefaultBrush");
        input.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimaryBrush");
        return input;
    }

    internal static Button CreateButton(string text, string style = "BtnDefault") => new()
    {
        Content = text,
        Style = Application.Current?.TryFindResource(style) as Style,
        Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 30,
        Margin = new Thickness(4, 0, 0, 0)
    };

    internal static string GetText(FrameworkElement? owner, string key, string fallback) =>
        owner?.TryFindResource(key) as string ?? Application.Current?.TryFindResource(key) as string ?? fallback;
}
