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
    private int nextId = 9000;

    public HotkeyManager Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        hwndSource = HwndSource.FromHwnd(helper.Handle);
        hwndSource?.AddHook(HwndHook);
        return this;
    }

    public HotkeyManager RegisterHotkey(Key key, ModifierKeys modifiers, Action action)
    {
        var id = nextId++;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var modFlags = (uint)modifiers;

        NativeMethods.RegisterHotKey(hwndSource.Handle, id, modFlags, (uint)virtualKey);
        hotkeyActions[id] = action;
        return this;
    }

    public HotkeyManager UnregisterAll()
    {
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
