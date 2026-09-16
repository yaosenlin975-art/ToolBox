using System.Windows;

using System.Windows.Controls;

using System.Windows.Input;

using System.Windows.Shapes;

using ToolBox.Core.ActionChain;

using ToolBox.Core.Providers;

using ToolBox.Core.Reminder;

using ToolBox.Core.Scheduling;

using ToolBox.Core.Theming;

using ToolBox.Core.Todo;

using ToolBox.Models;

using ToolBox.Services.Ocr;



namespace ToolBox.Views;



public partial class SettingsView : UserControl

{

    private ToolBoxOption result = null!;

    private bool isLoading = true;



    public SettingsView()

    {

        InitializeComponent();

        Loaded += (s, e) =>
        {
            LoadSettings();
            ShutdownScheduler.Instance.Changed += OnShutdownSchedulerChanged;
            App.LanguageChanged += OnShutdownSchedulerChanged;
            ReminderStore.Instance.ItemsChanged += RefreshReminders;
        };

        Unloaded += (s, e) =>
        {
            ShutdownScheduler.Instance.Changed -= OnShutdownSchedulerChanged;
            App.LanguageChanged -= OnShutdownSchedulerChanged;
            ReminderStore.Instance.ItemsChanged -= RefreshReminders;
        };

    }



    private void LoadSettings()

    {

        isLoading = true;

        result = ToolBoxOption.Load();

        var data = result.Data;



        rdoAppMode.IsChecked = data.AppType == EApplicationType.ApplicationMode;

        rdoResidentMode.IsChecked = data.AppType == EApplicationType.ResidentMode;

        chkAutoStartup.IsChecked = AutoStartup.IsEnabled;



        var themeMode = ParseThemeMode(data.Theme);

        rdoThemeLight.IsChecked = themeMode == EThemeMode.Light;

        rdoThemeDark.IsChecked = themeMode == EThemeMode.Dark;

        rdoThemeSystem.IsChecked = themeMode == EThemeMode.System;



        chkHotKeyEnable.IsChecked = result.HotKeyEnable;

        txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(result.CaptureHotKey);

        txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(result.HideShowHotKey);

        txtHotKey3.Text = ToolBoxOption.KeyToDisplayString(result.NetworkHotKey);



        sldAreaTransparent.Value = data.SelectAreaTransparent;

        txtAreaTransparentValue.Text = data.SelectAreaTransparent.ToString();

        sldAreaTransparent.ValueChanged += (s, e) =>

            txtAreaTransparentValue.Text = ((int)sldAreaTransparent.Value).ToString();

        sldChatFontSize.Value = data.ChatFontSize;
        txtChatFontSizeValue.Text = data.ChatFontSize.ToString();
        sldChatFontSize.ValueChanged += (s, e) =>
            txtChatFontSizeValue.Text = ((int)sldChatFontSize.Value).ToString();



        rdoLineSolid.IsChecked = !data.SelectLineSolid;

        rdoLineDotted.IsChecked = data.SelectLineSolid;



        sldCompactOpacity.Value = Math.Max(30, data.CompactOpacity);

        txtCompactOpacityValue.Text = ((int)sldCompactOpacity.Value).ToString();

        chkAutoHide.IsChecked = data.AutoHideEnabled;



        LoadProviders();



        chkAutoScreenshot.IsChecked = data.AutoScreenshotEnabled;

        txtAutoScreenshotCron.Text = data.AutoScreenshotCron;

        chkDailyReport.IsChecked = data.DailyReportEnabled;

        txtDailyReportTime.Text = data.DailyReportTime;



        rdoLangZh.IsChecked = result.Language != "en-US";

        rdoLangEn.IsChecked = result.Language == "en-US";



        sldScreenshotMaxAge.Value = data.ScreenshotMaxAge;

        txtScreenshotMaxAgeValue.Text = data.ScreenshotMaxAge > 0

            ? data.ScreenshotMaxAge + " 天"

            : "永久保留";

        // Clipboard settings
        sldClipboardMaxEntries.Value = Math.Max(50, data.ClipboardMaxEntries);
        txtClipboardMaxEntriesValue.Text = ((int)sldClipboardMaxEntries.Value).ToString();
        txtClipboardIgnoredApps.Text = data.ClipboardIgnoredApps ?? string.Empty;



        // Scrap opacity

        chkInactiveAlpha.IsChecked = result.Scrap.InactiveAlphaChange;

        sldInactiveAlpha.Value = Math.Max(5, result.Scrap.InactiveAlphaValue);

        sldInactiveAlpha.IsEnabled = result.Scrap.InactiveAlphaChange;

        txtInactiveAlphaValue.Text = result.Scrap.InactiveAlphaValue + "%";

        chkMouseOverAlpha.IsChecked = result.Scrap.MouseOverAlphaChange;

        sldMouseOverAlpha.Value = Math.Max(5, result.Scrap.MouseOverAlphaValue);

        sldMouseOverAlpha.IsEnabled = result.Scrap.MouseOverAlphaChange;

        txtMouseOverAlphaValue.Text = result.Scrap.MouseOverAlphaValue + "%";





        try
        {
            txtVersion.Text = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "2.0.0";
        }
        catch { txtVersion.Text = "2.0.0"; }

        // OCR 设置(AC5.2): 引擎选择 + 默认语言 + 已安装语言包列表
        rdoOcrTesseract.IsChecked = data.OcrEngine != "WindowsOCR";
        rdoOcrWindows.IsChecked = data.OcrEngine == "WindowsOCR";
        txtOcrLanguage.Text = data.OcrLanguage ?? OcrService.DefaultLanguage;
        RefreshOcrInstalledLangs();



        LoadCategories();
        LoadActionChains();

        // 定时关机（CSH-11）: 恢复选择 + 已设置的倒计时状态
        rdoShutdownAtTime.IsChecked = true;
        cmbShutdownMinutes.Text = string.IsNullOrWhiteSpace(cmbShutdownMinutes.Text) ? "30" : cmbShutdownMinutes.Text;
        ShutdownTimeRow.Visibility = rdoShutdownAtTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShutdownMinutesRow.Visibility = rdoShutdownCountdown.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateShutdownStatus();

        // 定时提醒（CSH-28）
        RenderReminderCards();

        // 同步各导航 Section 的可见性（首次加载时导航 Checked 事件早于 Loaded 触发，需补一次）
        OnNavChecked(null, null!);

        isLoading = false;

    }



