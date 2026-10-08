using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
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
        if (failures.ToString().Length != 0) throw new InvalidOperationException(failures.ToString());
        Console.WriteLine($"Passed {checks} WPF theme/template smoke checks (no application startup or user task writes).");
        app.Shutdown();
    }

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
