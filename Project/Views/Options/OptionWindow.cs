using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Models;
using ToolBox.Services;

namespace ToolBox.Views;

public partial class OptionWindow : Window
{
    private ToolBoxOption originalOption;
    public ToolBoxOption Result { get; private set; }

    public OptionWindow(ToolBoxOption option)
    {
        InitializeComponent();
        originalOption = option;
        Result = option.DeepCopy();
        LoadOption();
    }

    private void LoadOption()
    {
        // 常规
        rdoAppMode.IsChecked = Result.Data.AppType == EApplicationType.ApplicationMode;
        rdoResidentMode.IsChecked = Result.Data.AppType == EApplicationType.ResidentMode;
        chkShowMainWindow.IsChecked = Result.Data.ShowMainWindow;
        chkSplash.IsChecked = Result.Data.ShowSplashWindow;
        chkAutoStartup.IsChecked = AutoStartup.IsEnabled;
        chkTopMost.IsChecked = Result.Data.TopMostEnabled;

        // 快捷键
        chkHotKeyEnable.IsChecked = Result.HotKeyEnable;
        txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(Result.CaptureHotKey);
        txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(Result.HideShowHotKey);

        // 截图
        rdoLineSolid.IsChecked = Result.Data.SelectLineSolid;
        rdoLineDotted.IsChecked = !Result.Data.SelectLineSolid;
        rectLineColor.Background = new SolidColorBrush(Color.FromRgb(
            Result.Data.SelectLineColorR,
            Result.Data.SelectLineColorG,
            Result.Data.SelectLineColorB));
        rectBackColor.Background = new SolidColorBrush(Color.FromRgb(
            Result.Data.SelectBackColorR,
            Result.Data.SelectBackColorG,
            Result.Data.SelectBackColorB));
        sliderTransparency.Value = Result.Data.SelectAreaTransparent;
        chkCursor.IsChecked = Result.Data.CursorEnabled;
        chkFullscreenCursor.IsChecked = Result.Data.FullscreenCursor;
        chkMagnifier.IsChecked = Result.Data.MagnifierEnabled;

        // 贴图
        chkScrapImageDrag.IsChecked = Result.Scrap.ImageDrag;
        chkInactiveAlpha.IsChecked = Result.Scrap.InactiveAlphaChange;
        txtInactiveAlpha.Text = Result.Scrap.InactiveAlphaValue.ToString();
        chkMouseOverAlpha.IsChecked = Result.Scrap.MouseOverAlphaChange;
        txtMouseOverAlpha.Text = Result.Scrap.MouseOverAlphaValue.ToString();
        chkDustBox.IsChecked = Result.Data.DustBoxEnable;
        txtDustBoxCapacity.Text = Result.Data.DustBoxCapacity.ToString();

        // 菜单
        lstScrapMenuList.ItemsSource = Result.Scrap.SubMenuStyles;

        // 样式
        lstStyles.ItemsSource = Result.Styles;
        lstStyles.DisplayMemberPath = "StyleName";

        // 语言
        rdoLangZh.IsChecked = Result.Language != "en-US";
        rdoLangEn.IsChecked = Result.Language == "en-US";

        // 语言
        Result.Language = rdoLangEn.IsChecked == true ? "en-US" : "zh-CN";

        // 杂项
        chkBackgroundTransparent.IsChecked = Result.Data.BackgroundTransparentEnabled;
        rectCursorColor.Background = new SolidColorBrush(Color.FromRgb(
            Result.Data.FullscreenCursorColorR,
            Result.Data.FullscreenCursorColorG,
            Result.Data.FullscreenCursorColorB));
        rdoCursorSolid.IsChecked = Result.Data.FullscreenCursorSolid;
        rdoCursorHollow.IsChecked = !Result.Data.FullscreenCursorSolid;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveOption();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveOption()
    {
        // 常规
        Result.Data.AppType = rdoAppMode.IsChecked == true
            ? EApplicationType.ApplicationMode
            : EApplicationType.ResidentMode;
        Result.Data.ShowMainWindow = chkShowMainWindow.IsChecked == true;
        Result.Data.ShowSplashWindow = chkSplash.IsChecked == true;
        Result.Data.TopMostEnabled = chkTopMost.IsChecked == true;

        // Auto startup
        if (chkAutoStartup.IsChecked == true && !AutoStartup.IsEnabled)
            AutoStartup.Enable();
        else if (chkAutoStartup.IsChecked != true && AutoStartup.IsEnabled)
            AutoStartup.Disable();

        // 快捷键
        Result.HotKeyEnable = chkHotKeyEnable.IsChecked == true;

        // 截图
        Result.Data.SelectLineSolid = rdoLineSolid.IsChecked == true;
        if (rectLineColor.Background is SolidColorBrush lineBrush)
        {
            Result.Data.SelectLineColorR = lineBrush.Color.R;
            Result.Data.SelectLineColorG = lineBrush.Color.G;
            Result.Data.SelectLineColorB = lineBrush.Color.B;
        }
        if (rectBackColor.Background is SolidColorBrush backBrush)
        {
            Result.Data.SelectBackColorR = backBrush.Color.R;
            Result.Data.SelectBackColorG = backBrush.Color.G;
            Result.Data.SelectBackColorB = backBrush.Color.B;
        }
        Result.Data.SelectAreaTransparent = (int)sliderTransparency.Value;
        Result.Data.CursorEnabled = chkCursor.IsChecked == true;
        Result.Data.FullscreenCursor = chkFullscreenCursor.IsChecked == true;
        Result.Data.MagnifierEnabled = chkMagnifier.IsChecked == true;

        // 贴图
        Result.Scrap.ImageDrag = chkScrapImageDrag.IsChecked == true;
        Result.Scrap.InactiveAlphaChange = chkInactiveAlpha.IsChecked == true;
        if (int.TryParse(txtInactiveAlpha.Text, out var inactiveAlpha))
            Result.Scrap.InactiveAlphaValue = inactiveAlpha;
        Result.Scrap.MouseOverAlphaChange = chkMouseOverAlpha.IsChecked == true;
        if (int.TryParse(txtMouseOverAlpha.Text, out var mouseOverAlpha))
            Result.Scrap.MouseOverAlphaValue = mouseOverAlpha;
        Result.Data.DustBoxEnable = chkDustBox.IsChecked == true;
        if (ushort.TryParse(txtDustBoxCapacity.Text, out var dustCapacity))
            Result.Data.DustBoxCapacity = dustCapacity;

        // 语言
        rdoLangZh.IsChecked = Result.Language != "en-US";
        rdoLangEn.IsChecked = Result.Language == "en-US";

        // 语言
        Result.Language = rdoLangEn.IsChecked == true ? "en-US" : "zh-CN";

        // 杂项
        Result.Data.BackgroundTransparentEnabled = chkBackgroundTransparent.IsChecked == true;
        Result.Data.FullscreenCursorSolid = rdoCursorSolid.IsChecked == true;
        if (rectCursorColor.Background is SolidColorBrush cursorBrush)
        {
            Result.Data.FullscreenCursorColorR = cursorBrush.Color.R;
            Result.Data.FullscreenCursorColorG = cursorBrush.Color.G;
            Result.Data.FullscreenCursorColorB = cursorBrush.Color.B;
        }
    }

    private void BtnChangeHotKey1_Click(object sender, RoutedEventArgs e)
    {
        var window = new HotkeyInputWindow();
        window.Owner = this;
        if (window.ShowDialog() == true)
        {
            var modifiers = Key.None;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Control)) modifiers |= Key.LeftCtrl;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Key.LeftShift;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Key.LeftAlt;
            var combined = window.SelectedKey | modifiers;
            Result.CaptureHotKey = combined;
            txtHotKey1.Text = ToolBoxOption.KeyToDisplayString(combined);
        }
    }

    private void BtnChangeHotKey2_Click(object sender, RoutedEventArgs e)
    {
        var window = new HotkeyInputWindow();
        window.Owner = this;
        if (window.ShowDialog() == true)
        {
            var modifiers = Key.None;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Control)) modifiers |= Key.LeftCtrl;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Key.LeftShift;
            if (window.SelectedModifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Key.LeftAlt;
            var combined = window.SelectedKey | modifiers;
            Result.HideShowHotKey = combined;
            txtHotKey2.Text = ToolBoxOption.KeyToDisplayString(combined);
        }
    }

    private void BtnAddMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selectWindow = new StyleItemSelectWindow();
        if (selectWindow.ShowDialog() == true && selectWindow.SelectedItem != null)
        {
            var style = new CStyle { StyleName = selectWindow.SelectedItem.GetDisplayName() };
            style.AddStyle(selectWindow.SelectedItem);
            Result.Styles.Add(style);
            Result.Scrap.SubMenuStyles.Add(style.StyleId);
            lstStyles.Items.Refresh();
            lstScrapMenuList.Items.Refresh();
        }
    }

    private void BtnRemoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (lstScrapMenuList.SelectedItem is int styleId)
        {
            Result.Scrap.SubMenuStyles.Remove(styleId);
            lstScrapMenuList.Items.Refresh();
        }
    }

    private void BtnAddStyle_Click(object sender, RoutedEventArgs e)
    {
        var style = new CStyle { StyleName = "新样式" };
        Result.Styles.Add(style);
        lstStyles.Items.Refresh();
    }

    private void BtnEditStyle_Click(object sender, RoutedEventArgs e)
    {
        if (lstStyles.SelectedItem is CStyle style)
        {
            var editWindow = new StyleEditWindow(style);
            if (editWindow.ShowDialog() == true)
            {
                var index = Result.Styles.IndexOf(style);
                if (index >= 0)
                    Result.Styles[index] = editWindow.Result;
                lstStyles.Items.Refresh();
            }
        }
    }

    private void BtnRemoveStyle_Click(object sender, RoutedEventArgs e)
    {
        if (lstStyles.SelectedItem is CStyle style)
        {
            Result.Styles.Remove(style);
            Result.Scrap.SubMenuStyles.Remove(style.StyleId);
            lstStyles.Items.Refresh();
            lstScrapMenuList.Items.Refresh();
        }
    }

    // LLM Settings
    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var provider = cmbProvider.SelectedItem?.ToString() ?? "";
        var model = cmbModel.SelectedItem?.ToString() ?? "";
        var apiKey = txtApiKey.Password;
        var baseUrl = txtBaseUrl.Text;

        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model))
        {
            MessageBox.Show("Please select provider and model.", "LLM Settings");
            return;
        }

        Core.Providers.ProviderManager.Instance.SetActiveModel(provider, model);
        MessageBox.Show("Provider configured: " + provider + " / " + model, "LLM Settings");
    }

    private void WhitelistAdd_Click(object sender, RoutedEventArgs e)
    {
        var path = WhitelistInput.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;
        Core.Security.FileAccessWhitelist.Instance.Add(path);
        WhitelistInput.Text = "";
        RefreshWhitelist();
    }

    private void WhitelistRemove_Click(object sender, RoutedEventArgs e)
    {
        if (WhitelistBox.SelectedItem is string path)
        {
            Core.Security.FileAccessWhitelist.Instance.Remove(path);
            RefreshWhitelist();
        }
    }

    private void RefreshWhitelist()
    {
        WhitelistBox.ItemsSource = Core.Security.FileAccessWhitelist.Instance.AllowedPaths.ToList();
    }

}




