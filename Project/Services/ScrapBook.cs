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

        scraps.Add(scrap);
        scrap.Show();
        scrap.InitializedValue = true;
        scrap.RaiseScrapCreated();

        Services.LayerManager.Instance.RegisterWindow(scrap);

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
    }

    private void OnScrapCreated(object sender, ScrapEventArgs e) { }
    private void OnScrapActive(object sender, ScrapEventArgs e) { }
    private void OnScrapInactive(object sender, ScrapEventArgs e) { }
    private void OnScrapLocationChanged(object sender, ScrapEventArgs e) { }
    private void OnScrapImageChanged(object sender, ScrapEventArgs e) { }
    private void OnScrapStyleApplied(object sender, ScrapEventArgs e) { }
    private void OnScrapStyleRemoved(object sender, ScrapEventArgs e) { }
}





