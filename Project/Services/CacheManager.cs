﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ToolBox.Models;
using ToolBox.Services;

namespace ToolBox.Services;

public class CacheManager : IScrapAddedListener, IScrapRemovedListener, IScrapLocationChangedListener, IScrapImageChangedListener, IScrapStyleAppliedListener, IScrapStyleRemovedListener
{
    public static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Setuna");

    public static CacheManager Instance { get; } = new();

    public event EventHandler? OnScrapCached;

    public bool IsInit { get; private set; }

    public CacheManager Init()
    {
        IsInit = false;

        if (!Directory.Exists(CachePath))
            Directory.CreateDirectory(CachePath);

        // 启动时清理过期截图
        var options = ToolBoxOption.Load();
        CleanupExpired(options.Data.ScreenshotMaxAge);

        return this;
    }

    public void RestoreScraps(ScrapBook mainBook)
    {
        var directoryInfo = new DirectoryInfo(CachePath);
        if (!directoryInfo.Exists)
        {
            directoryInfo.Create();
            IsInit = true;
            return;
        }

        var directories = directoryInfo.GetDirectories("*", SearchOption.TopDirectoryOnly);
        var list = new List<CacheItem>(directories.Length);

        foreach (var directory in directories)
        {
            var item = CacheItem.Read(directory.FullName);
            if (item?.IsValid == true)
                list.Add(item);
        }

        list.Sort((x, y) => x.SortingOrder.CompareTo(y.SortingOrder));

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var image = item.ReadImage();
            if (image != null)
            {
                mainBook.AddScrap(image,
                    (int)item.Position.X,
                    (int)item.Position.Y,
                    image.PixelWidth,
                    image.PixelHeight,
                    item);
            }
        }

        IsInit = true;
    }

    /// <summary>
    /// 清理超过指定天数的过期缓存截图。
    /// </summary>
    public void CleanupExpired(int maxAgeDays)
    {
        if (maxAgeDays <= 0) return;

        var cutoff = DateTime.Now.AddDays(-maxAgeDays);
        var directories = Directory.GetDirectories(CachePath, "*", SearchOption.TopDirectoryOnly);
        foreach (var dir in directories)
        {
            var dirInfo = new DirectoryInfo(dir);
            if (dirInfo.CreationTime < cutoff)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    public void ScrapAdded(object sender, ScrapEventArgs e)
    {
        var scrap = e.Scrap;
        if (scrap.CacheItem != null) return;

        var cacheItem = CacheItem.Create(
            scrap.CreationTime,
            scrap.ImageViewSource,
            new Point(scrap.Left, scrap.Top),
            scrap.StyleIdValue,
            scrap.StyleClickPointValue);
        scrap.CacheItem = cacheItem;
        OnScrapCached?.Invoke(this, EventArgs.Empty);
    }

    public void ScrapRemoved(object sender, ScrapEventArgs e)
    {
        var cacheItem = e.Scrap?.CacheItem;
        if (cacheItem == null) return;
        e.Scrap.CacheItem = null;
        cacheItem.Delete();
    }

    public void ScrapLocationChanged(object sender, ScrapEventArgs e)
    {
        var cacheItem = e.Scrap?.CacheItem;
        if (cacheItem == null) return;
        cacheItem.Position = new Point(e.Scrap.Left, e.Scrap.Top);
        cacheItem.SaveInfo();
    }

    public void ScrapImageChanged(object sender, ScrapEventArgs e)
    {
        var cacheItem = e.Scrap?.CacheItem;
        var image = e.Scrap?.ImageViewSource;
        if (cacheItem == null || image == null) return;
        cacheItem.SaveImage(image);
    }

    public void ScrapStyleApplied(object sender, ScrapEventArgs e)
    {
        var scrap = e.Scrap;
        var cacheItem = scrap?.CacheItem;
        if (cacheItem == null || scrap.StyleIdValue == 0) return;
        cacheItem.StyleId = scrap.StyleIdValue;
        cacheItem.StyleClickPoint = scrap.StyleClickPointValue;
        cacheItem.SaveInfo();
    }

    public void ScrapStyleRemoved(object sender, ScrapEventArgs e)
    {
        var cacheItem = e.Scrap?.CacheItem;
        if (cacheItem == null) return;
        cacheItem.StyleId = 0;
        cacheItem.StyleClickPoint = new Point(0, 0);
        cacheItem.SaveInfo();
    }
}
