using System;
using ToolBox.Core.Native;

namespace ToolBox.Core.Windows;

public delegate void WindowHandler(object sender, WindowInfo windowInfo);

public class WindowManager
{
    public static readonly WindowManager Instance = new();

    public event WindowHandler WindowActived;
    public event WindowHandler TopMostChanged;

    private WindowInfo foregroundWindow;
    private WindowInfo topMostWindow;
    private IntPtr lastFiredForegroundHandle = IntPtr.Zero;
    private IntPtr lastFiredTopMostHandle = IntPtr.Zero;

    public WindowInfo CurrentForegroundWindow => foregroundWindow;
    public WindowInfo TopMostWindow => topMostWindow;

    public WindowManager Update()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (foregroundWindow.Handle != hwnd)
        {
            foregroundWindow = GetWindowInfo(hwnd);
        }

        var topHwnd = NativeMethods.GetTopWindow(IntPtr.Zero);
        if (topMostWindow.Handle != topHwnd)
        {
            topMostWindow = GetWindowInfo(topHwnd);
        }

        if (foregroundWindow.Handle != lastFiredForegroundHandle)
        {
            lastFiredForegroundHandle = foregroundWindow.Handle;
            WindowActived?.Invoke(this, foregroundWindow);
        }

        if (topMostWindow.Handle != lastFiredTopMostHandle)
        {
            lastFiredTopMostHandle = topMostWindow.Handle;
            TopMostChanged?.Invoke(this, topMostWindow);
        }

        return this;
    }

    public WindowInfo GetWindowInfo(IntPtr hwnd)
    {
        var titleName = NativeMethods.GetWindowTitle(hwnd);
        var className = NativeMethods.GetClassNameStr(hwnd);
        NativeMethods.GetWindowZOrder(hwnd, out var zOrder);
        NativeMethods.GetWindowRect(hwnd, out var rect);

        return new WindowInfo
        {
            Handle = hwnd,
            TitleName = titleName,
            ClassName = className,
            ZOrder = zOrder,
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
        };
    }
}

