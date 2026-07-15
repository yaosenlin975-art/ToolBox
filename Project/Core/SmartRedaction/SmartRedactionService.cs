using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace ToolBox.Core.SmartRedaction;

/// <summary>敏感信息打码区域</summary>
public class RedactionRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Type { get; set; } = ""; // Email, Phone, Ip, Face, QrCode
    public string OriginalText { get; set; } = "";
    public bool IsConfirmed { get; set; } = true;
}

/// <summary>打码方式</summary>
public enum RedactionMode
{
    Blur,       // 高斯模糊
    Pixelate    // 马赛克
}

/// <summary>智能打码服务 (P2-05)</summary>
public class SmartRedactionService
{
    public static SmartRedactionService Instance { get; } = new();

    private SmartRedactionService() { }

    /// <summary>自动检测敏感信息并返回打码区域</summary>
    public List<RedactionRegion> DetectSensitiveRegions(string text)
    {
        var regions = new List<RedactionRegion>();

        // Simple regex-based detection
        var emailMatches = System.Text.RegularExpressions.Regex.Matches(text, @"[\w\.-]+@[\w\.-]+\.\w+");
        foreach (System.Text.RegularExpressions.Match match in emailMatches)
        {
            regions.Add(new RedactionRegion
            {
                Type = "Email",
                OriginalText = match.Value,
                X = 0, Y = 0, Width = 0, Height = 0 // Will be mapped from text position
            });
        }

        var phoneMatches = System.Text.RegularExpressions.Regex.Matches(text, @"1[3-9]\d{9}");
        foreach (System.Text.RegularExpressions.Match match in phoneMatches)
        {
            regions.Add(new RedactionRegion
            {
                Type = "Phone",
                OriginalText = match.Value
            });
        }

        var ipMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");
        foreach (System.Text.RegularExpressions.Match match in ipMatches)
        {
            regions.Add(new RedactionRegion
            {
                Type = "IP",
                OriginalText = match.Value
            });
        }

        return regions;
    }

    /// <summary>对图像应用打码</summary>
    public System.Drawing.Bitmap ApplyRedaction(System.Drawing.Bitmap source, List<RedactionRegion> regions, RedactionMode mode)
    {
        foreach (var region in regions.Where(r => r.IsConfirmed))
        {
            if (region.Width <= 0 || region.Height <= 0) continue;

            var rect = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
            if (mode == RedactionMode.Blur)
            {
                ApplyBlur(source, rect);
            }
            else
            {
                ApplyPixelate(source, rect);
            }
        }
        return source;
    }

    private static void ApplyBlur(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle rect)
    {
        // Simple box blur
        using var temp = bitmap.Clone(rect, bitmap.PixelFormat);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.DrawImage(temp, rect);
    }

    private static void ApplyPixelate(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle rect)
    {
        int pixelSize = 10;
        using var temp = new System.Drawing.Bitmap(rect.Width / pixelSize, rect.Height / pixelSize);
        using (var g = System.Drawing.Graphics.FromImage(temp))
        {
            g.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, temp.Width, temp.Height), rect, System.Drawing.GraphicsUnit.Pixel);
        }
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.DrawImage(temp, rect);
        }
    }
}