    private static EThemeMode ParseThemeMode(string? s)

    {

        return s?.ToLowerInvariant() switch

        {

            "light" => EThemeMode.Light,

            "dark" => EThemeMode.Dark,

            _ => EThemeMode.System

        };

    }



    private void OnNavChecked(object sender, RoutedEventArgs e)

    {

        if (!IsLoaded) return;

        SectionGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionHotKey.Visibility = NavHotKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionCapture.Visibility = NavCapture.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionLlm.Visibility = NavLlm.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionSchedule.Visibility = NavSchedule.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionReminder.Visibility = NavReminder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionShutdown.Visibility = NavShutdown.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionClipboard.Visibility = NavClipboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionOcr.Visibility = NavOcr.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionLanguage.Visibility = NavLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionActionChains.Visibility = NavActionChains.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    }



    private void OnThemeChecked(object sender, RoutedEventArgs e)

    {

        if (isLoading || result == null) return;



        EThemeMode mode;

        if (rdoThemeLight.IsChecked == true) mode = EThemeMode.Light;

        else if (rdoThemeDark.IsChecked == true) mode = EThemeMode.Dark;

        else mode = EThemeMode.System;



        ThemeManager.Instance.SetMode(mode);

        result.Data.Theme = mode.ToString();

        result.Save();

    }



    private void LoadProviders()

