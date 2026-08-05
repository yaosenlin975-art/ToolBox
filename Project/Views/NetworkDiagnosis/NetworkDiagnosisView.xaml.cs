using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ToolBox.Views.NetworkDiagnosis;

/// <summary>
/// 网络诊断主视图（T3 外壳）。
/// 布局遵循 PRD 5.2：状态区 → 操作区 → 检测结果列表（行展开）→ 修复区 → 日志区（折叠）。
/// 状态机遵循 PRD 5.3：Idle → Diagnosing → Healthy / IssuesFound。
/// 双主题（AC-10）：代码构建元素的画刷一律 SetResourceReference 动态资源，随主题切换自动更新。
/// 语言包（AC-11）：订阅资源字典变更，语言切换后状态区/结果行/日志/修复区整体重渲染。
/// 诊断运行由 DemoDiagnosisRunner 提供占位探测，T1 Checker 注册表落地后替换。
/// </summary>
public partial class NetworkDiagnosisView : UserControl
{
    private enum RepairUiState
    {
        Hidden,
        Ready,
        Executing,
        Done
    }

    private readonly List<DiagnosticItem> items = new();
    private readonly List<LogEntry> logEntries = new();
    private List<RepairStep> repairSteps = new();
    private bool repairNeedsRestart;
    private DiagnosisSessionStatus sessionStatus = DiagnosisSessionStatus.Idle;
    private CancellationTokenSource? runCts;
    private RepairUiState repairState = RepairUiState.Hidden;
    private int repairCount;

    private sealed record LogEntry(string Time, string Key, object[] Args);

    public NetworkDiagnosisView()
    {
        InitializeComponent();
        // 语言切换(App.ApplyLanguage)后整体重渲染状态区/结果行/日志/修复区，与 XAML DynamicResource 文案保持一致（AC-11）
        App.LanguageChanged += OnLanguageChanged;
        ApplySessionStatus(DiagnosisSessionStatus.Idle);
        RenderLog();
    }

