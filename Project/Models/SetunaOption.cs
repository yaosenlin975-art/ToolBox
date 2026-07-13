using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using System.Xml.Serialization;

namespace ToolBox.Models;

[XmlRoot("ToolBoxOption")]
public class ToolBoxOption
{
    public const int CtrlBit = 1 << 16;
    public const int ShiftBit = 1 << 17;
    public const int AltBit = 1 << 18;
    public const int WinBit = 1 << 19;
    public const int KeyMask = 0xFFFF;

    public ToolBoxOptionData Data { get; set; } = new();
    public ScrapOption Scrap { get; set; } = new();
    public List<CStyle> Styles { get; set; } = new();
    public List<KeyItem> HotKeys { get; set; } = new();
    public bool HotKeyEnable { get; set; } = true;

    [XmlIgnore]
    public Key CaptureHotKey { get; set; } = (Key)((int)Key.D1 | CtrlBit);

    [XmlElement("CaptureHotKey")]
    public int CaptureHotKeyValue
    {
        get => (int)CaptureHotKey;
        set => CaptureHotKey = (Key)value;
    }

    [XmlIgnore]
    public Key HideShowHotKey { get; set; } = (Key)((int)Key.D2 | CtrlBit);

    [XmlElement("HideShowHotKey")]
    public int HideShowHotKeyValue
    {
        get => (int)HideShowHotKey;
        set => HideShowHotKey = (Key)value;
    }

    public string Language { get; set; } = "zh-CN";

    private static readonly string configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Setuna", "config.xml");

    public static ToolBoxOption GetDefaultOption()
    {
        var option = new ToolBoxOption();
        option.Data.AppType = EApplicationType.ApplicationMode;
        option.Data.ShowMainWindow = true;
        option.Data.ShowSplashWindow = true;
        option.Data.DustBoxCapacity = 5;
        option.Data.DustBoxEnable = true;
        option.Data.SelectAreaTransparent = 80;
        option.Data.SelectLineSolid = false;
        option.Data.SelectLineColorR = 0;
        option.Data.SelectLineColorG = 0;
        option.Data.SelectLineColorB = 255;
        option.Data.SelectBackColorR = 0;
        option.Data.SelectBackColorG = 0;
        option.Data.SelectBackColorB = 139;
        option.Scrap.InactiveAlphaChange = true;
        option.Scrap.InactiveAlphaValue = 10;
        option.Scrap.MouseOverAlphaChange = true;
        option.Scrap.MouseOverAlphaValue = 90;
        option.Scrap.ImageDrag = true;
        option.CaptureHotKey = (Key)((int)Key.D1 | CtrlBit);
        option.HideShowHotKey = (Key)((int)Key.D2 | CtrlBit);

        var copyStyle = new CStyle { StyleName = "Copy" };
        copyStyle.AddStyle(new CCopyStyleItem { CopyFromSource = true });
        copyStyle.AddKeyItem(Key.D1 | Key.LeftCtrl);
        option.Styles.Add(copyStyle);

        var copyBorderStyle = new CStyle { StyleName = "Copy (border)" };
        copyBorderStyle.AddStyle(new CCopyStyleItem { CopyFromSource = false });
        copyBorderStyle.AddKeyItem(Key.D2 | Key.LeftCtrl);
        option.Styles.Add(copyBorderStyle);

        var cutStyle = new CStyle { StyleName = "Cut" };
        cutStyle.AddStyle(new CCopyStyleItem { CopyFromSource = true });
        cutStyle.AddStyle(new CCloseStyleItem());
        cutStyle.AddKeyItem(Key.X | Key.LeftCtrl);
        option.Styles.Add(cutStyle);

        var cutBorderStyle = new CStyle { StyleName = "Cut (border)" };
        cutBorderStyle.AddStyle(new CCopyStyleItem { CopyFromSource = false });
        cutBorderStyle.AddStyle(new CCloseStyleItem());
        cutBorderStyle.AddKeyItem(Key.X | Key.LeftCtrl | Key.LeftShift);
        option.Styles.Add(cutBorderStyle);

        var pasteStyle = new CStyle { StyleName = "Paste" };
        pasteStyle.AddStyle(new CPasteStyleItem());
        pasteStyle.AddKeyItem(Key.V | Key.LeftCtrl);
        option.Styles.Add(pasteStyle);

        var saveStyle = new CStyle { StyleName = "Save" };
        saveStyle.AddStyle(new CImagePngStyleItem());
        saveStyle.AddKeyItem(Key.S | Key.LeftCtrl);
        option.Styles.Add(saveStyle);

        option.Scrap.SubMenuStyles = new List<int> { 1, 5, 6, 7 };
        return option;
    }

