using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class StyleEditWindow : Window
{
    private CStyle editingStyle;
    public CStyle Result { get; private set; }

    public StyleEditWindow(CStyle style)
    {
        InitializeComponent();
        editingStyle = style;
        Result = style.DeepCopy();
        LoadStyle();
    }

    private void LoadStyle()
    {
        txtName.Text = Result.StyleName;
        lstItems.ItemsSource = Result.Items;
        lstKeys.ItemsSource = Result.KeyItems;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Result.StyleName = txtName.Text;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnAddItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new StyleItemSelectWindow();
        if (dialog.ShowDialog() == true)
        {
            Result.Items.Add(dialog.SelectedItem);
            lstItems.Items.Refresh();
        }
    }

    private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (lstItems.SelectedItem is IStyleItem item)
        {
            Result.Items.Remove(item);
            lstItems.Items.Refresh();
        }
    }

    private void BtnAddKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyInputWindow();
        if (dialog.ShowDialog() == true)
        {
            Result.AddKeyItem(dialog.SelectedKey);
            lstKeys.Items.Refresh();
        }
    }

    private void BtnRemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (lstKeys.SelectedItem is KeyItem key)
        {
            Result.KeyItems.Remove(key);
            lstKeys.Items.Refresh();
        }
    }
}
