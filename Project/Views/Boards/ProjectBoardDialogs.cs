using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Boards;

namespace ToolBox.Views.Boards;

internal sealed record BoardCardDraft(string Title, string Description, int Priority, IReadOnlyList<string> Tags, DateTime? DueDate);

internal static class ProjectBoardDialogs
{
    public static BoardCardDraft? EditCard(Window? owner, string title, ProjectBoardCard? card)
    {
        string Text(string key, string fallback) => GetText(owner, key, fallback);
        var window = CreateWindow(owner, title, 560);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = CreateHeader(window, title);
        root.Children.Add(header);

        var form = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
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

        AddField(form, Text("Lang_Board_Title", "Title"), titleInput);
        AddField(form, Text("Lang_Board_Description", "Description"), descriptionInput);
        AddField(form, Text("Lang_Board_Priority", "Priority"), priorityInput);
        AddField(form, Text("Lang_Board_Tags", "Tags"), tagsInput);
        form.Children.Add(Label(Text("Lang_Board_TagsHint", "One tag per line"), tertiary: true));
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        dateRow.Children.Add(dueDate);
        dateRow.Children.Add(clearDate);
        AddField(form, Text("Lang_Board_DueDate", "Due date"), dateRow);
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 590, Content = form };
        Grid.SetRow(formScroll, 1);
        root.Children.Add(formScroll);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        var cancel = CreateButton(Text("Lang_Board_Cancel", "Cancel"));
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => window.DialogResult = false;
        var save = CreateButton(Text("Lang_Board_Save", "Save"), "BtnPrimary");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleInput.Text))
            {
                MessageBox.Show(window, Text("Lang_Board_TitleRequired", "Enter a title."), title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                titleInput.Focus();
                return;
            }
            string[] tags = tagsInput.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            window.Tag = new BoardCardDraft(titleInput.Text.Trim(), descriptionInput.Text, priorityInput.SelectedIndex,
                tags, dueDate.SelectedDate?.Date.ToUniversalTime());
            window.DialogResult = true;
        };
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
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var selector = new ComboBox { ItemsSource = columns, DisplayMemberPath = "Title", SelectedValuePath = "Id", Margin = new Thickness(0, 18, 0, 16), Height = 34 };
        if (columns.Count > 0) selector.SelectedValue = columns[0].Id;
        Grid.SetRow(selector, 1);
        root.Children.Add(selector);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton(GetText(owner, "Lang_Board_Cancel", "Cancel"));
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => window.DialogResult = false;
        var choose = CreateButton(GetText(owner, "Lang_Board_Save", "Select"), "BtnPrimary");
        choose.Click += (_, _) =>
        {
            if (selector.SelectedValue is Guid) window.DialogResult = true;
        };
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
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader(window, title));
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 16) };
        fields.Children.Add(Label(prompt));
        var input = TextInput(initialValue, 34);
        input.MinHeight = 34;
        fields.Children.Add(input);
        Grid.SetRow(fields, 1);
        root.Children.Add(fields);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton(Text("Lang_Board_Cancel", "Cancel"));
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => window.DialogResult = false;
        var save = CreateButton(Text("Lang_Board_Save", "Save"), "BtnPrimary");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(window, Text("Lang_Board_TitleRequired", "Enter a value."), title, MessageBoxButton.OK, MessageBoxImage.Information);
                input.Focus();
                return;
            }
            window.Tag = input.Text.Trim();
            window.DialogResult = true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        window.Content = CreateShell(root);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? window.Tag as string : null;
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
        close.Click += (_, _) => window.DialogResult = false;
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) window.DragMove();
        };
        return grid;
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

    private static Button CreateButton(string text, string style = "BtnDefault")
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), MinHeight = 30, Style = Application.Current?.TryFindResource(style) as Style };
        return button;
    }

    internal static string GetText(FrameworkElement? owner, string key, string fallback) =>
        owner?.TryFindResource(key) as string ?? Application.Current?.TryFindResource(key) as string ?? fallback;
}
