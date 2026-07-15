using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;

namespace ToolBox.Views.Pomodoro;

/// <summary>番茄钟视图 (P3-04)</summary>
public partial class PomodoroView : UserControl
{
    private readonly ToolBox.Core.Pomodoro.PomodoroService _service = ToolBox.Core.Pomodoro.PomodoroService.Instance;
    private TextBlock? _timerText;
    private TextBlock? _modeLabel;

    public PomodoroView()
    {
        InitializeComponent();
        _service.Tick += OnTick;
        _service.Completed += OnCompleted;
        UpdateUI();
    }

    private void InitializeComponent()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new TextBlock
        {
            Text = "番茄钟", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0);

        // Timer display
        var timerPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _timerText = new TextBlock
        {
            Text = "25:00", FontSize = 48, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("AccentBrush")
        };
        timerPanel.Children.Add(_timerText);
        _modeLabel = new TextBlock
        {
            Text = "专注模式", FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0)
        };
        timerPanel.Children.Add(_modeLabel);
        Grid.SetRow(timerPanel, 1);

        // Controls
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var btnStart = new Button { Content = "开始专注", Style = (Style)FindResource("BtnPrimary"), Margin = new Thickness(0, 0, 8, 0) };
        btnStart.Click += (s, e) => _service.StartFocus();
        var btnBreak = new Button { Content = "开始休息", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        btnBreak.Click += (s, e) => _service.StartBreak();
        var btnStop = new Button { Content = "停止", Style = (Style)FindResource("IconButton") };
        btnStop.Click += (s, e) => _service.Stop();
        controls.Children.Add(btnStart);
        controls.Children.Add(btnBreak);
        controls.Children.Add(btnStop);
        Grid.SetRow(controls, 2);

        grid.Children.Add(header);
        grid.Children.Add(timerPanel);
        grid.Children.Add(controls);
        Content = grid;
    }

    private void OnTick()
    {
        _timerText?.Dispatcher.Invoke(UpdateUI);
    }

    private void OnCompleted(bool isFocus)
    {
        try
        {
            if (isFocus) System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    private void UpdateUI()
    {
        if (_timerText != null)
        {
            var ts = TimeSpan.FromSeconds(_service.RemainingSeconds);
            _timerText.Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        if (_modeLabel != null)
            _modeLabel.Text = _service.IsFocusMode ? "专注模式" : "休息模式";
    }
}
