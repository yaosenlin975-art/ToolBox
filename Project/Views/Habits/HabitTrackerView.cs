using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;

namespace ToolBox.Views.Habits;

/// <summary>习惯打卡视图 (P3-05)</summary>
public partial class HabitTrackerView : UserControl
{
    private readonly ToolBox.Core.Habits.HabitStore _store = ToolBox.Core.Habits.HabitStore.Instance;

    public HabitTrackerView()
    {
        InitializeComponent();
        _store.HabitsChanged += () => Dispatcher.Invoke(RefreshHabits);
        RefreshHabits();
    }

    private void InitializeComponent()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock
        {
            Text = "习惯打卡", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var btnAdd = new Button
        {
            Content = "+ 新建习惯", Style = (Style)FindResource("BtnPrimary"),
            Margin = new Thickness(16, 0, 0, 0)
        };
        btnAdd.Click += BtnAdd_Click;
        header.Children.Add(title);
        header.Children.Add(btnAdd);
        Grid.SetRow(header, 0);

        // Stats
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var totalLabel = new TextBlock
        {
            Text = "0 个习惯", FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        };
        stats.Children.Add(totalLabel);
        Grid.SetRow(stats, 1);

        // Habit list
        var listPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var scroll = new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);

        grid.Children.Add(header);
        grid.Children.Add(stats);
        grid.Children.Add(scroll);
        Content = grid;
    }

    private void RefreshHabits()
    {
        if (Content is Grid grid)
        {
            var scroll = grid.Children.OfType<ScrollViewer>().FirstOrDefault();
            if (scroll?.Content is WrapPanel panel)
            {
                panel.Children.Clear();
                foreach (var habit in _store.Habits)
                {
                    panel.Children.Add(CreateHabitCard(habit));
                }
            }
        }
    }

    private UIElement CreateHabitCard(ToolBox.Core.Habits.HabitEntry habit)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BgSunkenBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(4),
            Width = 180
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = habit.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"连续 {habit.CurrentStreak} 天 | 共 {habit.TotalCheckIns} 次",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 0)
        });

        var checkBtn = new Button
        {
            Content = habit.IsCheckedInToday ? "✅ 已打卡" : "⭕ 打卡",
            Style = (Style)FindResource(habit.IsCheckedInToday ? "OutlineButton" : "BtnPrimary"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        checkBtn.Click += (s, e) =>
        {
            if (habit.IsCheckedInToday)
                _store.UncheckIn(habit.Id);
            else
                _store.CheckIn(habit.Id);
        };
        stack.Children.Add(checkBtn);

        card.Child = stack;
        return card;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "新建习惯",
            Width = 360,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("BgBrush")
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "习惯名称", FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 4)
        });
        var txtName = new TextBox
        {
            Background = (Brush)FindResource("BgSunkenBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(txtName);

        panel.Children.Add(new TextBlock
        {
            Text = "频率", FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 4)
        });
        var cmbFreq = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        cmbFreq.Items.Add("每天");
        cmbFreq.Items.Add("每周");
        cmbFreq.SelectedIndex = 0;
        panel.Children.Add(cmbFreq);

        var btnSave = new Button
        {
            Content = "保存",
            Style = (Style)FindResource("BtnPrimary"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnSave.Click += (s, ev) =>
        {
            if (!string.IsNullOrWhiteSpace(txtName.Text))
            {
                _store.Add(new ToolBox.Core.Habits.HabitEntry
                {
                    Name = txtName.Text.Trim(),
                    Frequency = cmbFreq.SelectedIndex == 0 ? "daily" : "weekly"
                });
                dialog.Close();
            }
        };
        panel.Children.Add(btnSave);

        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