    {

        cmbProvider.Items.Clear();

        foreach (var config in ProviderManager.Instance.BuiltinConfigs)

            cmbProvider.Items.Add(config.Name);

        foreach (var config in ProviderManager.Instance.CustomConfigs)

            cmbProvider.Items.Add(config.Name);



        if (ProviderManager.Instance.ActiveModel != null)

            cmbProvider.SelectedItem = ProviderManager.Instance.ActiveModel.ProviderName;



        if (cmbProvider.SelectedIndex < 0 && cmbProvider.Items.Count > 0)

            cmbProvider.SelectedIndex = 0;



        LoadProviderConfig();

        LoadModels();

    }



    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)

    {

        LoadProviderConfig();

        LoadModels();

    }



    private void LoadProviderConfig()

    {

        if (cmbProvider.SelectedItem is not string name) return;

        var config = ProviderManager.Instance.GetConfig(name);

        if (config == null) return;

        txtApiKey.Password = config.ApiKey;

        txtBaseUrl.Text = config.BaseUrl;

    }



    private void LoadModels()

    {

        cmbModel.Items.Clear();

        if (cmbProvider.SelectedItem is not string providerName) return;

        var config = ProviderManager.Instance.GetConfig(providerName);

        if (config == null) return;

        foreach (var m in config.Models)

            cmbModel.Items.Add(m.ModelId);

        if (ProviderManager.Instance.ActiveModel != null && providerName == ProviderManager.Instance.ActiveModel.ProviderName)

            cmbModel.SelectedItem = ProviderManager.Instance.ActiveModel.ModelId;

        if (cmbModel.SelectedIndex < 0 && cmbModel.Items.Count > 0)

            cmbModel.SelectedIndex = 0;

    }



    private void CollectSettings()

    {

        result.Data.AppType = rdoAppMode.IsChecked == true ? EApplicationType.ApplicationMode : EApplicationType.ResidentMode;

        result.Data.ShowMainWindow = rdoAppMode.IsChecked == true;

        // 开机自启：以注册表 Run 键为准，勾选则写入、取消则删除
        if (chkAutoStartup.IsChecked == true)
            AutoStartup.Enable();
        else
            AutoStartup.Disable();

        result.HotKeyEnable = chkHotKeyEnable.IsChecked == true;

        result.Data.SelectAreaTransparent = (int)sldAreaTransparent.Value;

        result.Data.SelectLineSolid = rdoLineDotted.IsChecked == true;

        result.Language = rdoLangEn.IsChecked == true ? "en-US" : "zh-CN";



        result.Data.AutoScreenshotEnabled = chkAutoScreenshot.IsChecked == true;

        result.Data.AutoScreenshotCron = txtAutoScreenshotCron.Text.Trim();

        result.Data.DailyReportEnabled = chkDailyReport.IsChecked == true;

        result.Data.DailyReportTime = txtDailyReportTime.Text.Trim();

        result.Data.ScreenshotMaxAge = (int)sldScreenshotMaxAge.Value;
        result.Data.ChatFontSize = (int)sldChatFontSize.Value;
        result.Data.ClipboardMaxEntries = (int)sldClipboardMaxEntries.Value;
        result.Data.ClipboardIgnoredApps = txtClipboardIgnoredApps.Text?.Trim() ?? string.Empty;

        result.Scrap.InactiveAlphaChange = chkInactiveAlpha.IsChecked == true;

        result.Scrap.InactiveAlphaValue = (int)sldInactiveAlpha.Value;

        result.Scrap.MouseOverAlphaChange = chkMouseOverAlpha.IsChecked == true;

        result.Scrap.MouseOverAlphaValue = (int)sldMouseOverAlpha.Value;

        result.Data.AutoHideEnabled = chkAutoHide.IsChecked == true;



        if (cmbProvider.SelectedItem is string providerName)

        {

            var config = ProviderManager.Instance.GetConfig(providerName);

            if (config != null)

            {

                config.ApiKey = txtApiKey.Password;

                config.BaseUrl = txtBaseUrl.Text.Trim();

                if (config.IsBuiltin)

                    ProviderManager.Instance.UpdateBuiltinConfig(providerName, txtApiKey.Password, txtBaseUrl.Text.Trim());

                else

                    ProviderManager.Instance.UpdateCustomProvider(providerName, txtApiKey.Password, txtBaseUrl.Text.Trim());

            }

            if (cmbModel.SelectedItem is string modelId)

                ProviderManager.Instance.SetActiveModel(providerName, modelId);

        }

        // OCR 设置(AC5.2)
        result.Data.OcrEngine = rdoOcrWindows.IsChecked == true ? "WindowsOCR" : "Tesseract";
        result.Data.OcrLanguage = string.IsNullOrWhiteSpace(txtOcrLanguage.Text) ? OcrService.DefaultLanguage : txtOcrLanguage.Text.Trim();

    }



    private void DiscoverModels_Click(object sender, RoutedEventArgs e)

    {

        if (cmbProvider.SelectedItem is not string name) return;

        var config = ProviderManager.Instance.GetConfig(name);

        if (config == null) return;

        config.ApiKey = txtApiKey.Password;

        config.BaseUrl = txtBaseUrl.Text.Trim();



        btnDiscoverModels.IsEnabled = false;

        btnDiscoverModels.Content = (FindResource("Lang_Discovering") as string) ?? "发现中...";



        _ = DiscoverModelsAsync(name);

    }



    private async System.Threading.Tasks.Task DiscoverModelsAsync(string providerName)

    {

        try

        {

            var models = await ProviderManager.Instance.DiscoverModelsAsync(providerName);

            if (models.Count > 0)

            {

                LoadModels();

                MessageWindow.Show(

                    (FindResource("Lang_DiscoverModels") as string) ?? "发现模型",

                    string.Format((FindResource("Lang_DiscoveredCount") as string) ?? "发现 {0} 个模型", models.Count),

                    GetOwner());

            }

            else

            {

                MessageWindow.Show(

                    (FindResource("Lang_DiscoverModels") as string) ?? "发现模型",

                    (FindResource("Lang_NoModelsFound") as string) ?? "未发现模型，请检查连接配置",

                    GetOwner());

            }

        }

        catch (Exception ex)

        {

            MessageWindow.Show(

                (FindResource("Lang_Error") as string) ?? "错误",

                string.Format((FindResource("Lang_DiscoverFailed") as string) ?? "发现模型失败: {0}", ex.Message),

                GetOwner());

        }

        finally

        {

            btnDiscoverModels.IsEnabled = true;

            btnDiscoverModels.Content = (FindResource("Lang_DiscoverModels") as string) ?? "发现模型";

        }

    }



    private void SldCompactOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)

    {

        if (isLoading || result == null) return;

        var v = (int)sldCompactOpacity.Value;

        txtCompactOpacityValue.Text = v.ToString();

        result.Data.CompactOpacity = v;

        App.CompactToolbox?.ApplyCompactOpacity(v);

    }



    private void SldScreenshotMaxAge_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)

    {

        if (isLoading || result == null) return;

        var v = (int)sldScreenshotMaxAge.Value;

        txtScreenshotMaxAgeValue.Text = v > 0 ? v + " 天" : "永久保留";

        result.Data.ScreenshotMaxAge = v;

    }



    private void ChkInactiveAlpha_Changed(object sender, RoutedEventArgs e)

    {

        if (isLoading || result == null) return;

        sldInactiveAlpha.IsEnabled = chkInactiveAlpha.IsChecked == true;

        result.Scrap.InactiveAlphaChange = chkInactiveAlpha.IsChecked == true;
        (App.Current.MainWindow as MainWindow)?.RefreshScrapOpacity();

    }



    private void SldInactiveAlpha_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)

    {

        if (isLoading || result == null) return;

        var v = (int)sldInactiveAlpha.Value;

        txtInactiveAlphaValue.Text = v + "%";

        result.Scrap.InactiveAlphaValue = v;
        (App.Current.MainWindow as MainWindow)?.RefreshScrapOpacity();

    }



    private void ChkMouseOverAlpha_Changed(object sender, RoutedEventArgs e)

    {

        if (isLoading || result == null) return;

        sldMouseOverAlpha.IsEnabled = chkMouseOverAlpha.IsChecked == true;

        result.Scrap.MouseOverAlphaChange = chkMouseOverAlpha.IsChecked == true;
        (App.Current.MainWindow as MainWindow)?.RefreshScrapOpacity();

    }



    private void SldMouseOverAlpha_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)

    {

        if (isLoading || result == null) return;

        var v = (int)sldMouseOverAlpha.Value;

        txtMouseOverAlphaValue.Text = v + "%";

        result.Scrap.MouseOverAlphaValue = v;
        (App.Current.MainWindow as MainWindow)?.RefreshScrapOpacity();

    }



    private void HotKey_GotFocus(object sender, RoutedEventArgs e)

    {

        if (sender is TextBox tb)

        {

            tb.Background = (Brush)FindResource("AccentSoftBrush");

            tb.Text = (FindResource("Lang_HotkeyPressing") as string) ?? "按下快捷键组合...";

        }

    }



    private void HotKey_LostFocus(object sender, RoutedEventArgs e)

    {

        if (sender is TextBox tb)

        {

            tb.Background = (Brush)FindResource("BgSunkenBrush");

            if (sender == txtHotKey1)

                txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(result.CaptureHotKey);

            else if (sender == txtHotKey2)

                txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(result.HideShowHotKey);

            else if (sender == txtHotKey3)

                txtHotKey3.Text = ToolBoxOption.KeyToDisplayString(result.NetworkHotKey);

        }

    }



    private void HotKey_PreviewKeyDown(object sender, KeyEventArgs e)

    {

        if (sender is not TextBox tb) return;

        e.Handled = true;



        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (IsModifierKey(key)) return;



        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape)

        {

            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));

            return;

        }



        var combined = CombineKey(key, modifiers);

        if (sender == txtHotKey1)

        {

            result.CaptureHotKey = combined;

            txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(combined);

        }

        else if (sender == txtHotKey2)

        {

            result.HideShowHotKey = combined;

            txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(combined);

        }

        else if (sender == txtHotKey3)

        {

            result.NetworkHotKey = combined;

            txtHotKey3.Text = ToolBoxOption.KeyToDisplayString(combined);

        }

        tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));

    }



    private static bool IsModifierKey(Key key)

    {

        return key == Key.LeftCtrl || key == Key.RightCtrl

            || key == Key.LeftShift || key == Key.RightShift

            || key == Key.LeftAlt || key == Key.RightAlt

            || key == Key.LWin || key == Key.RWin;

    }



    private static Key CombineKey(Key key, ModifierKeys modifiers)

    {

        var result = (int)key & ToolBoxOption.KeyMask;

        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ToolBoxOption.CtrlBit;

        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ToolBoxOption.ShiftBit;

        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ToolBoxOption.AltBit;

        return (Key)result;

    }



    private void TestConnection_Click(object sender, RoutedEventArgs e)

    {

        if (cmbProvider.SelectedItem is string providerName && cmbModel.SelectedItem is string modelId)

        {

            ProviderManager.Instance.SetActiveModel(providerName, modelId);

            var provider = ProviderManager.Instance.CreateActiveProvider();

            if (provider != null)

                MessageWindow.Show(

                    (FindResource("Lang_Test") as string) ?? "测试",

                    (FindResource("Lang_ConnSuccess") as string) ?? "连接成功!",

                    GetOwner());

            else

                MessageWindow.Show(

                    (FindResource("Lang_Test") as string) ?? "测试",

                    (FindResource("Lang_ConnFail") as string) ?? "连接失败，请检查配置",

                    GetOwner());

        }

    }



    private void BtnSave_Click(object sender, RoutedEventArgs e)

    {

        CollectSettings();

        result.Save();

        App.CompactToolbox?.ApplyAutoHide(result.Data.AutoHideEnabled);
        App.ApplyLanguage(result.Language);

        App.MainWindow?.ReRegisterHotkeys();

        Core.Scheduling.ScheduleManager.Apply(result.Data);

        MessageWindow.Show(

            (FindResource("Lang_Save") as string) ?? "保存",

            (FindResource("Lang_Saved") as string) ?? "设置已保存",

            GetOwner());

    }



    private void LoadCategories()
    {
        if (CategoryList == null) return;
        CategoryList.ItemsSource = TodoStore.Instance.Categories.ToList();
    }

    private async void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = NewCategoryInput.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await TodoStore.Instance.AddCategory(name);
        NewCategoryInput.Text = "";
        LoadCategories();
    }

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            await TodoStore.Instance.RemoveCategory(name);
            LoadCategories();
        }
    }

    private Window? GetOwner() => Window.GetWindow(this);



    private void BtnReset_Click(object sender, RoutedEventArgs e)

    {

        LoadSettings();

    }

    /// <summary>剪贴板最大条目数 Slider 变更</summary>
    private void SldClipboardMaxEntries_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isLoading || result == null) return;
        var v = (int)sldClipboardMaxEntries.Value;
        txtClipboardMaxEntriesValue.Text = v.ToString();
        result.Data.ClipboardMaxEntries = v;
    }

    // ===== OCR 设置(AC5.2) =====

    /// <summary>刷新已安装语言包列表显示。</summary>
    private void RefreshOcrInstalledLangs()
    {
        try
        {
            var langs = OcrService.Instance.ListInstalledLanguages();
            txtOcrInstalledLangs.Text = langs.Count == 0 ? "(未安装)" : string.Join(", ", langs);
        }
        catch (Exception ex)
        {
            txtOcrInstalledLangs.Text = "读取失败: " + ex.Message;
        }
    }

    /// <summary>手动刷新已安装语言包按钮。</summary>
    private void BtnRefreshOcrLangs_Click(object sender, RoutedEventArgs e)
    {
        RefreshOcrInstalledLangs();
    }

    // ===== Action Chain =====

    private void LoadActionChains()
    {
        var store = ActionChainStore.Instance;
        cmbDefaultChain.Items.Clear();
        foreach (var chain in store.Chains)
            cmbDefaultChain.Items.Add(new ComboBoxItem { Content = chain.Name, Tag = chain.Id });

        var defaultChain = store.GetDefaultChain();
        if (defaultChain != null)
        {
            for (int i = 0; i < cmbDefaultChain.Items.Count; i++)
            {
                if (cmbDefaultChain.Items[i] is ComboBoxItem item && item.Tag is string id && id == defaultChain.Id)
                    cmbDefaultChain.SelectedIndex = i;
            }
        }

        chkContinueOnError.IsChecked = defaultChain != null ? !defaultChain.StopOnError : false;
        RenderActionChainCards();
    }

    private void RenderActionChainCards()
    {
        ActionChainCards.Children.Clear();
        var store = ActionChainStore.Instance;
        txtChainCount.Text = store.Chains.Count + " 条链";

        foreach (var chain in store.Chains)
        {
            var isDefault = chain.Id == store.DefaultChainId;
            var card = new Border
            {
                Background = (Brush)FindResource(isDefault ? "AccentSoftBrush" : "BgSunkenBrush"),
                BorderBrush = (Brush)FindResource(isDefault ? "AccentBrush" : "BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var cardStack = new StackPanel();

            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
            titleStack.Children.Add(new TextBlock { Text = "⚡ ", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            titleStack.Children.Add(new TextBlock
            {
                Text = chain.Name,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (isDefault)
                titleStack.Children.Add(new Border
                {
                    Background = (Brush)FindResource("AccentBrush"), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "默认", FontSize = 10, Foreground = Brushes.White }
                });
            if (chain.IsBuiltIn)
                titleStack.Children.Add(new Border
                {
                    Background = (Brush)FindResource("BgSunkenBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "内置", FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush") }
                });
            Grid.SetColumn(titleStack, 0);
            titleRow.Children.Add(titleStack);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
            if (!isDefault)
            {
                var setBtn = new Button
                {
                    Content = "设为默认", Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Tag = chain
                };
                setBtn.Click += (s, e2) =>
                {
                    if (s is Button b && b.Tag is ActionChainDefinition c)
                    {
                        ActionChainStore.Instance.SetDefault(c.Id);
                        LoadActionChains();
                    }
                };
                btnStack.Children.Add(setBtn);
            }
            if (!chain.IsBuiltIn)
            {
                var delBtn = new Button
                {
                    Content = "✕", Style = (Style)FindResource("IconButton"),
                    Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0), Tag = chain
                };
                delBtn.Click += (s, e2) =>
                {
                    if (s is Button b && b.Tag is ActionChainDefinition c)
                    {
                        ActionChainStore.Instance.Delete(c.Id);
                        LoadActionChains();
                    }
                };
                btnStack.Children.Add(delBtn);
            }
            Grid.SetColumn(btnStack, 1);
            titleRow.Children.Add(btnStack);
            cardStack.Children.Add(titleRow);

            var nodesText = string.Join(" → ", chain.Nodes.Select(n => GetNodeEmoji(n.NodeType) + GetNodeDisplayName(n.NodeType)));
            cardStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(nodesText) ? "(空链)" : nodesText,
                FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 6, 0, 0)
            });

            if (!string.IsNullOrEmpty(chain.Description))
                cardStack.Children.Add(new TextBlock
                {
                    Text = chain.Description, FontSize = 11,
                    Foreground = (Brush)FindResource("TextTertiaryBrush"), Margin = new Thickness(0, 2, 0, 0)
                });

            card.Child = cardStack;
            ActionChainCards.Children.Add(card);
        }
    }

    private string GetNodeEmoji(string nodeType) => nodeType switch
    {
        "ocr" => "🔤 ",
        "translate" => "🌐 ",
        "copy" => "📋 ",
        "save" => "💾 ",
        "send_to_ai" => "🤖 ",
        "open_editor" => "✏️ ",
        _ => "• "
    };

    private string GetNodeDisplayName(string nodeType) => nodeType switch
    {
        "ocr" => "OCR 提取",
        "translate" => "翻译文本",
        "copy" => "复制到剪贴板",
        "save" => "保存文件",
        "send_to_ai" => "发送到 AI",
        "open_editor" => "打开编辑器",
        _ => nodeType
    };

    private async void BtnAddChain_Click(object sender, RoutedEventArgs e)
    {
        var nameDialog = new InputWindow("新建动作链", "输入名称");
        if (await nameDialog.ShowNonModalAsync(Window.GetWindow(this)) && !string.IsNullOrWhiteSpace(nameDialog.Value))
        {
            var name = nameDialog.Value?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            var nodeDialog = new InputWindow("选择节点", "输入节点类型 (逗号分隔)\\n可选: ocr, translate, copy, save, send_to_ai, open_editor");
            if (await nodeDialog.ShowNonModalAsync(Window.GetWindow(this)) && !string.IsNullOrWhiteSpace(nodeDialog.Value))
            {
                var nodeTypes = nodeDialog.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(20).ToList() ?? new List<string>();

                var chain = new ActionChainDefinition
                {
                    Name = name,
                    Nodes = nodeTypes.Select(t => new ActionNodeConfig { NodeType = t }).ToList()
                };
                ActionChainStore.Instance.Add(chain);
                LoadActionChains();
            }
        }
    }

    private void CmbDefaultChain_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isLoading) return;
        if (cmbDefaultChain.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            ActionChainStore.Instance.SetDefault(id);
            RenderActionChainCards();
        }
    }

    private void ChkContinueOnError_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (isLoading) return;
        var store = ActionChainStore.Instance;
        var defaultChain = store.GetDefaultChain();
        if (defaultChain != null)
        {
            defaultChain.StopOnError = chkContinueOnError.IsChecked != true;
            store.Update(defaultChain);
        }
    }

    // ===== 定时关机（CSH-11） =====

    private void OnShutdownModeChanged(object sender, RoutedEventArgs e)
    {
        if (isLoading) return;
        ShutdownTimeRow.Visibility = rdoShutdownAtTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShutdownMinutesRow.Visibility = rdoShutdownCountdown.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShutdownSchedulerChanged() => UpdateShutdownStatus();

    private void UpdateShutdownStatus()
    {
        if (!IsLoaded) return;
        var sched = ShutdownScheduler.Instance;
        var remaining = sched.GetRemaining();
        txtShutdownStatus.Text = remaining != null && sched.TargetTime != null
            ? string.Format((FindResource("Lang_Settings_ShutdownStatusRunning") as string) ?? "将于 {0} 关机，剩余 {1}",
                sched.TargetTime.Value.ToString("HH:mm"), ShutdownScheduler.FormatRemaining(remaining.Value))
            : (FindResource("Lang_Settings_ShutdownStatusIdle") as string) ?? "未设置定时关机";
        btnShutdownStart.IsEnabled = sched.State != ShutdownScheduler.ShutdownState.Running;
        btnShutdownCancel.IsEnabled = sched.State == ShutdownScheduler.ShutdownState.Running;
    }

    private void BtnShutdownStart_Click(object sender, RoutedEventArgs e)
    {
        DateTime target;
        if (rdoShutdownAtTime.IsChecked == true)
        {
            if (!TimeSpan.TryParseExact(txtShutdownTime.Text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var span))
            {
                MessageWindow.Show(
                    (FindResource("Lang_Settings_Shutdown") as string) ?? "定时关机",
                    (FindResource("Lang_Settings_ShutdownInvalidTime") as string) ?? "请输入有效时刻（格式 HH:mm）",
                    GetOwner());
                return;
            }
            target = DateTime.Today.Add(span);
            if (target <= DateTime.Now)
                target = target.AddDays(1);
        }
        else
        {
            if (!int.TryParse(cmbShutdownMinutes.Text.Trim(), out var minutes) || minutes < 1 || minutes > 1440)
            {
                MessageWindow.Show(
                    (FindResource("Lang_Settings_Shutdown") as string) ?? "定时关机",
                    (FindResource("Lang_Settings_ShutdownInvalidMinutes") as string) ?? "请输入有效的倒计时分钟数（1-1440）",
                    GetOwner());
                return;
            }
            target = DateTime.Now.AddMinutes(minutes);
        }

        ShutdownScheduler.Instance.ScheduleAt(target);
        UpdateShutdownStatus();
    }

    private void BtnShutdownCancel_Click(object sender, RoutedEventArgs e)
    {
        ShutdownScheduler.Instance.CancelAndAbort();
    }

    // ===== 定时提醒（CSH-28） =====

    /// <summary>托盘「提醒管理」入口：切换到设置页并定位到提醒 Section。</summary>
    public void NavigateToReminderSection()
    {
        NavReminder.IsChecked = true;
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (SectionReminder != null) SectionReminder.BringIntoView();
        }));
    }

    /// <summary>提醒数据变更（含 Cron 后台线程触发）后刷新列表，统一编组到 UI 线程。</summary>
    private void RefreshReminders()
    {
        var disp = Dispatcher;
        if (disp == null) return;
        disp.BeginInvoke(new Action(RenderReminderCards));
    }

    private void RenderReminderCards()
    {
        if (ReminderCards == null) return;
        ReminderCards.Children.Clear();

        var items = ReminderStore.Instance.GetAll()
            .OrderBy(r => r.GetNextFireLocal() ?? DateTime.MaxValue)
            .ToList();
        ReminderEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
            ReminderCards.Children.Add(BuildReminderCard(item));
    }

    private Border BuildReminderCard(ReminderItem item)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BgSunkenBrush"),
            BorderBrush = (Brush)FindResource("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧类型图标
        var iconBorder = new Border
        {
            Background = (Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(9999),
            Width = 36, Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        iconBorder.Child = new Path
        {
            Data = (Geometry)FindResource(item.Type == ReminderType.Once ? "IconSchedule" : "IconRepeat"),
            Fill = (Brush)FindResource("AccentBrush"),
            Width = 16, Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(iconBorder, 0);
        Grid.SetColumn(iconBorder, 0);
        root.Children.Add(iconBorder);

        // 中间：标题 + 下次触发时间
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var next = item.GetNextFireLocal();
        string nextText;
        if (next != null)
            nextText = string.Format((FindResource("Lang_ReminderNext") as string) ?? "下次 {0}",
                next.Value.ToString("yyyy-MM-dd HH:mm"));
        else if (item.Type == ReminderType.Once)
            nextText = (FindResource("Lang_ReminderExpired") as string) ?? "已过期";
        else
            nextText = "-";
        info.Children.Add(new TextBlock
        {
            Text = nextText,
            FontSize = 11, FontFamily = (FontFamily)FindResource("FontMono"),
            Foreground = (Brush)FindResource("TextTertiaryBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetRow(info, 0);
        Grid.SetColumn(info, 1);
        root.Children.Add(info);

        // 右侧启用开关
        var toggle = new CheckBox
        {
            Style = (Style)FindResource("ToggleSwitch"),
            IsChecked = item.IsEnabled,
            Tag = item,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += OnReminderToggleChanged;
        toggle.Unchecked += OnReminderToggleChanged;
        Grid.SetRow(toggle, 0);
        Grid.SetColumn(toggle, 2);
        root.Children.Add(toggle);

        // 底行：类型标签 + 编辑/删除
        var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeLabel = new TextBlock
        {
            Text = item.Type == ReminderType.Once
                ? (FindResource("Lang_ReminderTypeOnce") as string) ?? "一次性"
                : (FindResource("Lang_ReminderTypeRecurring") as string) ?? "重复",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeLabel, 0);
        bottom.Children.Add(typeLabel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };

        var editBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0), Tag = item,
            ToolTip = (FindResource("Lang_Edit") as string) ?? "编辑"
        };
        editBtn.Content = new Path
        {
            Data = (Geometry)FindResource("IconEdit"),
            Width = 12, Height = 12, Stretch = Stretch.Uniform,
            Fill = (Brush)FindResource("TextSecondaryBrush")
        };
        editBtn.Click += OnEditReminder_Click;
        buttons.Children.Add(editBtn);

        var deleteBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0), Tag = item,
            ToolTip = (FindResource("Lang_ReminderDelete") as string) ?? "删除"
        };
        deleteBtn.Content = new Path
        {
            Data = (Geometry)FindResource("IconDelete"),
            Width = 12, Height = 12, Stretch = Stretch.Uniform,
            Fill = (Brush)FindResource("TextSecondaryBrush")
        };
        deleteBtn.Click += OnDeleteReminder_Click;
        buttons.Children.Add(deleteBtn);

        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);

        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        card.Child = root;
        return card;
    }

    private async void OnReminderToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is ReminderItem item)
        {
            await ReminderStore.Instance.SetEnabledAsync(item.Id, cb.IsChecked == true);
            ReminderScheduler.Instance.ApplyItem(item);
        }
    }

    private async void OnEditReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ReminderItem item) return;
        var dialog = new Reminder.ReminderDialog(item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            await ReminderStore.Instance.UpdateAsync(dialog.Result);
            ReminderScheduler.Instance.ApplyItem(dialog.Result);
        }
    }

    private async void OnDeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ReminderItem item) return;
        await ReminderStore.Instance.DeleteAsync(item.Id);
        ReminderScheduler.Instance.Unregister(item.Id);
    }

    private async void BtnAddReminder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Reminder.ReminderDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            await ReminderStore.Instance.AddAsync(dialog.Result);
            ReminderScheduler.Instance.ApplyItem(dialog.Result);
        }
    }

}

