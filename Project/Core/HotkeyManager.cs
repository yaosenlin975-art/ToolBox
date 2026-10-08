using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ToolBox.Core.Native;

public class HotkeyManager
{
    public static HotkeyManager Instance { get; } = new();

    private HwndSource hwndSource;
    private readonly Dictionary<int, Action> hotkeyActions = new();
    private bool hooked;
    private int nextId = 9000;

    public HotkeyManager Initialize(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        hwndSource = HwndSource.FromHwnd(helper.Handle);
        // 设置页每次保存都会重跑注册流程，钩子只挂一次：重复挂会让同一次 WM_HOTKEY 回调多次，
        // 开关型热键（显示/隐藏全部贴图、屏幕冻结）表现为按了没反应
        if (hwndSource != null && !hooked)
        {
            hwndSource.AddHook(HwndHook);
            hooked = true;
        }
        return this;
    }

    public HotkeyManager RegisterHotkey(Key key, ModifierKeys modifiers, Action action)
    {
        var id = nextId++;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        // MOD_NOREPEAT(0x4000): 长按不放时系统不再连续投递
        var modFlags = (uint)modifiers | 0x4000;

        var handle = hwndSource?.Handle ?? IntPtr.Zero;
        if (!NativeMethods.RegisterHotKey(handle, id, modFlags, (uint)virtualKey))
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 热键注册失败(可能已被其它程序占用): {modifiers}+{key}");
            return this;
        }
        hotkeyActions[id] = action;
        return this;
    }

    public HotkeyManager UnregisterAll()
    {
        // 启动时就禁用热键的话 hwndSource 仍为 null，此处不能碰它的 Handle
        if (hwndSource != null)
            foreach (var id in hotkeyActions.Keys)
                NativeMethods.UnregisterHotKey(hwndSource.Handle, id);
        hotkeyActions.Clear();
        return this;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (hotkeyActions.TryGetValue(id, out var action))
            {
                action?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }
}
