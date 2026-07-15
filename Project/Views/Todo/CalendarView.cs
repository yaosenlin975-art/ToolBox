using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ToolBox.Views.Todo;

/// <summary>日历视图 (P2-01) - 代码后置文件</summary>
public partial class CalendarView : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime), typeof(CalendarView),
            new PropertyMetadata(DateTime.Today));

    public DateTime SelectedDate
    {
        get => (DateTime)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    private DateTime _currentMonth = DateTime.Today;

    public CalendarView()
    {
        InitializeComponent();
        BtnPrev.Click += (s, e) => BuildMonthGrid(_currentMonth.AddMonths(-1));
        BtnNext.Click += (s, e) => BuildMonthGrid(_currentMonth.AddMonths(1));
        BtnToday.Click += (s, e) => BuildMonthGrid(DateTime.Today);
        BuildMonthGrid(_currentMonth);
    }

    private void BuildMonthGrid(DateTime month)
    {
        MonthLabel.Text = month.ToString("yyyy年MM月");
        _currentMonth = month;
        MonthGrid.Children.Clear();

        var firstDay = new DateTime(month.Year, month.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var startOffset = (int)firstDay.DayOfWeek;

        for (int i = 0; i < startOffset; i++)
            MonthGrid.Children.Add(new Border { Width = 48, Height = 44 });

        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(month.Year, month.Month, day);
            var dayCell = new Border
            {
                Width = 48, Height = 44,
                Background = date == DateTime.Today ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent,
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1)
            };
            var cellStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            cellStack.Children.Add(new TextBlock
            {
                Text = day.ToString(), FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            var todoCount = GetTodoCount(date);
            if (todoCount > 0)
                cellStack.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 2, 0, 0), Fill = (Brush)FindResource("AccentBrush") });
            dayCell.Child = cellStack;
            dayCell.MouseLeftButtonDown += (s, e) => SelectedDate = date;
            MonthGrid.Children.Add(dayCell);
        }
    }

    private static int GetTodoCount(DateTime date)
    {
        // Mock data - query TodoStore in real implementation
        return date.Day % 3 == 0 ? 1 : 0;
    }
}
