using System.Runtime.InteropServices;
using ToolBox.Core.Native;

namespace ToolBox.Core.Window;

/// <summary>P3-03: 窗口置顶/取消置顶（纯 P/Invoke）</summary>
public static class WindowPinner
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private readonly static uint SWP_NOSIZE = 0x0001;
    private readonly static uint SWP_NOMOVE = 0x0002;
    private readonly static uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>切换窗口置顶状态</summary>
    public static bool TogglePin(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        bool isTopmost = IsPinned(hwnd);
        SetWindowPos(hwnd, isTopmost ? HWND_NOTOPMOST : HWND_TOPMOST,
            0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
        return !isTopmost;
    }

    /// <summary>是否已置顶（通过 GetWindowLong 检查）</summary>
    public static bool IsPinned(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        const int GWL_EXSTYLE = -20;
        const uint WS_EX_TOPMOST = 0x0008;
        var exStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        return (exStyle & WS_EX_TOPMOST) != 0;
    }

    /// <summary>切换前台窗口置顶状态</summary>
    public static bool ToggleForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return TogglePin(hwnd);
    }
}
