using System;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.QrCode;

/// <summary>二维码识别服务 (P3-04) - 预留接口,后续接入 ZXing 或 Windows 内置解码</summary>
public class QrCodeService
{
    public static QrCodeService Instance { get; } = new();

    private QrCodeService() { }

    /// <summary>从 BitmapSource 识别二维码</summary>
    public string? ScanFromBitmapSource(BitmapSource source)
    {
        // TODO: 接入 ZXing 或 Windows 内置解码
        System.Diagnostics.Debug.WriteLine("[QrCode] ScanFromBitmapSource called (stub)");
        return null;
    }

    /// <summary>从文件路径识别二维码</summary>
    public string? ScanFromFile(string path)
    {
        // TODO: 接入 ZXing 或 Windows 内置解码
        System.Diagnostics.Debug.WriteLine("[QrCode] ScanFromFile called (stub)");
        return null;
    }
}
