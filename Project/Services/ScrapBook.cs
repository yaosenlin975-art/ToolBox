using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Services;

public class ScrapBook
{
    private readonly List<ScrapWindow> scraps = new();
    private readonly Queue<ScrapWindow> dustBox = new();
    private short dustCapacity = 5;

    public Views.MainWindow BindForm { get; set; }
    public int ScrapCount => scraps.Count;
    public int DustCount => dustBox.Count;
    public bool IsImageDrag { get; set; } = true;
    /// <summary>应用退出时为 true，此时关闭 scrap 不删除缓存，保留跨重启持久化。</summary>
    public bool IsShuttingDown { get; set; }

    public event ScrapEventHandler ScrapAdded;
    public event ScrapEventHandler ScrapRemoved;
    public event ScrapKeyPressHandler KeyPress;

    public ScrapBook(Views.MainWindow mainForm)
    {
        BindForm = mainForm;
    }

    public ScrapBook AddScrap(BitmapSource image, int x, int y, int width, int height)
    {
        return AddScrap(image, x, y, width, height, "");
    }

    public ScrapBook AddScrap(BitmapSource image, int x, int y, int width, int height, string name)
    {
        var scrap = new ScrapWindow();
        if (!string.IsNullOrEmpty(name))
            scrap.Title = name;
        scrap.SetImage(image);
        scrap.Left = x;
        scrap.Top = y;
        scrap.Width = width;
        scrap.Height = height;
        scrap.Manager = this;
        scrap.OnScrapClose += OnScrapClose;
        scrap.OnScrapCreated += OnScrapCreated;
        scrap.OnScrapActive += OnScrapActive;
        scrap.OnScrapInactive += OnScrapInactive;
        scrap.OnScrapLocationChanged += OnScrapLocationChanged;
        scrap.OnScrapImageChanged += OnScrapImageChanged;
        scrap.OnScrapStyleApplied += OnScrapStyleApplied;
        scrap.OnScrapStyleRemoved += OnScrapStyleRemoved;

        ApplyScrapOption(scrap);

        scraps.Add(scrap);
        scrap.Show();
        scrap.InitializedValue = true;
        scrap.RaiseScrapCreated();

        Services.LayerManager.Instance.RegisterWindow(scrap);

        ScrapAdded?.Invoke(this, new ScrapEventArgs { Scrap = scrap });
        return this;
    }

    /// <summary>
    /// 从已有缓存项恢复 scrap：复用其 CreationTime 与 CacheItem，避免重复写盘；
    /// 关闭时 CacheManager.ScrapRemoved 会据此删除缓存。
    /// </summary>
    public ScrapBook AddScrap(BitmapSource image, int x, int y, int width, int height, CacheItem existingCache)
    {
        var scrap = new ScrapWindow();
        if (existingCache != null)
        {
            scrap.CreationTime = existingCache.CreateTime;
            scrap.CacheItem = existingCache;
        }
        scrap.SetImage(image);
        scrap.Left = x;
        scrap.Top = y;
        scrap.Width = width;
        scrap.Height = height;
        scrap.Manager = this;
        scrap.OnScrapClose += OnScrapClose;
        scrap.OnScrapCreated += OnScrapCreated;
        scrap.OnScrapActive += OnScrapActive;
        scrap.OnScrapInactive += OnScrapInactive;
        scrap.OnScrapLocationChanged += OnScrapLocationChanged;
        scrap.OnScrapImageChanged += OnScrapImageChanged;
        scrap.OnScrapStyleApplied += OnScrapStyleApplied;
        scrap.OnScrapStyleRemoved += OnScrapStyleRemoved;

        scraps.Add(scrap);
        ApplyScrapOption(scrap);
        scrap.Show();
        scrap.InitializedValue = true;
        scrap.RaiseScrapCreated();

        Services.LayerManager.Instance.RegisterWindow(scrap);

        // scrap.CacheItem 已存在 → CacheManager.ScrapAdded 会跳过写盘，仅触发事件
        ScrapAdded?.Invoke(this, new ScrapEventArgs { Scrap = scrap });
        return this;
    }

    public ScrapBook RemoveScrap(ScrapWindow scrap)
    {
        if (scrap == null) return this;

        scraps.Remove(scrap);
        scrap.Hide();
        dustBox.Enqueue(scrap);

        while (dustBox.Count > dustCapacity)
        {
            var old = dustBox.Dequeue();
            old.CloseScrap();
        }

        ScrapRemoved?.Invoke(this, new ScrapEventArgs { Scrap = scrap });
        return this;
    }

