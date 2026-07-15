using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Native;
using ToolBox.Core.Snippets;

namespace ToolBox.Views.Snippets;

public partial class SnippetPopup : Window
{
    private List<SnippetItem> allItems = [];
    private List<SnippetItem> filteredItems = [];
    private bool showFavoritesOnly;

    public SnippetPopup()
    {
        InitializeComponent();
        SnippetStore.Instance.ItemsChanged += OnItemsChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        txtSearch.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SnippetStore.Instance.ItemsChanged -= OnItemsChanged;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Close();
    }

    private void OnItemsChanged() => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        allItems = SnippetStore.Instance.Items.ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = txtSearch.Text?.Trim() ?? string.Empty;
        filteredItems = string.IsNullOrEmpty(keyword)
            ? allItems.ToList()
            : SnippetStore.Instance.Search(keyword, null);

        if (showFavoritesOnly)
            filteredItems = filteredItems.Where(s => s.IsFavorite).ToList();

        lstSnippets.ItemsSource = filteredItems.Select(s => new SnippetViewModel(s)).ToList();
        lblStatus.Text = $"共 {filteredItems.Count} 条";

        if (filteredItems.Count > 0)
            lstSnippets.SelectedIndex = 0;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (lstSnippets.Items.Count > 0)
            {
                lstSnippets.Focus();
                lstSnippets.SelectedIndex = 0;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExpandAndCopySelected();
            e.Handled = true;
        }
    }

    private void LstSnippets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nothing special needed
    }

    private void LstSnippets_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExpandAndCopySelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && lstSnippets.SelectedIndex == 0)
        {
            txtSearch.Focus();
            e.Handled = true;
        }
    }

    private void ExpandAndCopySelected()
    {
        if (lstSnippets.SelectedItem is not SnippetViewModel vm) return;

        var content = SnippetStore.ExpandPlaceholders(vm.Item.Content);
        try
        {
            Clipboard.SetText(content);
            SnippetStore.Instance.IncrementUseCount(vm.Item.Id);
        }
        catch { }

        Close();

        // Simulate Ctrl+V paste after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        });
    }

    private void OnFilterChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb)
            showFavoritesOnly = rb.Tag as string == "fav";
        if (IsLoaded) ApplyFilter();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
