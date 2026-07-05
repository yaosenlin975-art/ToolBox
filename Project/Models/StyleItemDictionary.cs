using System;
using System.Collections.Generic;

namespace ToolBox.Models;

public static class StyleItemDictionary
{
    private static readonly Dictionary<string, Type> items = new();

    static StyleItemDictionary()
    {
        Register<CCopyStyleItem>("Copy");
        Register<CCloseStyleItem>("Close");
        Register<CPasteStyleItem>("Paste");
        Register<CDustBoxStyleItem>("DustBox");
        Register<CDustScrapStyleItem>("DustScrap");
        Register<CDustEraseStyleItem>("DustErase");
        Register<CAllHideStyleItem>("AllHide");
        Register<CAllShowStyleItem>("AllShow");
        Register<COptionStyleItem>("Option");
        Register<CShowVersionStyleItem>("ShowVersion");
        Register<CShutDownStyleItem>("ShutDown");
        Register<CSeparatorStyleItem>("Separator");
        Register<CImagePngStyleItem>("ImagePng");
        Register<CImageJpegStyleItem>("ImageJpeg");
        Register<CImageBmpStyleItem>("ImageBmp");
        Register<CRotateStyleItem>("Rotate");
        Register<CScaleStyleItem>("Scale");
        Register<COpacityStyleItem>("Opacity");
        Register<CToneReverseStyleItem>("ToneReverse");
        Register<CCompactStyleItem>("Compact");
        Register<CMoveStyleItem>("Move");
        Register<CMarginStyleItem>("Margin");
        Register<CTimerStyleItem>("Timer");
        Register<CTrimStyleItem>("Trim");
        Register<CWindowStyleItem>("Window");
        Register<CPaintStyleItem>("Paint");
        Register<CPicasaUploaderStyleItem>("PicasaUploader");
    }

    public static void Register<T>(string name) where T : IStyleItem
    {
        items[name] = typeof(T);
    }

    public static Type Find(string name)
    {
        items.TryGetValue(name, out var type);
        return type;
    }

    public static IStyleItem Create(string name)
    {
        var type = Find(name);
        if (type == null) return null;
        return (IStyleItem)Activator.CreateInstance(type);
    }

    public static IEnumerable<KeyValuePair<string, Type>> GetAll() => items;
}
