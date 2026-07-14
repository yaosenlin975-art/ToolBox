// ColorPickerService.cs - 屏幕取色器核心服务
// 职责:像素读取(GetPixel)+ 色值格式计算(HEX/RGB/HSL) + 输出格式管理
// 设计要点:
//   - 单例模式,与项目其他 *Manager.Instance 风格一致
//   - DPI 已由 App.SetProcessDPIAware 声明,GetPixel 直接接收物理像素坐标
//   - 多显示器: GetDC(IntPtr.Zero) 返回桌面 DC,可读跨屏虚拟坐标
//   - 命名空间用 ColorPicker 而非 Color,避免遮蔽 GlobalUsings 中的 Color 别名
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

using ToolBox.Core.Native;

namespace ToolBox.Core.ColorPicker;

/// <summary>取色输出格式</summary>
public enum EColorFormat
{
    Hex,
    Rgb,
    Hsl
}

/// <summary>取色结果数据</summary>
public class ColorInfo
{
    public Color Color { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public string Hex { get; set; } = string.Empty;
    public string Rgb { get; set; } = string.Empty;
    public string Hsl { get; set; } = string.Empty;
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public DateTime PickedAt { get; set; }
}

/// <summary>
/// 屏幕取色器核心服务:像素读取 + 色值计算 + 输出格式管理。
/// </summary>
public class ColorPickerService
{
    private const int ZOOM_SOURCE_SIZE = 30;
    private const int ZOOM_DISPLAY_SIZE = 120;
    private const int ZOOM_SCALE = ZOOM_DISPLAY_SIZE / ZOOM_SOURCE_SIZE; // 4 倍

    public static ColorPickerService Instance { get; } = new();

    private ColorPickerService() { }

    /// <summary>当前输出格式(由 Tab 切换)</summary>
    public EColorFormat OutputFormat { get; private set; } = EColorFormat.Hex;

    /// <summary>格式切换通知</summary>
    public event Action<EColorFormat>? FormatChanged;

    /// <summary>
    /// 获取指定屏幕坐标(物理像素)的像素颜色。
    /// GetPixel 单次调用 &lt; 1μs,适合高频取色。
    /// </summary>
    public ColorInfo PickColor(int screenX, int screenY)
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            // COLORREF = 0x00BBGGRR
            var pixel = NativeMethods.GetPixel(hdc, screenX, screenY);
            var r = (byte)(pixel & 0xFF);
            var g = (byte)((pixel >> 8) & 0xFF);
            var b = (byte)((pixel >> 16) & 0xFF);
            return BuildColorInfo(r, g, b, screenX, screenY);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// 截取鼠标周围 30×30 区域并放大到 120×120 的 BitmapSource。
    /// 用 NearestNeighbor 保持像素硬边界,便于精确取色。
    /// </summary>
    public BitmapSource CaptureZoomRegion(int centerX, int centerY)
    {
        var half = ZOOM_SOURCE_SIZE / 2;
        var startX = centerX - half;
        var startY = centerY - half;

        using var src = new System.Drawing.Bitmap(ZOOM_SOURCE_SIZE, ZOOM_SOURCE_SIZE, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(src))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.CopyFromScreen(startX, startY, 0, 0, new System.Drawing.Size(ZOOM_SOURCE_SIZE, ZOOM_SOURCE_SIZE), System.Drawing.CopyPixelOperation.SourceCopy);
        }

        using var scaled = new System.Drawing.Bitmap(ZOOM_DISPLAY_SIZE, ZOOM_DISPLAY_SIZE, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, 0, 0, ZOOM_DISPLAY_SIZE, ZOOM_DISPLAY_SIZE);
        }

        var hBitmap = scaled.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>切换输出格式:Hex → Rgb → Hsl → Hex</summary>
    public ColorPickerService ToggleFormat()
    {
        OutputFormat = OutputFormat switch
        {
            EColorFormat.Hex => EColorFormat.Rgb,
            EColorFormat.Rgb => EColorFormat.Hsl,
            _ => EColorFormat.Hex
        };
        FormatChanged?.Invoke(OutputFormat);
        return this;
    }

    /// <summary>按指定格式获取色值的字符串表示</summary>
    public static string FormatColor(ColorInfo info, EColorFormat format)
    {
        return format switch
        {
            EColorFormat.Rgb => info.Rgb,
            EColorFormat.Hsl => info.Hsl,
            _ => info.Hex
        };
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static string ToRgb(byte r, byte g, byte b) => $"rgb({r}, {g}, {b})";

    /// <summary>RGB → HSL 转换(标准算法,输出 h/s/l 均为 0~1 浮点)</summary>
    public static void RgbToHsl(byte r8, byte g8, byte b8, out double h, out double s, out double l)
    {
        var r = r8 / 255.0;
        var g = g8 / 255.0;
        var b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        l = (max + min) / 2.0;
        if (delta < 1e-6)
        {
            h = 0; s = 0;
            return;
        }

        s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
        if (max == r)
            h = (g - b) / delta + (g < b ? 6.0 : 0.0);
        else if (max == g)
            h = (b - r) / delta + 2.0;
        else
            h = (r - g) / delta + 4.0;
        h *= 60.0;
    }

    public static string ToHsl(byte r, byte g, byte b)
    {
        RgbToHsl(r, g, b, out var h, out var s, out var l);
        return $"hsl({h:F0}, {s * 100:F0}%, {l * 100:F0}%)";
    }

    private static ColorInfo BuildColorInfo(byte r, byte g, byte b, int x, int y)
    {
        return new ColorInfo
        {
            Color = Color.FromRgb(r, g, b),
            R = r, G = g, B = b,
            Hex = ToHex(r, g, b),
            Rgb = ToRgb(r, g, b),
            Hsl = ToHsl(r, g, b),
            ScreenX = x, ScreenY = y,
            PickedAt = DateTime.Now
        };
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
