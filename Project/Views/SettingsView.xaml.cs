using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Providers;
using ToolBox.Core.Theming;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class SettingsView : UserControl
{
    private ToolBoxOption result = null!;
    private bool isLoading = true;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (s, e) => LoadSettings();
    }

    private void LoadSettings()
    {
        isLoading = true;
        result = ToolBoxOption.Load();
        var data = result.Data;

        rdoAppMode.IsChecked = data.AppType == EApplicationType.ApplicationMode;
        rdoResidentMode.IsChecked = data.AppType == EApplicationType.ResidentMode;
        chkAutoStartup.IsChecked = data.ShowMainWindow;

        var themeMode = ParseThemeMode(data.Theme);
        rdoThemeLight.IsChecked = themeMode == EThemeMode.Light;
        rdoThemeDark.IsChecked = themeMode == EThemeMode.Dark;
        rdoThemeSystem.IsChecked = themeMode == EThemeMode.System;

        chkHotKeyEnable.IsChecked = result.HotKeyEnable;
        txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(result.CaptureHotKey);
        txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(result.HideShowHotKey);

        sldAreaTransparent.Value = data.SelectAreaTransparent;
        txtAreaTransparentValue.Text = data.SelectAreaTransparent.ToString();
        sldAreaTransparent.ValueChanged += (s, e) =>
            txtAreaTransparentValue.Text = ((int)sldAreaTransparent.Value).ToString();

        rdoLineSolid.IsChecked = !data.SelectLineSolid;
        rdoLineDotted.IsChecked = data.SelectLineSolid;

        sldCompactOpacity.Value = data.CompactOpacity;
        txtCompactOpacityValue.Text = data.CompactOpacity.ToString();

        LoadProviders();

        chkAutoScreenshot.IsChecked = data.AutoScreenshotEnabled;
        txtAutoScreenshotCron.Text = data.AutoScreenshotCron;
        chkDailyReport.IsChecked = data.DailyReportEnabled;
        txtDailyReportTime.Text = data.DailyReportTime;

        rdoLangZh.IsChecked = result.Language != "en-US";
        rdoLangEn.IsChecked = result.Language == "en-US";

        try
        {
            txtVersion.Text = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "2.0.0";
        }
        catch { txtVersion.Text = "2.0.0"; }

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
        SectionLanguage.Visibility = NavLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        result.HotKeyEnable = chkHotKeyEnable.IsChecked == true;
        result.Data.SelectAreaTransparent = (int)sldAreaTransparent.Value;
        result.Data.SelectLineSolid = rdoLineDotted.IsChecked == true;
        result.Language = rdoLangEn.IsChecked == true ? "en-US" : "zh-CN";

        result.Data.AutoScreenshotEnabled = chkAutoScreenshot.IsChecked == true;
        result.Data.AutoScreenshotCron = txtAutoScreenshotCron.Text.Trim();
        result.Data.DailyReportEnabled = chkDailyReport.IsChecked == true;
        result.Data.DailyReportTime = txtDailyReportTime.Text.Trim();

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
    }

    private void DiscoverModels_Click(object sender, RoutedEventArgs e)
    {
        if (cmbProvider.SelectedItem is not string name) return;
        var config = ProviderManager.Instance.GetConfig(name);
        if (config == null) return;
        config.ApiKey = txtApiKey.Password;
        config.BaseUrl = txtBaseUrl.Text.Trim();

        btnDiscoverModels.IsEnabled = false;
        btnDiscoverModels.Content = "发现中...";

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
                MessageWindow.Show("发现模型", $"发现 {models.Count} 个模型", GetOwner());
            }
            else
            {
                MessageWindow.Show("发现模型", "未发现模型，请检查连接配置", GetOwner());
            }
        }
        catch (Exception ex)
        {
            MessageWindow.Show("错误", $"发现模型失败: {ex.Message}", GetOwner());
        }
        finally
        {
            btnDiscoverModels.IsEnabled = true;
            btnDiscoverModels.Content = "发现模型";
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

    private void HotKey_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Background = (Brush)FindResource("AccentSoftBrush");
            tb.Text = "按下快捷键组合...";
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
                MessageWindow.Show("测试", "连接成功!", GetOwner());
            else
                MessageWindow.Show("测试", "连接失败，请检查配置", GetOwner());
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        CollectSettings();
        result.Save();
        App.MainWindow?.ReRegisterHotkeys();
        MessageWindow.Show("保存", "设置已保存", GetOwner());
    }

    private Window? GetOwner() => Window.GetWindow(this);

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
    }
}
