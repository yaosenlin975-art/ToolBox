using System;
using System.Collections.Generic;
using System.Windows;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class StyleItemSelectWindow : Window
{
    public IStyleItem SelectedItem { get; private set; }

    public StyleItemSelectWindow()
    {
        InitializeComponent();
        LoadStyleItems();
    }

    private void LoadStyleItems()
    {
        var items = new List<string>();
        foreach (var kvp in StyleItemDictionary.GetAll())
        {
            items.Add(kvp.Key);
        }
        lstStyleItems.ItemsSource = items;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (lstStyleItems.SelectedItem is string name)
        {
            SelectedItem = StyleItemDictionary.Create(name);
            DialogResult = true;
        }
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
