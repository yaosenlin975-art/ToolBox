using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolBox.Core.Snippets;

/// <summary>
/// Global keyboard hook that detects trigger keywords (e.g., ";email") and expands them
/// by replacing the typed keyword with the snippet content via clipboard paste.
/// Uses WH_KEYBOARD_LL (low-level keyboard hook).
/// </summary>
public sealed class SnippetExpander : IDisposable
{
    private static SnippetExpander? _instance;
    public static SnippetExpander Instance => _instance ??= new SnippetExpander();

    private IntPtr hookId = IntPtr.Zero;
    private NativeHookProc? hookProc;
    private bool isEnabled = true;
    private string triggerPrefix = ";";
    private readonly List<char> inputBuffer = [];
    private readonly HashSet<string> excludedApps = [];

    // Excluded process names (lowercase) - apps where expansion is disabled
    private static readonly HashSet<string> defaultExcludedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv", "code", "notepad++", "sublime_text"
    };

    private delegate IntPtr NativeHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, NativeHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private SnippetExpander() { }

    public void SetEnabled(bool enabled)
    {
        isEnabled = enabled;
        if (!enabled) inputBuffer.Clear();
    }

    public void SetTriggerPrefix(string prefix)
    {
        triggerPrefix = string.IsNullOrEmpty(prefix) ? ";" : prefix;
        inputBuffer.Clear();
    }

    public void AddExcludedApp(string processName)
    {
        if (!string.IsNullOrWhiteSpace(processName))
            excludedApps.Add(processName.Trim().ToLowerInvariant());
    }

    public void Start()
    {
        if (hookId != IntPtr.Zero) return;
        hookProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        if (module != null)
        {
            var moduleHandle = GetModuleHandle(module.ModuleName!);
            hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, moduleHandle, 0);
        }
    }

    public void Stop()
    {
        if (hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookId);
            hookId = IntPtr.Zero;
        }
        inputBuffer.Clear();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vkCode = (int)hookStruct.vkCode;

            // Skip if disabled or in excluded app
            if (!isEnabled || IsExcludedApp())
            {
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }

            // Clear buffer on Escape, Enter, or non-printable
            if (vkCode == VK_ESCAPE || vkCode == VK_RETURN)
            {
                inputBuffer.Clear();
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }

            // Handle backspace
            if (vkCode == VK_BACK)
            {
                if (inputBuffer.Count > 0)
                    inputBuffer.RemoveAt(inputBuffer.Count - 1);
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }

            // Convert VK to char (simplified - works for ASCII printable)
            char ch = VkToChar(vkCode);
            if (ch == '\0')
            {
                inputBuffer.Clear();
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }

            inputBuffer.Add(ch);

            // Keep buffer reasonable size
            if (inputBuffer.Count > 100)
                inputBuffer.RemoveRange(0, inputBuffer.Count - 50);

            // Check if buffer ends with a trigger keyword + space
            if (ch == ' ' && inputBuffer.Count > 1)
            {
                var bufferStr = new string(inputBuffer.ToArray());
                // Look for trigger prefix followed by keyword ending with space
                var lastSpace = bufferStr.LastIndexOf(' ');
                if (lastSpace > 0)
                {
                    var beforeSpace = bufferStr[..lastSpace];
                    var triggerStart = beforeSpace.LastIndexOf(triggerPrefix, StringComparison.Ordinal);
                    if (triggerStart >= 0 && triggerStart == beforeSpace.Length - beforeSpace.Length + beforeSpace.LastIndexOf(triggerPrefix))
                    {
                        var trigger = beforeSpace[triggerStart..];
                        var match = SnippetStore.Instance.FindByTrigger(trigger);
                        if (match != null)
                        {
                            // Found a match! Expand it.
                            var expanded = SnippetStore.ExpandPlaceholders(match.Content);
                            SnippetStore.Instance.IncrementUseCount(match.Id);

                            // Remove the trigger text + space by sending backspaces
                            var charsToDelete = trigger.Length + 1; // +1 for the space
                            for (int i = 0; i < charsToDelete; i++)
                            {
                                SimulateKeyPress(VK_BACK);
                            }

                            // Paste expanded content via clipboard
                            System.Windows.Clipboard.SetText(expanded);
                            System.Threading.Thread.Sleep(50);
                            SimulateCtrlV();

                            inputBuffer.Clear();
                        }
                    }
                }
            }
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private static bool IsExcludedApp()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            uint processId;
            GetWindowThreadProcessId(foreground, out processId);
            var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName.ToLowerInvariant();

            return defaultExcludedApps.Contains(name);
        }
        catch
        {
            return false;
        }
    }

    private static void SimulateKeyPress(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SimulateCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(0x56, 0, 0, UIntPtr.Zero); // V
        keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static char VkToChar(int vk)
    {
        // Simplified VK-to-Char for common printable ASCII
        if (vk >= 0x30 && vk <= 0x39) return (char)('0' + (vk - 0x30)); // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return (char)('a' + (vk - 0x41)); // a-z
        if (vk == VK_SPACE) return ' ';
        if (vk == 0xBA) return ';';
        if (vk == 0xBB) return '=';
        if (vk == 0xBC) return ',';
        if (vk == 0xBD) return '-';
        if (vk == 0xBE) return '.';
        if (vk == 0xBF) return '/';
        if (vk == 0xC0) return '`';
        if (vk == 0xDB) return '[';
        if (vk == 0xDC) return '\\';
        if (vk == 0xDD) return ']';
        if (vk == 0xDE) return '\'';
        return '\0';
    }

    private const byte VK_CONTROL = 0x11;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public void Dispose()
    {
        Stop();
        _instance = null;
    }
}
