using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class UiSmoke
{
    [STAThread]
    private static void Main(string[] args)
    {
        var dll = Path.GetFullPath(args[0]);
        var directory = Path.GetDirectoryName(dll)!;
        var root = Path.Combine(Path.GetTempPath(), "ToolBox.MotionChecks", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", root);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(failures));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        int checks = 0;
        try
        {
            foreach (string theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries.Clear();
                foreach (string resource in new[] { "DesignTokens", theme, "Lang zh-CN" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/ToolBox;component/Themes/{resource}.xaml") });
                foreach (string key in new[] { "BtnDefault", "BtnPrimary" })
                {
                    var button = new Button { Style = (Style)app.FindResource(key), Content = "Motion" };
                    button.ApplyTemplate();
                    button.Measure(new Size(200, 50));
                    if (button.Template.FindName("hoverLayer", button) is not Border ||
                        button.Template.FindName("pressedLayer", button) is not Border)
                        throw new InvalidOperationException($"Missing state layers: {theme}/{key}");
                    checks++;
                }
                var compact = (Window)Activator.CreateInstance(assembly.GetType("ToolBox.Views.CompactToolboxWindow", true)!)!;
                compact.ApplyTemplate();
                compact.Measure(new Size(360, 480));
                var state = compact.GetType().GetField("edgeState", BindingFlags.Instance | BindingFlags.NonPublic)!;
                if (state.GetValue(compact)!.ToString() != "Visible") throw new InvalidOperationException("Invalid initial edge state");
                compact.GetType().GetMethod("ApplyAutoHide")!.Invoke(compact, new object[] { false });
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                checks++;
            }
            if (args.Contains("--todo-dismiss")) CheckTodoDismiss(assembly, root);
            if (args.Contains("--edge-docking")) CheckEdgeDocking(assembly, root);
            if (failures.ToString().Length != 0) throw new InvalidOperationException(failures.ToString());
            Console.WriteLine($"Passed {checks} WPF theme/template smoke checks.");
        }
        finally
        {
            if (!Dispatcher.CurrentDispatcher.HasShutdownStarted) app.Shutdown();
        }
        if (args.Contains("--edge-docking")) Environment.Exit(0);
    }

    private static void CheckEdgeDocking(Assembly assembly, string root)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var storeType = assembly.GetType("ToolBox.Core.Todo.TodoStore", true)!;
        var store = storeType.GetProperty("Instance")!.GetValue(null)!;
        var storePath = (string)storeType.GetField("filePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        if (!storePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Edge docking regression checks require an isolated DEBUG assembly.");

        var windowType = assembly.GetType("ToolBox.Views.CompactToolboxWindow", true)!;
        var window = (Window)Activator.CreateInstance(windowType)!;
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.IsHitTestVisible = false;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        int checks = 0;
        void Assert(bool condition, string name)
        {
            checks++;
            if (!condition) throw new InvalidOperationException($"Failed edge docking check: {name}");
        }

        var geometryType = assembly.GetType("ToolBox.Views.EdgeDockGeometry", true)!;
        var sideType = assembly.GetType("ToolBox.Views.DockSide", true)!;
        var outerEdge = geometryType.GetMethod("IsOuterEdge", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bottomSide = Enum.Parse(sideType, "Bottom");
        var workBelowTaskbar = new Rect(0, 0, 1920, 1040);
        var taskbarWindow = new Rect(600, 940, 600, 100);
        var currentMonitor = new Rect(0, 0, 1920, 1080);
        bool IsBottomOuter(Rect[] monitors) => (bool)outerEdge.Invoke(null,
            new object[] { taskbarWindow, workBelowTaskbar, bottomSide, monitors })!;
        Assert(IsBottomOuter(new[] { currentMonitor }),
            "current monitor does not block its own bottom work-area edge behind the taskbar");
        Assert(!IsBottomOuter(new[] { currentMonitor, new Rect(0, 1030, 1920, 200) }),
            "an actual overlapping monitor below still blocks the bottom edge");

        try
        {
            var applyAutoHide = windowType.GetMethod("ApplyAutoHide")!;
            void SetAutoHide(bool enabled) => applyAutoHide.Invoke(window, new object[] { enabled });
            SetAutoHide(false);
            window.Show();
            if (!PumpUntil(() => ReadGeometry(window).Success, 5000))
                throw new InvalidOperationException("Timed out waiting for edge test window initialization.");

            var dpi = VisualTreeHelper.GetDpi(window);
            var geometry = ReadGeometry(window);
            Assert(Math.Abs(geometry.ScaleX - dpi.DpiScaleX) < 0.01 &&
                   Math.Abs(geometry.ScaleY - dpi.DpiScaleY) < 0.01,
                "monitor geometry uses current visual DPI scales");

            var pointer = GetCursorPosition();
            void MoveToCenter()
            {
                var g = ReadGeometry(window);
                SetPhysicalOrigin(window, g.Work.Left + (g.Work.Width - g.Window.Width) / 2,
                    g.Work.Top + (g.Work.Height - g.Window.Height) / 2, g);
                PumpFor(80);
            }
            void PrepareSide(string side, double overX = 36, double overY = 36)
            {
                SetAutoHide(false);
                MoveToCenter();
                SetAutoHide(true);
                var g = ReadGeometry(window);
                double x = g.Work.Left + (g.Work.Width - g.Window.Width) / 2;
                double y = g.Work.Top + (g.Work.Height - g.Window.Height) / 2;
                if (side is "Left" or "Right")
                    y = pointer.Y > g.Work.Top + g.Work.Height / 2
                        ? g.Work.Top + 20 * g.ScaleY : g.Work.Bottom - g.Window.Height - 20 * g.ScaleY;
                else
                    x = pointer.X > g.Work.Left + g.Work.Width / 2
                        ? g.Work.Left + 20 * g.ScaleX : g.Work.Right - g.Window.Width - 20 * g.ScaleX;
                if (side == "Left") x = g.Work.Left - overX * g.ScaleX;
                if (side == "Right") x = g.Work.Right - g.Window.Width + overX * g.ScaleX;
                if (side == "Top") y = g.Work.Top - overY * g.ScaleY;
                if (side == "Bottom") y = g.Work.Bottom - g.Window.Height + overY * g.ScaleY;
                SetPhysicalOrigin(window, x, y, g);
                PumpFor(80);
            }

            string State() => windowType.GetField("edgeState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!.ToString()!;
            string DockSide() => windowType.GetField("dockSide", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!.ToString()!;
            void Invoke(string name, params object?[] values) =>
                windowType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values);
            void StartSnap() => Invoke("SnapAfterDrag");
            void CompleteHide(string side, bool checkDelay)
            {
                var before = ReadGeometry(window).Window;
                var watch = Stopwatch.StartNew();
                StartSnap();
                PumpFor(300);
                var early = ReadGeometry(window).Window;
                Assert(State() != "Hidden" && Math.Abs(early.Left - before.Left) < 3 &&
                       Math.Abs(early.Top - before.Top) < 3,
                    $"{side} remains in place during the 800ms idle delay");
                bool hidden = PumpUntil(() => State() == "Hidden", checkDelay ? 1500 : 2200);
                Assert(hidden, $"{side} hides after align without another 800ms wait");
                if (checkDelay)
                    Assert(watch.ElapsedMilliseconds >= 650, "idle hide does not begin immediately");
                Assert(DockSide() == side, $"{side} selected as dock side");
                CheckHiddenStrip(window, side, ReadGeometry(window).Work, Assert);
            }

            PrepareSide("Right");
            CompleteHide("Right", true);
            SetAutoHide(false);
            Assert(State() == "Visible", "disabling auto hide restores a hidden window immediately");
            Assert(((FrameworkElement)window.FindName("RootBorder")!).Visibility == Visibility.Visible,
                "disabled auto hide restores content hit target");

            foreach (string side in new[] { "Left", "Top", "Bottom" })
            {
                PrepareSide(side);
                CompleteHide(side, false);
                SetAutoHide(false);
            }

            PrepareSide("Right");
            StartSnap();
            Invoke("Window_MouseEnter", window, null);
            PumpFor(950);
            Assert(State() != "Hidden", "pointer re-entry cancels the pending hide timer");
            SetAutoHide(false);

            if (SystemParameters.ClientAreaAnimation)
            {
                PrepareSide("Top");
                StartSnap();
                bool aligning = PumpUntil(() => State() == "Aligning", 1250);
                Assert(aligning, "top docking enters its align animation after the idle delay");
                if (aligning)
                {
                    Invoke("TodoCollapse_Click", window, new RoutedEventArgs());
                    PumpFor(300);
                    Assert(State() == "Visible" && HasNoPositionAnimations(window),
                        "top fold cancels docking and clears both position clocks");
                }
                SetAutoHide(false);

                PrepareSide("Left");
                StartSnap();
                bool hiding = PumpUntil(() => State() == "Hiding", 1250);
                Assert(hiding, "left docking enters its hide animation");
                if (hiding)
                {
                    Invoke("Window_MouseEnter", window, null);
                    SetAutoHide(false);
                    PumpFor(300);
                    Assert(State() == "Visible" && HasNoPositionAnimations(window),
                        "rapid reverse supersedes stale hide completion and clears both position clocks");
                }
            }

            PrepareSide("Bottom", 24, 64);
            StartSnap();
            bool cornerHidden = PumpUntil(() => State() == "Hidden", 1800);
            var corner = ReadGeometry(window);
            Assert(cornerHidden && DockSide() == "Bottom", "deeper bottom overrun wins at a corner");
            Assert(corner.Window.Left >= corner.Work.Left - 2 && corner.Window.Right <= corner.Work.Right + 2,
                "corner correction returns the other axis to the work area");
            CheckHiddenStrip(window, "Bottom", corner.Work, Assert);

            var onActivated = windowType.GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic);
            onActivated?.Invoke(window, new object[] { EventArgs.Empty });
            Assert(State() == "Visible", "activation callback restores a hidden window without native focus change");
            SetAutoHide(true);
            SetPhysicalOrigin(window, corner.Work.Right + 80 * corner.ScaleX, corner.Work.Bottom + 80 * corner.ScaleY, corner);
            var hook = windowType.GetMethod("WindowHook", BindingFlags.Instance | BindingFlags.NonPublic);
            if (hook == null) throw new InvalidOperationException("Window DPI/message hook was not found.");
            hook.Invoke(window, new object?[] { IntPtr.Zero, 0x02E0, IntPtr.Zero, IntPtr.Zero, false });
            PumpFor(120);
            var restored = ReadGeometry(window);
            Assert(restored.Window.Left >= restored.Work.Left - 2 &&
                   restored.Window.Top >= restored.Work.Top - 2 &&
                   restored.Window.Right <= restored.Work.Right + 2 &&
                   restored.Window.Bottom <= restored.Work.Bottom + 2,
                "DPI change notification constrains window to current work area");
            window.Hide();
            window.Show();
            PumpFor(100);
            Assert(State() == "Visible" && ((FrameworkElement)window.FindName("RootBorder")!).Visibility == Visibility.Visible,
                "external Hide/Show restores the window body");
        }
        finally
        {
            if (!window.Dispatcher.HasShutdownStarted)
            {
                window.Dispatcher.ShutdownStarted += (_, _) => { if (!closed) window.Close(); };
                window.Dispatcher.InvokeShutdown();
            }
            if (!closed) window.Close();
        }

        Console.WriteLine($"Passed {checks} isolated real-window edge docking checks.");
    }

    private static (bool Success, Rect Work, Rect Window, double ScaleX, double ScaleY) ReadGeometry(Window window)
    {
        var method = window.GetType().GetMethod("TryGetScreenGeometry", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Compact window geometry test entry point was not found.");
        object?[] values = { Rect.Empty, Rect.Empty, 0d, 0d };
        bool success = (bool)method.Invoke(window, values)!;
        return (success, (Rect)values[0]!, (Rect)values[1]!, (double)values[2]!, (double)values[3]!);
    }

    private static void SetPhysicalOrigin(Window window, double x, double y,
        (bool Success, Rect Work, Rect Window, double ScaleX, double ScaleY) geometry)
    {
        if (!geometry.Success) throw new InvalidOperationException("Unable to read current window geometry.");
        window.Left += (x - geometry.Window.Left) / geometry.ScaleX;
        window.Top += (y - geometry.Window.Top) / geometry.ScaleY;
    }

    private static bool HasNoPositionAnimations(Window window)
    {
        double left = (double)window.GetValue(Window.LeftProperty);
        double top = (double)window.GetValue(Window.TopProperty);
        double baseLeft = (double)window.GetAnimationBaseValue(Window.LeftProperty);
        double baseTop = (double)window.GetAnimationBaseValue(Window.TopProperty);
        return Math.Abs(left - baseLeft) < 0.5 && Math.Abs(top - baseTop) < 0.5;
    }

    private static void CheckHiddenStrip(Window window, string side, Rect work, Action<bool, string> assert)
    {
        var body = (FrameworkElement)window.FindName("RootBorder")!;
        var rootGrid = (FrameworkElement)window.FindName("RootGrid")!;
        var strip = (FrameworkElement)window.FindName("HiddenStrip")!;
        var arrow = (TextBlock)window.FindName("StripArrow")!;
        assert(body.Visibility == Visibility.Hidden && strip.Visibility == Visibility.Visible,
            $"{side} hidden body exposes only its grip");
        assert(side is "Left" or "Right" ? strip.ActualWidth <= 20 && strip.ActualWidth >= 8
                : strip.ActualHeight <= 20 && strip.ActualHeight >= 8,
            $"{side} grip is oriented along the matching edge");
        assert(rootGrid.Clip is RectangleGeometry,
            $"{side} applies a work-area clip while hidden");
        if (rootGrid.Clip is RectangleGeometry clip)
        {
            var rect = clip.Rect;
            double clipTolerance = 3;
            if (side == "Left") assert(rect.Right >= rootGrid.ActualWidth - clipTolerance && rect.Width <= 20,
                "left clipping keeps only the grip within the work area");
            if (side == "Right") assert(rect.Left <= clipTolerance && rect.Width <= 20,
                "right clipping keeps only the grip within the work area");
            if (side == "Top") assert(rect.Bottom >= rootGrid.ActualHeight - clipTolerance && rect.Height <= 20,
                "top clipping keeps only the grip within the work area");
            if (side == "Bottom") assert(rect.Top <= clipTolerance && rect.Height <= 20,
                "bottom clipping keeps only the grip above the taskbar");
        }
        string expectedArrow = side switch { "Left" => "▶", "Right" => "◀", "Top" => "▼", _ => "▲" };
        assert(arrow.Text == expectedArrow, $"{side} grip arrow points inward");

        var first = strip.PointToScreen(new Point(0, 0));
        var last = strip.PointToScreen(new Point(strip.ActualWidth, strip.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(window);
        double toleranceX = 3 * dpi.DpiScaleX;
        double toleranceY = 3 * dpi.DpiScaleY;
        if (side == "Left") assert(first.X >= work.Left - toleranceX && last.X <= work.Left + 20 * dpi.DpiScaleX,
            "left grip remains in current work area");
        if (side == "Right") assert(last.X <= work.Right + toleranceX && first.X >= work.Right - 20 * dpi.DpiScaleX,
            "right grip remains in current work area");
        if (side == "Top") assert(first.Y >= work.Top - toleranceY && last.Y <= work.Top + 20 * dpi.DpiScaleY,
            "top grip remains in current work area");
        if (side == "Bottom") assert(last.Y <= work.Bottom + toleranceY && first.Y >= work.Bottom - 20 * dpi.DpiScaleY,
            "bottom grip remains above the taskbar work-area boundary");
    }

    private static bool PumpUntil(Func<bool> completed, int timeoutMs)
    {
        if (completed()) return true;
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (completed() || watch.ElapsedMilliseconds >= timeoutMs) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return completed();
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    private static ScreenPoint GetCursorPosition() =>
        GetCursorPos(out var point) ? point : throw new InvalidOperationException("Unable to read cursor position.");

    private static void CheckTodoDismiss(Assembly assembly, string root)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var storeType = assembly.GetType("ToolBox.Core.Todo.TodoStore", true)!;
        var store = storeType.GetProperty("Instance")!.GetValue(null)!;
        var path = (string)storeType.GetField("filePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Todo regression checks require an isolated DEBUG assembly.");
        var add = storeType.GetMethod("AddAsync")!;
        object NewItem()
        {
            var values = add.GetParameters().Select(p => p.DefaultValue).ToArray();
            values[0] = "Detail window regression";
            var task = (Task)add.Invoke(store, values)!;
            WaitUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        void StoreAction(string name, object item)
        {
            var task = (Task)storeType.GetMethod(name)!.Invoke(store, new[] { item.GetType().GetProperty("Id")!.GetValue(item) })!;
            WaitUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        foreach (string action in new[] { "DetailTrashBtn", "DetailRestoreBtn", "DeleteBtn", "missing" })
        {
            object item = action == "missing" ? Activator.CreateInstance(assembly.GetType("ToolBox.Core.Todo.TodoItem", true)!)! : NewItem();
            if (action is "DetailRestoreBtn" or "DeleteBtn") StoreAction("TrashAsync", item);
            var window = (Window)Activator.CreateInstance(assembly.GetType("ToolBox.Views.Todo.TodoDetailWindow", true)!, item, null)!;
            var panel = (FrameworkElement)window.FindName("DetailPanel");
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            var button = (Button)panel.FindName(action == "missing" ? "DetailTrashBtn" : action);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (action is "DetailTrashBtn" or "DeleteBtn") WaitUntil(() => closed);
            else
            {
                if (action == "DetailRestoreBtn") WaitUntil(() => !(bool)item.GetType().GetProperty("IsTrashed")!.GetValue(item)!);
                if (closed) throw new InvalidOperationException($"Unexpected dismissal: {action}");
                window.Close();
            }
        }
        var view = (FrameworkElement)Activator.CreateInstance(assembly.GetType("ToolBox.Views.Todo.TodoView", true)!)!;
        view.GetType().GetMethod("ShowDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new[] { NewItem() });
        var embedded = (FrameworkElement)view.FindName("DetailPanel");
        ((Button)embedded.FindName("DetailTrashBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => embedded.Visibility == Visibility.Collapsed);
        if (((FrameworkElement)view.FindName("EmptyHint")).Visibility != Visibility.Visible)
            throw new InvalidOperationException("Embedded detail did not return to empty state.");
        Console.WriteLine("Passed 5 isolated Todo dismissal checks: trash, permanent delete, restore, failed trash, embedded detail.");
    }

    private static void WaitUntil(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (completed() || watch.Elapsed > TimeSpan.FromSeconds(5)) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (!completed()) throw new InvalidOperationException("Timed out waiting for Todo UI action.");
    }
}
