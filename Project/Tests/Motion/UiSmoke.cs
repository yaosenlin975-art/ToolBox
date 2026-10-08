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
        if (failures.ToString().Length != 0) throw new InvalidOperationException(failures.ToString());
        Console.WriteLine($"Passed {checks} WPF theme/template smoke checks (no application startup or user task writes).");
        app.Shutdown();
    }
}
