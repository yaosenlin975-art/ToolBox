using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Snippets;

namespace ToolBox.Views.Snippets;

public partial class SnippetView : UserControl
{
    private List<SnippetItem> allItems = [];
    private List<SnippetItem> filteredItems = [];
    private SnippetItem? selectedItem;

    public SnippetView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SnippetStore.Instance.ItemsChanged += OnItemsChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SnippetStore.Instance.ItemsChanged -= OnItemsChanged;
    }

    private void OnItemsChanged() => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        allItems = SnippetStore.Instance.Items.ToList();
        LoadCategories();
        ApplyFilter();
    }

    private void LoadCategories()
    {
        var cats = new List<string> { "全部" };
        cats.AddRange(SnippetStore.Instance.Categories);
        cmbCategory.ItemsSource = cats;
        if (cmbCategory.SelectedIndex < 0)
            cmbCategory.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var category = cmbCategory.SelectedItem as string;
        var keyword = txtSearch.Text?.Trim() ?? string.Empty;

        filteredItems = category == "全部" || string.IsNullOrEmpty(category)
            ? SnippetStore.Instance.Search(keyword, null)
            : SnippetStore.Instance.Search(keyword, category);

        lstSnippets.ItemsSource = filteredItems.Select(s => new SnippetViewModel(s)).ToList();
        lblCount.Text = $"共 {filteredItems.Count} 条";
        lblStatus.Text = $"显示 {filteredItems.Count} / {allItems.Count} 条";
    }

    private void ShowDetail(SnippetItem? item)
    {
        selectedItem = item;
        if (item == null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        txtDetailName.Text = item.Name;
        txtDetailTrigger.Text = string.IsNullOrEmpty(item.Trigger) ? "(无)" : item.Trigger;
        txtDetailCategory.Text = item.Category;
        txtDetailLanguage.Text = item.Language;
        txtDetailContent.Text = item.Content;
        txtDetailUseCount.Text = item.UseCount.ToString();
        txtDetailCreated.Text = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void LstSnippets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (lstSnippets.SelectedItem is SnippetViewModel vm)
            ShowDetail(vm.Item);
        else
            ShowDetail(null);
    }

    private void OnFavoriteClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SnippetViewModel vm)
        {
            vm.Item.IsFavorite = !vm.Item.IsFavorite;
            SnippetStore.Instance.Update(vm.Item);
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SnippetEditWindow
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            SnippetStore.Instance.Add(dialog.Result);
        }
    }

    private void BtnCopyContent_Click(object sender, RoutedEventArgs e)
    {
        if (selectedItem == null) return;
        try
        {
            var expanded = SnippetStore.ExpandPlaceholders(selectedItem.Content);
            Clipboard.SetText(expanded);
            SnippetStore.Instance.IncrementUseCount(selectedItem.Id);
        }
        catch { }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (selectedItem == null) return;
        var dialog = new SnippetEditWindow(selectedItem)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            SnippetStore.Instance.Update(dialog.Result);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (selectedItem == null) return;
        var result = MessageBox.Show(
            $"确定删除片段「{selectedItem.Name}」？",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            SnippetStore.Instance.Delete(selectedItem.Id);
            ShowDetail(null);
        }
    }

    private void LstSnippets_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && lstSnippets.SelectedItem is SnippetViewModel vm)
        {
            SnippetStore.Instance.Delete(vm.Item.Id);
            e.Handled = true;
        }
    }
}

/// <summary>ViewModel wrapper for SnippetItem in ListBox binding.</summary>
internal class SnippetViewModel
{
    public SnippetItem Item { get; }
    public string DisplayName => Item.Name;
    public string TriggerDisplay => string.IsNullOrEmpty(Item.Trigger) ? "" : Item.Trigger;
    public string LanguageDisplay => Item.Language;
    public string ContentPreview => Item.Content.Length > 80 ? Item.Content[..80] + "..." : Item.Content;
    public string CategoryDisplay => Item.Category;
    public string UseCountDisplay => $"使用 {Item.UseCount} 次";
    public string UpdatedDisplay => Item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public Brush FavoriteColor => Item.IsFavorite
        ? (Brush)Application.Current.FindResource("AccentBrush")
        : (Brush)Application.Current.FindResource("TextTertiaryBrush");

    public SnippetViewModel(SnippetItem item) => Item = item;
}
