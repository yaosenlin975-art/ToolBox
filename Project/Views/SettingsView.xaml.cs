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

        sldCompactOpacity.Value = Math.Max(30, data.CompactOpacity);
        txtCompactOpacityValue.Text = ((int)sldCompactOpacity.Value).ToString();

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
        result.Data.ScreenshotMaxAge = (int)sldScreenshotMaxAge.Value;

        result.Scrap.InactiveAlphaChange = chkInactiveAlpha.IsChecked == true;
        result.Scrap.InactiveAlphaValue = (int)sldInactiveAlpha.Value;
        result.Scrap.MouseOverAlphaChange = chkMouseOverAlpha.IsChecked == true;
        result.Scrap.MouseOverAlphaValue = (int)sldMouseOverAlpha.Value;


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
    }

    private void SldInactiveAlpha_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isLoading || result == null) return;
        var v = (int)sldInactiveAlpha.Value;
        txtInactiveAlphaValue.Text = v + "%";
        result.Scrap.InactiveAlphaValue = v;
    }

    private void ChkMouseOverAlpha_Changed(object sender, RoutedEventArgs e)
    {
        if (isLoading || result == null) return;
        sldMouseOverAlpha.IsEnabled = chkMouseOverAlpha.IsChecked == true;
        result.Scrap.MouseOverAlphaChange = chkMouseOverAlpha.IsChecked == true;
    }

    private void SldMouseOverAlpha_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isLoading || result == null) return;
        var v = (int)sldMouseOverAlpha.Value;
        txtMouseOverAlphaValue.Text = v + "%";
        result.Scrap.MouseOverAlphaValue = v;
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
        App.ApplyLanguage(result.Language);
        App.MainWindow?.ReRegisterHotkeys();
        Core.Scheduling.ScheduleManager.Apply(result.Data);
        MessageWindow.Show(
            (FindResource("Lang_Save") as string) ?? "保存",
            (FindResource("Lang_Saved") as string) ?? "设置已保存",
            GetOwner());
    }

    private Window? GetOwner() => Window.GetWindow(this);

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
    }
}
