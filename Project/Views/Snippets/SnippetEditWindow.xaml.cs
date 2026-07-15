using System.Windows;
using System.Windows.Input;
using ToolBox.Core.Snippets;

namespace ToolBox.Views.Snippets;

public partial class SnippetEditWindow : Window
{
    public SnippetItem? Result { get; private set; }
    private readonly SnippetItem? existing;

    public SnippetEditWindow(SnippetItem? existing = null)
    {
        InitializeComponent();
        this.existing = existing;

        if (existing != null)
        {
            TitleDisplay.Text = "编辑代码片段";
            txtName.Text = existing.Name;
            txtTrigger.Text = existing.Trigger ?? "";
            txtCategory.Text = existing.Category;
            txtContent.Text = existing.Content;
            SelectLanguage(existing.Language);
        }
        else
        {
            TitleDisplay.Text = "新建代码片段";
            txtCategory.Text = "默认";
        }
    }

    private void SelectLanguage(string lang)
    {
        foreach (var item in cmbLanguage.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem ci &&
                ci.Content is string s && s.Equals(lang, StringComparison.OrdinalIgnoreCase))
            {
                cmbLanguage.SelectedItem = ci;
                return;
            }
        }
    }

    private string GetSelectedLanguage()
    {
        if (cmbLanguage.SelectedItem is System.Windows.Controls.ComboBoxItem ci && ci.Content is string s)
            return s;
        return "plaintext";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("请输入片段名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            txtName.Focus();
            return;
        }

        var trigger = txtTrigger.Text.Trim();
        var category = txtCategory.Text.Trim();
        if (string.IsNullOrEmpty(category)) category = "默认";

        if (existing != null)
        {
            existing.Name = name;
            existing.Trigger = string.IsNullOrEmpty(trigger) ? null : trigger;
            existing.Category = category;
            existing.Language = GetSelectedLanguage();
            existing.Content = txtContent.Text ?? "";
            existing.UpdatedAt = DateTime.UtcNow;
            Result = existing;
        }
        else
        {
            Result = new SnippetItem
            {
                Name = name,
                Trigger = string.IsNullOrEmpty(trigger) ? null : trigger,
                Category = category,
                Language = GetSelectedLanguage(),
                Content = txtContent.Text ?? ""
            };
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}
