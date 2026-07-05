using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ToolBox.Core.Windows;
using ToolBox.Models;

namespace ToolBox.Services;

public class LayerManager
{
    public static LayerManager Instance { get; } = new();

    private readonly Dictionary<Window, int> windowLayers = new();
    private int maxSortingOrder;
    private int suspendCount;
    private bool initialized;
    private bool isRefreshing;

    public LayerManager Init()
    {
        if (initialized) return this;
        initialized = true;

        WindowManager.Instance.WindowActived += OnWindowActived;
        WindowManager.Instance.TopMostChanged += OnTopMostChanged;

        return this;
    }

    public LayerManager DeInit()
    {
        WindowManager.Instance.WindowActived -= OnWindowActived;
        WindowManager.Instance.TopMostChanged -= OnTopMostChanged;
        initialized = false;
        return this;
    }

    public LayerManager SuspendRefresh()
    {
        suspendCount++;
        return this;
    }

    public LayerManager ResumeRefresh()
    {
        suspendCount = Math.Max(0, suspendCount - 1);
        return this;
    }

    public int GetNextSortingOrder()
    {
        if (maxSortingOrder > 1000)
            OptimizeLayerCounter();
        return ++maxSortingOrder;
    }

    public LayerManager RegisterWindow(Window window)
    {
        windowLayers[window] = GetNextSortingOrder();
        RefreshLayer();
        return this;
    }

    public LayerManager UnregisterWindow(Window window)
    {
        windowLayers.Remove(window);
        return this;
    }

    public LayerManager SetTopMost(Window window)
    {
        if (windowLayers.ContainsKey(window))
            windowLayers[window] = GetNextSortingOrder();
        window.Topmost = true;
        return this;
    }

    public LayerManager RefreshLayer()
    {
        if (suspendCount > 0 || isRefreshing) return this;
        isRefreshing = true;

        try
        {
            var sorted = new List<KeyValuePair<Window, int>>(windowLayers);
            sorted.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var kvp in sorted)
            {
                if (kvp.Key is ScrapWindow scrap && scrap.IsVisible)
                    scrap.Topmost = true;
            }
        }
        finally
        {
            isRefreshing = false;
        }

        return this;
    }

    public LayerManager UpdateLayerFromExternal(WindowInfo windowInfo)
    {
        if (suspendCount > 0) return this;

        if (windowInfo.IsEmpty) return this;

        foreach (var kvp in windowLayers)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(kvp.Key);
            if (helper.Handle == windowInfo.Handle)
                return this;
        }

        RefreshLayer();
        return this;
    }

    private void OnWindowActived(object sender, WindowInfo windowInfo)
    {
        UpdateLayerFromExternal(windowInfo);
    }

    private void OnTopMostChanged(object sender, WindowInfo windowInfo)
    {
        UpdateLayerFromExternal(windowInfo);
    }

    private void OptimizeLayerCounter()
    {
        var sorted = new List<KeyValuePair<Window, int>>(windowLayers);
        sorted.Sort((a, b) => a.Value.CompareTo(b.Value));

        windowLayers.Clear();
        maxSortingOrder = 0;
        foreach (var kvp in sorted)
            windowLayers[kvp.Key] = ++maxSortingOrder;
    }
}