    public ScrapBook RestoreFromDustBox()
    {
        if (dustBox.Count == 0) return this;
        var scrap = dustBox.Dequeue();
        scrap.Show();
        scraps.Add(scrap);
        ScrapAdded?.Invoke(this, new ScrapEventArgs { Scrap = scrap });
        return this;
    }

    public ScrapBook EraseDustBox()
    {
        while (dustBox.Count > 0)
        {
            var scrap = dustBox.Dequeue();
            scrap.CloseScrap();
        }
        return this;
    }

    public ScrapBook ShowAllScrap()
    {
        foreach (var scrap in scraps)
            scrap.Show();
        return this;
    }

    public ScrapBook HideAllScrap()
    {
        foreach (var scrap in scraps)
            scrap.Hide();
        return this;
    }

    public ScrapBook CloseAllScrap()
    {
        var list = scraps.ToArray();
        foreach (var scrap in list)
            scrap.CloseScrap();
        return this;
    }

    public bool IsActiveScrap(ScrapWindow scrap) => scraps.Contains(scrap);

    public void SelectScrap(ScrapWindow target)
    {
        foreach (var s in scraps)
            s.SetSelected(s == target);
    }

    public void DeselectAll()
    {
        foreach (var s in scraps)
            s.SetSelected(false);
    }

    public ScrapBook OnKeyUp(object sender, Key key)
    {
        KeyPress?.Invoke(sender, new ScrapKeyPressEventArgs { Key = key });
        return this;
    }

    public ScrapBook AddDragImageFileName(string path)
    {
        BindForm?.AddImageListFileName(path);
        return this;
    }

    public ScrapBook AddDragImageUrl(string url, int width = 0, int height = 0)
    {
        BindForm?.AddImageList(new ScrapSourceUrl(url, new Point(0, 0), width, height));
        return this;
    }

    public ScrapBook WClickStyle(ScrapWindow scrap, Point clickPoint)
    {
        var wclickStyleId = BindForm?.GetOptions()?.Scrap.WClickStyleId ?? 0;
        if (wclickStyleId != 0)
        {
            var style = BindForm?.GetOptions()?.FindStyle(wclickStyleId);
            if (style != null)
                style.Apply(scrap, clickPoint);
        }
        return this;
    }

    public IEnumerator<ScrapWindow> GetEnumerator() => scraps.GetEnumerator();

    private void OnScrapClose(object sender, ScrapEventArgs e)
    {
        scraps.Remove(e.Scrap);
        // 用户手动关闭时标记为非浮窗，下次启动不再恢复；应用退出时保留标记以便持久化。
        if (!IsShuttingDown && e.Scrap.CacheItem != null)
        {
            e.Scrap.CacheItem.IsFloating = false;
            e.Scrap.CacheItem.SaveInfo();
        }
    }

    private void OnScrapCreated(object sender, ScrapEventArgs e) { }
    private void OnScrapActive(object sender, ScrapEventArgs e) { }
    private void OnScrapInactive(object sender, ScrapEventArgs e) { }
    private void OnScrapLocationChanged(object sender, ScrapEventArgs e) { }
    private void OnScrapImageChanged(object sender, ScrapEventArgs e) { }
    private void OnScrapStyleApplied(object sender, ScrapEventArgs e) { }
    private void OnScrapStyleRemoved(object sender, ScrapEventArgs e) { }

    private void ApplyScrapOption(Models.ScrapWindow scrap)
    {
        var opt = BindForm?.GetOptions();
        if (opt == null) return;
        scrap.ActiveOpacityValue = 1.0;
        scrap.InactiveOpacityValue = opt.Scrap.InactiveAlphaChange && opt.Scrap.InactiveAlphaValue < 100
            ? opt.Scrap.InactiveAlphaValue / 100.0 : 1.0;
        scrap.RolloverOpacityValue = opt.Scrap.MouseOverAlphaChange && opt.Scrap.MouseOverAlphaValue < 100
            ? opt.Scrap.MouseOverAlphaValue / 100.0 : 1.0;
        if (!opt.Scrap.InactiveAlphaChange && !opt.Scrap.MouseOverAlphaChange)
        {
            scrap.Opacity = 1.0;
        }
        else
        {
            // Apply inactive opacity as initial; will switch on MouseEnter/MouseLeave
            scrap.Opacity = scrap.InactiveOpacityValue;
        }
    }
}





