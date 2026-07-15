﻿﻿﻿﻿﻿﻿﻿// ClipboardMonitor.cs - Win32 剪贴板链监听器
// 职责:监听系统剪贴板变化,异步解析内容并产生 ClipboardEntry
// 设计要点:
//   - 单例模式,与 *Manager.Instance 风格一致
//   - 通过 HwndSource.AddHook 挂接窗口消息,接收 WM_DRAWCLIPBOARD / WM_CHANGECBCHAIN
//   - WndProc 仅排队到 Dispatcher 异步处理,避免阻塞剪贴板链(规格要求 ≤1ms)
//   - 支持 Pause/Resume 与 PauseFor 临时静默(密码输入/隐私场景)
//   - 提供 IgnoreNextInternalUpdate 标志,防止自身写入剪贴板时形成回环
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ToolBox.Core.Native;

namespace ToolBox.Core.ClipboardHistory;

public class ClipboardMonitor : IDisposable
{
    public static ClipboardMonitor Instance { get; } = new();

    private const int THUMBNAIL_MAX_SIZE = 200;
    private const int THUMBNAIL_JPEG_QUALITY = 60;

    private HwndSource? hwndSource;
    private IntPtr nextClipboardViewer = IntPtr.Zero;
    private bool isPaused;
    private DispatcherTimer? pauseTimer;
    private bool ignoreNextInternalUpdate;

    private ClipboardMonitor() { }

    /// <summary>新剪贴板内容到达(去重后)</summary>
    public event Action<ClipboardEntry>? EntryCaptured;

    /// <summary>暂停状态变更通知</summary>
    public event Action<bool>? PauseChanged;

    /// <summary>当前是否暂停监听</summary>
    public bool IsPaused => isPaused;

