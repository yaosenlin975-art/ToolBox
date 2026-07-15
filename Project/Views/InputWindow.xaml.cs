using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ToolBox.Core.Todo;

namespace ToolBox.Views;

public partial class InputWindow : Window
{
    public string Value { get; private set; } = "";
    public int Priority { get; private set; }
    public string Category { get; private set; } = "默认";
    public DateTime? DueDate { get; private set; }
    private DispatcherTimer? dateParseDebounce;
    private DateTime? parsedDate;

    public InputWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        TitleDisplay.Text = title;
        Prompt.Text = prompt;
        Input.Focus();

        dateParseDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        dateParseDebounce.Tick += (s, e) =>
        {
            dateParseDebounce.Stop();
            ParseDateFromInput();
        };
    }

    public void ShowTodoFields()
    {
        TodoExtraPanel.Visibility = Visibility.Visible;
        CategoryInput.Text = "未分类";
        UpdateSuggestions("");
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CategoryInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSuggestions(CategoryInput.Text.Trim());
    }

    private void UpdateSuggestions(string filter)
    {
        var cats = TodoStore.Instance.Categories
            .Where(c => string.IsNullOrEmpty(filter) || c.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cats.Count == 0 || (cats.Count == 1 && cats[0] == filter))
        {
            CategorySuggestions.Visibility = Visibility.Collapsed;
            return;
        }
        CategorySuggestions.ItemsSource = cats;
        CategorySuggestions.Visibility = Visibility.Visible;
    }

    private void CategorySuggestion_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CategorySuggestions.SelectedItem is string cat)
        {
            CategoryInput.Text = cat;
            CategoryInput.CaretIndex = cat.Length;
            CategorySuggestions.Visibility = Visibility.Collapsed;
        }
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        dateParseDebounce?.Stop();
        dateParseDebounce?.Start();
    }

    private void ParseDateFromInput()
    {
        var text = Input.Text ?? string.Empty;
        var result = SmartDateParser.Parse(text);

        if (result.HasDate)
        {
            parsedDate = result.ParsedDate;
            var dateStr = result.ParsedDate!.Value.ToString("yyyy-MM-dd HH:mm");
            txtDateSuggestion.Text = $"识别到日期: {dateStr} ({result.MatchedText})";
            DateSuggestionPanel.Visibility = Visibility.Visible;

            // Auto-fill DatePicker if showing
            if (TodoExtraPanel.Visibility == Visibility.Visible && DueDatePicker.SelectedDate == null)
            {
                DueDatePicker.SelectedDate = result.ParsedDate!.Value.Date;
            }
        }
        else
        {
            parsedDate = null;
            DateSuggestionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnClearDate_Click(object sender, RoutedEventArgs e)
    {
        parsedDate = null;
        DateSuggestionPanel.Visibility = Visibility.Collapsed;
        DueDatePicker.SelectedDate = null;
    }

    private void CollectResult()
    {
        Value = Input.Text;
        if (PriHigh.IsChecked == true) Priority = 2;
        else if (PriMid.IsChecked == true) Priority = 1;
        var cat = CategoryInput.Text.Trim();
        Category = string.IsNullOrEmpty(cat) || cat == "未分类" ? "默认" : cat;

        // Use DatePicker value if set, otherwise use parsed date from input text
        DueDate = DueDatePicker.SelectedDate ?? parsedDate;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CollectResult();
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        CollectResult();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}