    public CStyle FindStyle(int styleId) => Styles.Find(s => s.StyleId == styleId);

    public KeyItemBook GetKeyItemBook()
    {
        var book = new KeyItemBook();
        foreach (var style in Styles)
            book.AddKeyItem(style.KeyItems);
        return book;
    }

    public ToolBoxOption Save()
    {
        var dir = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        using var stream = File.Create(configPath);
        var serializer = new XmlSerializer(typeof(ToolBoxOption));
        serializer.Serialize(stream, this);
        return this;
    }

    public static ToolBoxOption Load()
    {
        if (!File.Exists(configPath))
            return GetDefaultOption();
        try
        {
            using var stream = File.OpenRead(configPath);
            var serializer = new XmlSerializer(typeof(ToolBoxOption));
            return (ToolBoxOption)serializer.Deserialize(stream);
        }
        catch { return GetDefaultOption(); }
    }

    public ToolBoxOption DeepCopy()
    {
        var clone = new ToolBoxOption
        {
            Data = new ToolBoxOptionData
            {
                AppType = Data.AppType, ShowMainWindow = Data.ShowMainWindow,
                DupType = Data.DupType, ShowSplashWindow = Data.ShowSplashWindow,
                SelectLineSolid = Data.SelectLineSolid,
                SelectLineColorR = Data.SelectLineColorR, SelectLineColorG = Data.SelectLineColorG, SelectLineColorB = Data.SelectLineColorB,
                SelectBackColorR = Data.SelectBackColorR, SelectBackColorG = Data.SelectBackColorG, SelectBackColorB = Data.SelectBackColorB,
                SelectAreaTransparent = Data.SelectAreaTransparent,
                DustBoxEnable = Data.DustBoxEnable, DustBoxCapacity = Data.DustBoxCapacity,
                ClickCapture1 = Data.ClickCapture1, ClickCapture2 = Data.ClickCapture2,
                ClickCapture3 = Data.ClickCapture3, ClickCapture4 = Data.ClickCapture4,
                ClickCapture6 = Data.ClickCapture6, ClickCapture7 = Data.ClickCapture7,
                ClickCapture8 = Data.ClickCapture8, ClickCapture9 = Data.ClickCapture9,
                TopMostEnabled = Data.TopMostEnabled, CursorEnabled = Data.CursorEnabled,
                FullscreenCursor = Data.FullscreenCursor, MagnifierEnabled = Data.MagnifierEnabled,
                BackgroundTransparentEnabled = Data.BackgroundTransparentEnabled,
                FullscreenCursorSolid = Data.FullscreenCursorSolid,
                FullscreenCursorColorR = Data.FullscreenCursorColorR,
                FullscreenCursorColorG = Data.FullscreenCursorColorG,
                FullscreenCursorColorB = Data.FullscreenCursorColorB
            },
            Scrap = new ScrapOption
            {
                InactiveAlphaChange = Scrap.InactiveAlphaChange, InactiveAlphaValue = Scrap.InactiveAlphaValue,
                MouseOverAlphaChange = Scrap.MouseOverAlphaChange, MouseOverAlphaValue = Scrap.MouseOverAlphaValue,
                ImageDrag = Scrap.ImageDrag, CreateStyleId = Scrap.CreateStyleId, WClickStyleId = Scrap.WClickStyleId,
                SubMenuStyles = Scrap.SubMenuStyles != null ? new List<int>(Scrap.SubMenuStyles) : new List<int>()
            },
            HotKeyEnable = HotKeyEnable,
            CaptureHotKey = CaptureHotKey,
            HideShowHotKey = HideShowHotKey,
            Language = Language
        };
        foreach (var style in Styles)
            clone.Styles.Add(style.DeepCopy());
        foreach (var key in HotKeys)
            clone.HotKeys.Add(new KeyItem(key.KeyCode, key.StyleId));
        return clone;
    }