    /// <summary>忽略的应用名集合(由设置页配置)</summary>
    public HashSet<string> IgnoredApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>启动监听,绑定到指定窗口的消息循环</summary>
    public ClipboardMonitor Start(System.Windows.Window window)
    {
        if (hwndSource != null) return this;

        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);
        // 加入剪贴板链,保存下一个窗口句柄用于消息转发
        nextClipboardViewer = NativeMethods.SetClipboardViewer(hwnd);
        return this;
    }

    /// <summary>暂停监听(手动,需 Resume 恢复)</summary>
    public ClipboardMonitor Pause()
    {
        pauseTimer?.Stop();
        pauseTimer = null;
        if (!isPaused)
        {
            isPaused = true;
            PauseChanged?.Invoke(true);
        }
        return this;
    }

    /// <summary>暂停监听指定时长(适合 5 分钟隐私场景)</summary>
    public ClipboardMonitor PauseFor(TimeSpan duration)
    {
        Pause();
        pauseTimer?.Stop();
        pauseTimer = new DispatcherTimer { Interval = duration };
        pauseTimer.Tick += (_, _) =>
        {
            pauseTimer.Stop();
            pauseTimer = null;
            Resume();
        };
        pauseTimer.Start();
        return this;
    }

    /// <summary>恢复监听</summary>
    public ClipboardMonitor Resume()
    {
        pauseTimer?.Stop();
        pauseTimer = null;
        if (isPaused)
        {
            isPaused = false;
            PauseChanged?.Invoke(false);
        }
        return this;
    }

    /// <summary>
    /// 标记下一次剪贴板变化为"自身写入",应被忽略。
    /// 由 ClipboardPopup 点击条目写入剪贴板前调用,避免回环。
    /// </summary>
    public ClipboardMonitor IgnoreNext()
    {
        ignoreNextInternalUpdate = true;
        return this;
    }

    /// <summary>手动抓取当前剪贴板内容(用于按钮触发)</summary>
    public ClipboardEntry? CaptureNow()
    {
        if (isPaused) return null;
        return CaptureFromClipboard();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 仅处理剪贴板链消息,其他消息原样返回
        if (msg == NativeMethods.WM_DRAWCLIPBOARD)
        {
            // 关键:消息处理必须 ≤1ms,只排队,实际工作交给 Dispatcher
            // 即使下面条件不满足也要转发,否则破坏剪贴板链
            if (!isPaused)
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    new Action(OnClipboardChanged),
                    DispatcherPriority.Background);
            }
            // 必须转发给下一个链窗口
            if (nextClipboardViewer != IntPtr.Zero)
                NativeMethods.SendMessage(nextClipboardViewer, msg, wParam, lParam);
            handled = true;
        }
        else if (msg == NativeMethods.WM_CHANGECBCHAIN)
        {
            // wParam = 被移除的窗口句柄,lParam = 新的下一个窗口
            if (wParam == nextClipboardViewer)
                nextClipboardViewer = lParam;
            else if (nextClipboardViewer != IntPtr.Zero)
                NativeMethods.SendMessage(nextClipboardViewer, msg, wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>实际处理剪贴板内容(在 Dispatcher 异步执行)</summary>
    private void OnClipboardChanged()
    {
        // 消费"忽略自身写入"标志
        if (ignoreNextInternalUpdate)
        {
            ignoreNextInternalUpdate = false;
            return;
        }
        if (isPaused) return;

        var sourceApp = GetForegroundAppName();
        // 忽略应用列表(配置的密码管理器等)
        if (sourceApp != null && IgnoredApps.Contains(sourceApp)) return;

        var entry = CaptureFromClipboard(sourceApp);
        if (entry == null) return;

        ClipboardStore.Instance.Add(entry);
        EntryCaptured?.Invoke(entry);
    }

    /// <summary>从当前系统剪贴板读取并构建 ClipboardEntry</summary>
    private ClipboardEntry? CaptureFromClipboard(string? sourceApp = null)
    {
        // 优先级:文件 > 图片 > 文本(避免文件路径同时被当作文本记录)
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files == null || files.Count == 0) return null;
                var content = string.Join("\n", files.Cast<string>());
                var hash = ClipboardStore.ComputeHash(content);
                var entry = ClipboardEntry.Create(EClipboardEntryType.FileList, content, hash);
                entry.SourceApp = sourceApp;
                return entry;
            }
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return null;
                var thumbBytes = GenerateThumbnailJpeg(img);
                if (thumbBytes == null || thumbBytes.Length == 0) return null;
                // 图片类型用缩略图字节哈希去重(原图字节过长,缩略图已足以代表内容)
                var hash = ComputeHashFromBytes(thumbBytes);
                var entry = ClipboardEntry.Create(EClipboardEntryType.Image, string.Empty, hash);
                entry.SourceApp = sourceApp;
                entry.ThumbnailPath = ClipboardStore.Instance.SaveThumbnail(entry.Id, thumbBytes);
                return entry;
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return null;
                var hash = ClipboardStore.ComputeHash(text);
                var entry = ClipboardEntry.Create(EClipboardEntryType.Text, text, hash);
                entry.SourceApp = sourceApp;
                return entry;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolBox] clipboard capture failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 将 BitmapSource 缩放并编码为 200×200 JPEG(质量 60),返回字节数组。
    /// </summary>
    private static byte[]? GenerateThumbnailJpeg(BitmapSource source)
    {
        // 冻结图片使其可跨线程访问
        if (!source.IsFrozen) source.Freeze();

        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w == 0 || h == 0) return null;

        double scale = Math.Min(1.0, (double)THUMBNAIL_MAX_SIZE / Math.Max(w, h));
        var newW = Math.Max(1, (int)(w * scale));
        var newH = Math.Max(1, (int)(h * scale));

        // 用 TransformedBitmap 缩放,再编码为 JPEG
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var encoder = new JpegBitmapEncoder { QualityLevel = THUMBNAIL_JPEG_QUALITY };
        encoder.Frames.Add(BitmapFrame.Create(transformed));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string ComputeHashFromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>获取当前前台窗口的进程名(用于忽略应用列表过滤)</summary>
    private static string? GetForegroundAppName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0) return null;
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        pauseTimer?.Stop();
        pauseTimer = null;
        if (hwndSource != null)
        {
            hwndSource.RemoveHook(WndProc);
            try
            {
                if (nextClipboardViewer != IntPtr.Zero)
                    NativeMethods.ChangeClipboardChain(hwndSource.Handle, nextClipboardViewer);
            }
            catch { /* best-effort */ }
            hwndSource = null;
        }
        GC.SuppressFinalize(this);
    }
}
