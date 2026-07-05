using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class PaintWindow : Window
{
    private ScrapWindow parentScrap;
    private bool isDrawing;
    private Point lastPoint;
    private WriteableBitmap drawBitmap;
    private System.Windows.Shapes.Path currentPath;
    private Brush currentBrush = Brushes.Red;
    private double currentThickness = 2;

    public PaintWindow(ScrapWindow scrap)
    {
        parentScrap = scrap;
        InitializeComponent();
        LoadScrapImage();
    }

    private void LoadScrapImage()
    {
        var source = parentScrap.ImageViewSource;
        if (source == null) return;

        drawBitmap = new WriteableBitmap(source);
        drawCanvas.Background = new ImageBrush(drawBitmap);
        drawCanvas.Width = source.PixelWidth;
        drawCanvas.Height = source.PixelHeight;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            isDrawing = true;
            lastPoint = e.GetPosition(drawCanvas);
            drawCanvas.CaptureMouse();
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDrawing) return;

        var currentPoint = e.GetPosition(drawCanvas);

        var line = new System.Windows.Shapes.Line
        {
            X1 = lastPoint.X,
            Y1 = lastPoint.Y,
            X2 = currentPoint.X,
            Y2 = currentPoint.Y,
            Stroke = currentBrush,
            StrokeThickness = currentThickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };

        drawCanvas.Children.Add(line);
        lastPoint = currentPoint;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (isDrawing)
        {
            isDrawing = false;
            drawCanvas.ReleaseMouseCapture();
            SaveToBitmap();
        }
    }

    private void SaveToBitmap()
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(drawCanvas);
        var rtb = new RenderTargetBitmap(
            (int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new VisualBrush(drawCanvas), null, new Rect(bounds.Size));
        }
        rtb.Render(visual);
        rtb.Freeze();

        parentScrap.SetImage(rtb);
    }

    private void BtnRed_Click(object sender, RoutedEventArgs e) => currentBrush = Brushes.Red;
    private void BtnBlue_Click(object sender, RoutedEventArgs e) => currentBrush = Brushes.Blue;
    private void BtnGreen_Click(object sender, RoutedEventArgs e) => currentBrush = Brushes.Green;
    private void BtnBlack_Click(object sender, RoutedEventArgs e) => currentBrush = Brushes.Black;
    private void BtnWhite_Click(object sender, RoutedEventArgs e) => currentBrush = Brushes.White;

    private void BtnThin_Click(object sender, RoutedEventArgs e) => currentThickness = 1;
    private void BtnMedium_Click(object sender, RoutedEventArgs e) => currentThickness = 3;
    private void BtnThick_Click(object sender, RoutedEventArgs e) => currentThickness = 5;

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (drawCanvas.Children.Count > 0)
            drawCanvas.Children.RemoveAt(drawCanvas.Children.Count - 1);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        drawCanvas.Children.Clear();
        LoadScrapImage();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveToBitmap();
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
