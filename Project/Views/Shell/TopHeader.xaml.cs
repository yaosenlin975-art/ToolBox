using System.Windows;
using System.Windows.Controls;

namespace ToolBox.Views.Shell;

public partial class TopHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TopHeader),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public TopHeader()
    {
        InitializeComponent();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TopHeader h && h.TitleText != null)
            h.TitleText.Text = e.NewValue as string ?? string.Empty;
    }
}
