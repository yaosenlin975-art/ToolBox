using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ToolBox.Core.Native;

public class WpfTrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x800;
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_DOUBLECLICK = 0x08;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;

    private NOTIFYICONDATA iconData;
    private HwndSource hwndSource;
    private IntPtr hwnd;
    private Action onDoubleClick;
    private Action onRightClick;
    private bool disposed;

    public WpfTrayIcon(System.Windows.Window ownerWindow, string tooltip, Icon icon, Action onDoubleClick, Action onRightClick = null)
    {
        this.onDoubleClick = onDoubleClick;
        this.onRightClick = onRightClick;

        var helper = new WindowInteropHelper(ownerWindow);
        hwnd = helper.Handle;
        hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        iconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_DOUBLECLICK,
            uCallbackMessage = WM_TRAYICON,
            hIcon = icon.Handle,
            szTip = tooltip
        };

        Shell_NotifyIcon(NIM_ADD, ref iconData);
    }

    public WpfTrayIcon SetTooltip(string tooltip)
    {
        iconData.szTip = tooltip;
        iconData.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref iconData);
        return this;
    }

    public WpfTrayIcon SetIcon(Icon icon)
    {
        iconData.hIcon = icon.Handle;
        iconData.uFlags = NIF_ICON;
        Shell_NotifyIcon(NIM_MODIFY, ref iconData);
        return this;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = lParam.ToInt32();
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                onDoubleClick?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                onRightClick?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WM_LBUTTONUP)
            {
                Services.LayerManager.Instance.RefreshLayer();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            Shell_NotifyIcon(NIM_DELETE, ref iconData);
            hwndSource?.RemoveHook(WndProc);
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~WpfTrayIcon() => Dispose();

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
