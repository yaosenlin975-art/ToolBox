using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.StepCapture;

public class StepCaptureResult
{
    public int StepNumber { get; set; }
    public BitmapSource? BeforeImage { get; set; }
    public BitmapSource? AfterImage { get; set; }
    public Point ClickPosition { get; set; }
    public DateTime Timestamp { get; set; }
}

public class StepCaptureStepEventArgs : EventArgs
{
    public StepCaptureResult Result { get; set; } = new();
}

/// <summary>步骤捕获服务 (P2-04)</summary>
public class StepCaptureService : IDisposable
{
    public static StepCaptureService Instance { get; } = new();

    public bool IsCapturing { get; private set; }
    public int CaptureDelayMs { get; set; } = 200;
    public int HighlightDurationMs { get; set; } = 500;

    public event EventHandler<StepCaptureStepEventArgs>? StepCaptured;
    public event Action? CaptureStarted;
    public event Action? CaptureStopped;

    private List<StepCaptureResult> _steps = new();
    private int _hookId = 0;
    private bool _isClicking = false;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelMouseProc? _proc;

    private StepCaptureService() { }

    public IReadOnlyList<StepCaptureResult> Steps => _steps;

    public void StartCapture()
    {
        if (IsCapturing) return;
        IsCapturing = true;
        _steps.Clear();
        _proc = HookCallback;
        CaptureStarted?.Invoke();
    }

    public void StopCapture()
    {
        IsCapturing = false;
        CaptureStopped?.Invoke();
    }

    public void ClearSteps()
    {
        _steps.Clear();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // WM_LBUTTONDOWN = 0x0201
        if (nCode >= 0 && wParam == (IntPtr)0x0201 && !_isClicking)
        {
            _isClicking = true;
            var pt = GetMousePosition();

            var before = CaptureScreen();
            System.Threading.Tasks.Task.Delay(CaptureDelayMs).ContinueWith(_ =>
            {
                var after = CaptureScreen();
                var step = new StepCaptureResult
                {
                    StepNumber = _steps.Count + 1,
                    BeforeImage = before,
                    AfterImage = after,
                    ClickPosition = pt,
                    Timestamp = DateTime.Now
                };
                _steps.Add(step);
                StepCaptured?.Invoke(this, new StepCaptureStepEventArgs { Result = step });
                _isClicking = false;
            });
        }
        return User32.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static Point GetMousePosition()
    {
        var w32pt = new POINT();
        User32.GetCursorPos(out w32pt);
        return new Point(w32pt.X, w32pt.Y);
    }

    private static BitmapSource CaptureScreen()
    {
        var width = (int)SystemParameters.PrimaryScreenWidth;
        var height = (int)SystemParameters.PrimaryScreenHeight;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height));
        var bitmapData = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bitmapSource = BitmapSource.Create(
            width, height, bitmap.HorizontalResolution, bitmap.VerticalResolution,
            System.Windows.Media.PixelFormats.Bgra32, null,
            bitmapData.Scan0, bitmapData.Stride * height, bitmapData.Stride);
        bitmap.UnlockBits(bitmapData);
        bitmapSource.Freeze();
        return bitmapSource;
    }

    public void Dispose()
    {
        StopCapture();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

internal static class User32
{
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(int hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);
}
