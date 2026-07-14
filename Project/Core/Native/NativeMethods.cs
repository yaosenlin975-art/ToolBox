using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ToolBox.Core.Native;

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    // ── 剪贴板链(Clipboard Chain)API ──
    // 加入系统剪贴板链,返回下一个链窗口句柄(需保存以便消息转发)
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardViewer(IntPtr hWndNewViewer);

    // 从剪贴板链移除自身,需传入前一个链窗口
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeClipboardChain(IntPtr hWndRemove, IntPtr hWndNewNext);

    public const int WM_DRAWCLIPBOARD = 0x0308;
    public const int WM_CHANGECBCHAIN = 0x030D;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowModuleFileName(IntPtr hWnd, StringBuilder lpFilename, int nSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpFilename, int nSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetWindow", SetLastError = true)]
    public static extern IntPtr GetNextWindow(IntPtr hwnd, int wFlag);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern int BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hSrcDC, int xSrc, int ySrc, int dwRop);

    [DllImport("Gdi32.dll")]
    public static extern IntPtr CreateDC(string display, string c, object b, object a);

    [DllImport("Gdi32.dll")]
    public static extern bool DeleteDC(IntPtr handle);

    // 取色器：读取桌面 DC 指定坐标像素颜色,返回 COLORREF(0x00BBGGRR)
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ── 模拟键盘输入(用于向当前前台窗口发送 Ctrl+V 粘贴) ──
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_V = 0x56;

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    public static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    public const int GW_HWNDNEXT = 2;
    public const int GW_HWNDPREV = 3;
    public const int GW_HWNDLAST = 1;

    public const int SRCCOPY = 0x00CC0020;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_SYSCOMMAND = 0x0112;

    public const uint SC_CLOSE = 0xF060;

    public const byte VK_CONTROL = 0x11;

    public const int CURSOR_SHOWING = 0x00000001;

    public static void DrawCursorImageToScreenImage(System.Drawing.Point position, IntPtr hDC)
    {
        CURSORINFO vCursorInfo;
        vCursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        GetCursorInfo(out vCursorInfo);
        if (vCursorInfo.flags == CURSOR_SHOWING)
        {
            DrawIcon(hDC, position.X, position.Y, vCursorInfo.hCursor);
        }
    }

    public static bool GetWindowZOrder(IntPtr hwnd, out int zOrder)
    {
        var lowestHwnd = GetWindow(hwnd, GW_HWNDLAST);
        var z = 0;
        var hwndTmp = lowestHwnd;
        while (hwndTmp != IntPtr.Zero)
        {
            if (hwnd == hwndTmp)
            {
                zOrder = z;
                return true;
            }
            hwndTmp = GetWindow(hwndTmp, GW_HWNDPREV);
            z++;
        }
        zOrder = int.MinValue;
        return false;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var builder = new StringBuilder(length);
        GetWindowText(hwnd, builder, length + 1);
        return builder.ToString();
    }

    public static string GetModuleName(IntPtr hwnd)
    {
        var builder = new StringBuilder(1024);
        GetWindowModuleFileName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string GetClassNameStr(IntPtr hwnd)
    {
        var builder = new StringBuilder(1024);
        GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
struct CURSORINFO
{
    public int cbSize;
    public int flags;
    public IntPtr hCursor;
    public System.Drawing.Point ptScreenPos;
}