    private void OnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        RefreshAll();
    }

    /// <summary>资源字典变更（主题/语言）后整体重渲染。</summary>
    private void RefreshAll()
    {
        ApplySessionStatus(sessionStatus);
        ApplyRepairUi();
        RenderResults();
        RenderLog();
    }

    // ── 对外入口：热键/托盘/仪表盘唤起后由 WorkbenchWindow 直接实例化 ──

    /// <summary>一键诊断（操作区主按钮）。</summary>
    private async void BtnDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (sessionStatus == DiagnosisSessionStatus.Diagnosing) return;
        await RunDiagnosisAsync();
    }

    /// <summary>执行诊断：后台线程运行，结果增量渲染（PRD 8.1 UI 不阻塞）。</summary>
    private async Task RunDiagnosisAsync()
    {
        runCts?.Cancel();
        runCts = new CancellationTokenSource();
        var ct = runCts.Token;

        items.Clear();
        sessionStatus = DiagnosisSessionStatus.Diagnosing;
        ApplySessionStatus(sessionStatus);

        BtnDiagnose.IsEnabled = false;
        BtnRetest.Visibility = Visibility.Collapsed;
        repairState = RepairUiState.Hidden;
        ApplyRepairUi();

        var progress = new Progress<DiagnosticItem>(item =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (!items.Contains(item)) items.Add(item);
            RenderResults();
        });

        try
        {
            var result = await Task.Run(() => DemoDiagnosisRunner.RunAsync(progress, ct), ct);
            if (Dispatcher.HasShutdownStarted) return;
            foreach (var item in result)
                if (!items.Contains(item)) items.Add(item);
            RenderResults();

            var failed = result.Count(i => i.Status == DiagnosisItemStatus.Fail);
            var warned = result.Count(i => i.Status == DiagnosisItemStatus.Warning);
            sessionStatus = failed == 0 && warned == 0
                ? DiagnosisSessionStatus.Healthy
                : DiagnosisSessionStatus.IssuesFound;
            ApplySessionStatus(sessionStatus);

            if (sessionStatus == DiagnosisSessionStatus.IssuesFound)
            {
                // T2 修复器：按失败/警告项的 NET 错误码匹配修复动作（NET-201/202→DNS，NET-401→代理，NET-101/001→适配器，NET-301→Winsock）
                repairSteps = NetworkRepairService.BuildPlan(result);
                repairNeedsRestart = repairSteps.Any(s => s.Id == "winsock");
                if (repairSteps.Count > 0)
                {
                    repairState = RepairUiState.Ready;
                    repairCount = repairSteps.Count;
                    ApplyRepairUi();
                }
            }

            var (logKey, logArgs) = GetLogConclusion(result);
            AddLogEntry(logKey, logArgs);
        }
        catch (OperationCanceledException)
        {
            sessionStatus = DiagnosisSessionStatus.Idle;
            ApplySessionStatus(sessionStatus);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolBox] Network diagnosis failed: {ex.Message}");
            sessionStatus = DiagnosisSessionStatus.Idle;
            ApplySessionStatus(sessionStatus);
            AddLogEntry("Lang_Net_LogError");
        }
        finally
        {
            BtnDiagnose.IsEnabled = true;
            BtnRetest.Visibility = Visibility.Visible;
        }
    }

    /// <summary>执行修复（T2 修复器）：确认弹窗 → 按错误码匹配的动作逐项执行（UAC 单次批处理提权），过程/结果进修复区与日志区。</summary>
    private async void BtnRepair_Click(object sender, RoutedEventArgs e)
    {
        if (repairSteps.Count == 0) return;

        var confirm = new RepairConfirmWindow(repairSteps) { Owner = Window.GetWindow(this) };
        if (confirm.ShowDialog() != true)
        {
            AddLogEntry("Lang_Net_LogRepairCancelled");
            return;
        }

        repairState = RepairUiState.Executing;
        ApplyRepairUi();
        BtnDiagnose.IsEnabled = false;
        BtnRetest.IsEnabled = false;
        AddLogEntry("Lang_Net_LogRepairStart", repairSteps.Count);

        var progress = new Progress<RepairStep>(step =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            RenderPlan();
            if (step.Status == RepairStepStatus.Pass)
                AddLogEntry("Lang_Net_LogRepairStepDone", step.Title);
            else if (step.Status == RepairStepStatus.Fail)
                AddLogEntry("Lang_Net_LogRepairStepFailed", step.Title);
            else if (step.Status == RepairStepStatus.Skipped)
                AddLogEntry("Lang_Net_LogRepairStepSkipped", step.Title);
        });

        RepairSummary summary;
        try
        {
            summary = await Task.Run(() => NetworkRepairService.ExecuteAsync(repairSteps, progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolBox] Repair failed: {ex.Message}");
            AddLogEntry("Lang_Net_LogRepairError", ex.Message);
            repairState = RepairUiState.Done;
            ApplyRepairUi();
            BtnDiagnose.IsEnabled = true;
            BtnRetest.IsEnabled = true;
            return;
        }

        if (summary.ElevationCancelled || summary.ElevationFailed)
        {
            var affected = string.Join(", ", repairSteps
                .Where(s => s.Status == RepairStepStatus.Skipped)
                .Select(s => s.Title));
            MessageWindow.Show(
                L("Lang_Net_UacErrorTitle"),
                string.Format(L(summary.ElevationCancelled ? "Lang_Net_UacCancelled" : "Lang_Net_UacFailed"), affected),
                Window.GetWindow(this));
            AddLogEntry(summary.ElevationCancelled ? "Lang_Net_LogRepairUacCancel" : "Lang_Net_LogRepairUacFail");
        }

        if (summary.Failed + summary.Skipped == 0)
            AddLogEntry("Lang_Net_LogRepairAllDone", summary.Succeeded, repairSteps.Count);
        else
            AddLogEntry("Lang_Net_LogRepairPartial", summary.Succeeded, repairSteps.Count, summary.Failed + summary.Skipped);

        repairState = RepairUiState.Done;
        ApplyRepairUi();
        BtnDiagnose.IsEnabled = true;
        BtnRetest.IsEnabled = true;
    }

    /// <summary>稍后处理：隐藏修复区（AC-08 分支，T2 落地日志记录）。</summary>
    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        repairState = RepairUiState.Hidden;
        ApplyRepairUi();
        AddLogEntry("Lang_Net_LogDeferred");
    }

    private void ApplyRepairUi()
    {
        switch (repairState)
        {
            case RepairUiState.Hidden:
                RepairSection.Visibility = Visibility.Collapsed;
                break;
            case RepairUiState.Executing:
                RepairSection.Visibility = Visibility.Visible;
                RepairTitle.Text = L("Lang_Net_RepairExecuting");
                BtnRepair.IsEnabled = false;
                BtnLater.IsEnabled = false;
                RenderPlan();
                break;
            case RepairUiState.Done:
                RepairSection.Visibility = Visibility.Visible;
                RepairTitle.Text = RepairSummaryTitle();
                BtnRepair.IsEnabled = true;
                BtnLater.IsEnabled = true;
                RenderPlan();
                break;
            default:
                RepairSection.Visibility = Visibility.Visible;
                RepairTitle.Text = string.Format(L("Lang_Net_RepairTitle"), repairCount);
                RepairImpact.Text = L("Lang_Net_RepairImpact");
                BtnRepair.IsEnabled = true;
                BtnLater.IsEnabled = true;
                RenderPlan();
                break;
        }
        RepairRestartHint.Visibility = repairNeedsRestart && repairState != RepairUiState.Hidden
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (repairNeedsRestart && repairState != RepairUiState.Hidden)
            RepairRestartHint.Text = L("Lang_Net_RepairRestartHint");
    }

    /// <summary>修复完成后修复区标题（成功/部分成功/失败）。</summary>
    private string RepairSummaryTitle()
    {
        var pass = repairSteps.Count(s => s.Status == RepairStepStatus.Pass);
        if (pass > 0 && pass == repairSteps.Count) return L("Lang_Net_RepairDone");
        if (pass > 0) return L("Lang_Net_RepairPartial");
        return L("Lang_Net_RepairFailed");
    }

    /// <summary>渲染修复计划列表（状态图标 + 步骤名 + 错误码/提权标记）。</summary>
    private void RenderPlan()
    {
        RepairPlanList.Children.Clear();
        foreach (var step in repairSteps)
            RepairPlanList.Children.Add(BuildPlanRow(step));
    }

    private Grid BuildPlanRow(RepairStep step)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement icon;
        if (step.Status == RepairStepStatus.Running)
        {
            icon = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 16,
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            var (glyph, brushKey) = step.Status switch
            {
                RepairStepStatus.Pass => ("\uE73E", "SuccessBrush"),
                RepairStepStatus.Fail => ("\uE711", "ErrorBrush"),
                _ => ("\u25CB", "TextTertiaryBrush")
            };
            var glyphBlock = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            glyphBlock.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            icon = glyphBlock;
        }
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = step.Title,
            FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (step.ErrorCodes.Length > 0)
            chips.Children.Add(BuildChip(string.Join("/", step.ErrorCodes), "ErrorBrush", mono: true));
        if (step.RequiresElevation)
            chips.Children.Add(BuildChip(L("Lang_Net_RepairNeedAdmin"), "WarningBrush", mono: false));
        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);

        return grid;
    }

    private Border BuildChip(string text, string foregroundKey, bool mono)
    {
        var chip = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = mono ? (FontFamily)FindResource("FontMono") : (FontFamily)FindResource("FontSans"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.SetResourceReference(BackgroundProperty, "BgElevatedBrush");
        chip.SetResourceReference(BorderBrushProperty, "BorderDefaultBrush");
        ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        return chip;
    }

    // ── 会话状态渲染 ──

    private void ApplySessionStatus(DiagnosisSessionStatus status)
    {
        var dotBrush = "TextTertiaryBrush";
        switch (status)
        {
            case DiagnosisSessionStatus.Diagnosing:
                StatusTitle.Text = L("Lang_Net_StatusDiagnosing");
                StatusHint.Text = L("Lang_Net_StatusDiagnosingHint");
                dotBrush = "InfoBrush";
                break;
            case DiagnosisSessionStatus.Healthy:
                StatusTitle.Text = L("Lang_Net_StatusHealthy");
                StatusHint.Text = L("Lang_Net_StatusHealthyHint");
                dotBrush = "SuccessBrush";
                break;
            case DiagnosisSessionStatus.IssuesFound:
                StatusTitle.Text = L("Lang_Net_StatusIssues");
                StatusHint.Text = L("Lang_Net_StatusIssuesHint");
                dotBrush = "ErrorBrush";
                break;
            default:
                StatusTitle.Text = L("Lang_Net_StatusIdle");
                StatusHint.Text = L("Lang_Net_StatusIdleHint");
                dotBrush = "TextTertiaryBrush";
                break;
        }
        StatusDot.SetResourceReference(Border.BackgroundProperty, dotBrush);

        EmptyState.Visibility = items.Count == 0 && status != DiagnosisSessionStatus.Diagnosing
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResultsSection.Visibility = items.Count > 0 || status == DiagnosisSessionStatus.Diagnosing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── 检测结果列表渲染（状态图标 + 名称 + 详情，行展开详情） ──

    private void RenderResults()
    {
        ResultList.Children.Clear();
        foreach (var item in items)
            ResultList.Children.Add(BuildRow(item));
    }

    private Grid BuildRow(DiagnosticItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Cursor = Cursors.Hand,
            Tag = item
        };
        header.SetResourceReference(BackgroundProperty, "BgElevatedBrush");
        header.SetResourceReference(BorderBrushProperty, "BorderDefaultBrush");
        header.MouseLeftButtonUp += RowHeader_Click;
        header.MouseEnter += (s, e) =>
            header.SetResourceReference(BackgroundProperty, "BgHoverBrush");
        header.MouseLeave += (s, e) =>
            header.SetResourceReference(BackgroundProperty, "BgElevatedBrush");

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 状态图标（5.4：灰/旋转/绿/红/橙/灰）
        if (item.Status == DiagnosisItemStatus.Testing)
        {
            var spinner = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 16,
                Height = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(spinner, 0);
            headerGrid.Children.Add(spinner);
        }
        else
        {
            var (glyph, brushKey) = GetStatusVisual(item.Status);
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            Grid.SetColumn(icon, 0);
            headerGrid.Children.Add(icon);
        }

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(name, 1);
        headerGrid.Children.Add(name);

        var summary = new TextBlock
        {
            Text = item.Summary,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 8, 0)
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        Grid.SetColumn(summary, 2);
        headerGrid.Children.Add(summary);

        var chevron = new TextBlock
        {
            Text = item.IsExpanded ? "\u25B4" : "\u25BE",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        Grid.SetColumn(chevron, 3);
        headerGrid.Children.Add(chevron);

        header.Child = headerGrid;
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // 展开详情面板（行内展开，无隐藏交互）
        if (item.IsExpanded)
        {
            var details = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 8, 8),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(12, -4, 0, 8)
            };
            details.SetResourceReference(BackgroundProperty, "BgSunkenBrush");
            details.SetResourceReference(BorderBrushProperty, "BorderDefaultBrush");
            var stack = new StackPanel();
            foreach (var line in item.Details)
            {
                var lineText = new TextBlock
                {
                    Text = "• " + line,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                lineText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(lineText);
            }
            if (!string.IsNullOrEmpty(item.ErrorCode))
            {
                var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var codeLabel = new TextBlock
                {
                    Text = L("Lang_Net_ErrorCode"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                codeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
                codeRow.Children.Add(codeLabel);
                var chip = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = item.ErrorCode,
                        FontSize = 11,
                        FontFamily = (FontFamily)FindResource("FontMono"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                chip.SetResourceReference(BackgroundProperty, "BgElevatedBrush");
                chip.SetResourceReference(BorderBrushProperty, "BorderDefaultBrush");
                ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                codeRow.Children.Add(chip);
                stack.Children.Add(codeRow);
            }
            details.Child = stack;
            Grid.SetRow(details, 1);
            grid.Children.Add(details);
        }
        return grid;
    }

    private static (string glyph, string brushKey) GetStatusVisual(DiagnosisItemStatus status)
    {
        return status switch
        {
            DiagnosisItemStatus.Pass => ("\uE73E", "SuccessBrush"),
            DiagnosisItemStatus.Fail => ("\uE711", "ErrorBrush"),
            DiagnosisItemStatus.Warning => ("\uE7BA", "WarningBrush"),
            DiagnosisItemStatus.Skipped => ("\u25CB", "TextTertiaryBrush"),
            _ => ("\u25CB", "TextTertiaryBrush")
        };
    }

    private void RowHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: DiagnosticItem item })
        {
            item.IsExpanded = !item.IsExpanded;
            RenderResults();
        }
    }

    // ── 日志区（T4 落地最近 10 次记录持久化；当前为会话内记录，语言切换后重新本地化） ──

    private void AddLogEntry(string key, params object[] args)
    {
        logEntries.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), key, args));
        if (logEntries.Count > 10)
            logEntries.RemoveAt(0);
        RenderLog();
    }

    private void RenderLog()
    {
        LogList.Children.Clear();
        if (logEntries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = L("Lang_Net_LogEmpty"),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            LogList.Children.Add(empty);
            return;
        }
        foreach (var entry in logEntries)
        {
            var text = entry.Args.Length > 0
                ? string.Format(L(entry.Key), entry.Args)
                : L(entry.Key);
            var line = new TextBlock
            {
                Text = $"{entry.Time} · {text}",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            LogList.Children.Add(line);
        }
    }

    private static (string key, object[] args) GetLogConclusion(List<DiagnosticItem> result)
    {
        var failed = result.Count(i => i.Status == DiagnosisItemStatus.Fail);
        var warned = result.Count(i => i.Status == DiagnosisItemStatus.Warning);
        if (failed == 0 && warned == 0)
            return ("Lang_Net_LogHealthy", Array.Empty<object>());
        if (failed == 0)
            return ("Lang_Net_LogWarned", new object[] { warned });
        return ("Lang_Net_LogFailed", new object[] { failed, warned });
    }

    private string L(string key)
    {
        return (FindResource(key) as string) ?? key;
    }
}
