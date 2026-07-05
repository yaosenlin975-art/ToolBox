using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ToolBox.Views;

public partial class HotkeyInputWindow : Window
{
    public Key SelectedKey { get; private set; }
    public ModifierKeys SelectedModifiers { get; private set; }

    private bool isCapturing;

    public HotkeyInputWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!isCapturing)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.None)
            return;

        if (IsModifierKey(key))
        {
            UpdateDisplay();
            e.Handled = true;
            return;
        }

        SelectedKey = key;
        SelectedModifiers = Keyboard.Modifiers;
        UpdateDisplay();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!isCapturing)
            return;

        UpdateDisplay();
        e.Handled = true;
    }

    private bool IsModifierKey(Key key)
    {
        return key == Key.LeftCtrl || key == Key.RightCtrl
            || key == Key.LeftShift || key == Key.RightShift
            || key == Key.LeftAlt || key == Key.RightAlt
            || key == Key.LWin || key == Key.RWin;
    }

    private void UpdateDisplay()
    {
        var sb = new StringBuilder();
        var mods = SelectedKey != Key.None ? SelectedModifiers : Keyboard.Modifiers;

        if (mods.HasFlag(ModifierKeys.Control))
            sb.Append("Ctrl + ");
        if (mods.HasFlag(ModifierKeys.Shift))
            sb.Append("Shift + ");
        if (mods.HasFlag(ModifierKeys.Alt))
            sb.Append("Alt + ");

        if (SelectedKey != Key.None && !IsModifierKey(SelectedKey))
            sb.Append(SelectedKey);

        lblKey.Text = sb.Length > 0 ? sb.ToString() : "请按下快捷键组合...";
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        isCapturing = true;
        UpdateDisplay();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKey == Key.None)
        {
            MessageBox.Show("请先按下快捷键组合", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static string KeyToString(Key key, ModifierKeys modifiers)
    {
        var sb = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control))
            sb.Append("Ctrl+");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            sb.Append("Shift+");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            sb.Append("Alt+");
        sb.Append(key);
        return sb.ToString();
    }
}
