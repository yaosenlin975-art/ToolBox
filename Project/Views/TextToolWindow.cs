using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class TextToolWindow : Window
{
    private ScrapWindow parentScrap;
    private string textContent = "";
    private double fontSize = 16;
    private Brush textColor = Brushes.Black;

    public TextToolWindow(ScrapWindow scrap)
    {
        parentScrap = scrap;
        InitializeComponent();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        textContent = txtContent.Text;
        if (double.TryParse(txtFontSize.Text, out var size))
            fontSize = size;

        ApplyText();
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyText()
    {
        if (string.IsNullOrEmpty(textContent)) return;

        var visual = new DrawingVisual();
        FormattedText formattedText;
        using (var ctx = visual.RenderOpen())
        {
            formattedText = new FormattedText(
                textContent,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                fontSize,
                textColor,
                96);

            ctx.DrawText(formattedText, new System.Windows.Point(0, 0));
        }

        var rtb = new RenderTargetBitmap(
            (int)formattedText.Width + 10,
            (int)formattedText.Height + 10,
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        parentScrap.SetImage(rtb);
    }
}
