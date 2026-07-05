using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class LayerRenameWindow : Window
{
    private ScrapWindow parentScrap;

    public LayerRenameWindow(ScrapWindow scrap)
    {
        parentScrap = scrap;
        InitializeComponent();
        txtName.Text = scrap.Title;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        parentScrap.Title = txtName.Text;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