    public static string KeyToDisplayString(Key key)
    {
        var k = (int)key;
        var sb = new System.Text.StringBuilder();
        if ((k & CtrlBit) != 0) sb.Append("Ctrl+");
        if ((k & ShiftBit) != 0) sb.Append("Shift+");
        if ((k & AltBit) != 0) sb.Append("Alt+");
        if ((k & WinBit) != 0) sb.Append("Win+");
        var plainKey = (Key)(k & KeyMask);
        if (plainKey != Key.None) sb.Append(plainKey);
        return sb.ToString();
    }
}

public class ToolBoxOptionData
{
    public EApplicationType AppType { get; set; }
    public bool ShowMainWindow { get; set; }
    public EOpeningType DupType { get; set; }
    public bool ShowSplashWindow { get; set; } = true;
    public bool SelectLineSolid { get; set; }
    public byte SelectLineColorR { get; set; } = 0;
    public byte SelectLineColorG { get; set; } = 0;
    public byte SelectLineColorB { get; set; } = 255;
    public byte SelectBackColorR { get; set; } = 0;
    public byte SelectBackColorG { get; set; } = 0;
    public byte SelectBackColorB { get; set; } = 139;
    public int SelectAreaTransparent { get; set; } = 80;
    public bool DustBoxEnable { get; set; } = true;
    public ushort DustBoxCapacity { get; set; } = 5;
    public bool ClickCapture1 { get; set; }
    public bool ClickCapture2 { get; set; }
    public bool ClickCapture3 { get; set; }
    public bool ClickCapture4 { get; set; }
    public bool ClickCapture6 { get; set; }
    public bool ClickCapture7 { get; set; }
    public bool ClickCapture8 { get; set; }
    public bool ClickCapture9 { get; set; }
    public bool TopMostEnabled { get; set; }
    public bool CursorEnabled { get; set; }
    public bool FullscreenCursor { get; set; }
    public bool MagnifierEnabled { get; set; }
    public bool BackgroundTransparentEnabled { get; set; }
    public bool FullscreenCursorSolid { get; set; } = true;
    public byte FullscreenCursorColorR { get; set; } = 255;
    public byte FullscreenCursorColorG { get; set; } = 165;
    public byte FullscreenCursorColorB { get; set; } = 0;
    public int CompactOpacity { get; set; } = 50;
    public bool AutoScreenshotEnabled { get; set; }
    public string AutoScreenshotCron { get; set; } = "0 */30 * * * *";
    public bool DailyReportEnabled { get; set; }
    public string DailyReportTime { get; set; } = "18:00";
    public string Theme { get; set; } = "System";
    public int ScreenshotMaxAge { get; set; } = 30;
    public int ChatFontSize { get; set; } = 13;
}

public class ScrapOption
{
    public bool InactiveAlphaChange { get; set; }
    public int InactiveAlphaValue { get; set; } = 10;
    public bool MouseOverAlphaChange { get; set; }
    public int MouseOverAlphaValue { get; set; } = 90;
    public bool ImageDrag { get; set; } = true;
    public int CreateStyleId { get; set; }
    public int WClickStyleId { get; set; }
    public List<int> SubMenuStyles { get; set; } = new();
}
