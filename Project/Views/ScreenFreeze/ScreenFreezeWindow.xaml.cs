using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ToolBox.Views.ScreenFreeze;

public partial class ScreenFreezeWindow : Window
{
    private readonly BitmapSource _image;

    public ScreenFreezeWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;
        FreezeImage.Source = image;
        KeyDown += OnKeyDown;
        MouseRightButtonDown += (s, e) => Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
