using System.Threading.Tasks;
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
    public ERepeatFrequency? RepeatFrequency { get; private set; }
    public int ReminderHour { get; private set; } = 9;
    public int ReminderMinute { get; private set; } = 0;
    public int? RepeatDayOfWeek { get; private set; }
    public int? RepeatDayOfMonth { get; private set; }
    public int? RepeatMonth { get; private set; }
    public int? RepeatDay { get; private set; }
    private DispatcherTimer? dateParseDebounce;
    private DateTime? parsedDate;
    private TaskCompletionSource<bool>? _tcs;

    /// <summary>
    /// 非模态显示（主窗口保持可点击），等待用户确认/取消后返回结果。
    /// </summary>
    public Task<bool> ShowNonModalAsync(Window? owner)
    {
        _tcs = new TaskCompletionSource<bool>();
        Owner = owner;
        Closed += (_, _) => _tcs?.TrySetResult(false);
        Show();
        return _tcs.Task;
    }

    private void Finish(bool ok)
    {
        if (ok) CollectResult();
        if (_tcs is not null)
        {
            _tcs.TrySetResult(ok);
            Close();
        }
        else
        {
            DialogResult = ok;
        }
    }

    public InputWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        TitleDisplay.Text = title;
        Prompt.Text = prompt;
        Input.Focus();
        InitializeRepeatComboBoxes();

        dateParseDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        dateParseDebounce.Tick += (s, e) =>
        {
            dateParseDebounce.Stop();
            ParseDateFromInput();
        };
    }

    private void InitializeRepeatComboBoxes()
    {
        // 每月：1~31号
        for (int i = 1; i <= 31; i++)
            MonthlyDayCombo.Items.Add(new ComboBoxItem { Content = i.ToString() });

        // 每年：月份
        var monthNames = new[] { "1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月" };
        foreach (var name in monthNames)
            YearlyMonthCombo.Items.Add(new ComboBoxItem { Content = name });

        // 每年：日期（动态，跟随月份变化）
        RefreshYearlyDayItems();
        YearlyMonthCombo.SelectionChanged += (_, _) => RefreshYearlyDayItems();
    }

    private void RefreshYearlyDayItems()
    {
        int month = YearlyMonthCombo.SelectedIndex + 1;
        if (month < 1 || month > 12) month = 1;
        int maxDay = DateTime.DaysInMonth(DateTime.Now.Year, month);
        int prevSelected = YearlyDayCombo.SelectedIndex + 1;

        YearlyDayCombo.Items.Clear();
        for (int i = 1; i <= maxDay; i++)
            YearlyDayCombo.Items.Add(new ComboBoxItem { Content = i.ToString() });

        if (prevSelected > 0 && prevSelected <= maxDay)
            YearlyDayCombo.SelectedIndex = prevSelected - 1;
        else
            YearlyDayCombo.SelectedIndex = maxDay - 1;
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

        // Read repeat frequency
        RepeatFrequency = RepeatCombo.SelectedIndex switch
        {
            1 => ERepeatFrequency.Daily,
            2 => ERepeatFrequency.Weekly,
            3 => ERepeatFrequency.Monthly,
            4 => ERepeatFrequency.Yearly,
            _ => null
        };

        // Read reminder time (only when repeating)
        if (RepeatFrequency.HasValue)
        {
            int.TryParse(ReminderHourInput.Text.Trim(), out var h);
            int.TryParse(ReminderMinuteInput.Text.Trim(), out var m);
            ReminderHour = Math.Clamp(h, 0, 23);
            ReminderMinute = Math.Clamp(m, 0, 59);
        }

        // Read specific day selections
        if (RepeatFrequency == ERepeatFrequency.Weekly)
        {
            int dow = WeeklyDayCombo.SelectedIndex + 1;
            RepeatDayOfWeek = dow >= 1 && dow <= 7 ? dow : null;
        }
        if (RepeatFrequency == ERepeatFrequency.Monthly)
        {
            int dom = MonthlyDayCombo.SelectedIndex + 1;
            RepeatDayOfMonth = dom >= 1 && dom <= 31 ? dom : null;
        }
        if (RepeatFrequency == ERepeatFrequency.Yearly)
        {
            int m = YearlyMonthCombo.SelectedIndex + 1;
            int d = YearlyDayCombo.SelectedIndex + 1;
            RepeatMonth = m >= 1 && m <= 12 ? m : null;
            RepeatDay = d >= 1 && d <= 31 ? d : null;
        }
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Finish(true);
        }
        else if (e.Key == Key.Escape)
        {
            Finish(false);
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        Finish(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);
    private void OnClose(object sender, RoutedEventArgs e) => Finish(false);

    private void RepeatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatTimePanel != null)
            RepeatTimePanel.Visibility = RepeatCombo.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (WeeklyDayPanel != null)
            WeeklyDayPanel.Visibility = RepeatCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (MonthlyDayPanel != null)
            MonthlyDayPanel.Visibility = RepeatCombo.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (YearlyDatePanel != null)
            YearlyDatePanel.Visibility = RepeatCombo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }
}
