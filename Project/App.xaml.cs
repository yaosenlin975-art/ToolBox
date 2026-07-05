using System;
using System.Threading;
using System.Windows;

namespace ToolBox;

public partial class App : Application
{
    private static readonly Mutex SingleMutex = new(false, "Global\\ToolBox_SingleInstance_Mutex");
    private Views.MainWindow mainWindow;
    private Views.CompactToolboxWindow compactToolbox;
    private Views.WorkbenchWindow? workbench;

    public static Views.CompactToolboxWindow? CompactToolbox => Current is App app ? app.compactToolbox : null;
    public static Views.WorkbenchWindow? Workbench => Current is App app ? app.workbench : null;
    public static Views.MainWindow? MainWindow => Current is App app ? app.mainWindow : null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleMutex.WaitOne(0, false))
        {
            Shutdown();
            return;
        }

        var options = Models.ToolBoxOption.Load();
        var langFile = options.Language == "en-US" ? "Themes/Lang en-US.xaml" : "Themes/Lang zh-CN.xaml";
        var langDict = new ResourceDictionary { Source = new System.Uri(langFile, System.UriKind.Relative) };
        Resources.MergedDictionaries.Add(langDict);

        Core.Theming.ThemeManager.Instance.Initialize(options.Data.Theme);

        mainWindow = new Views.MainWindow();
        mainWindow.Show();

        compactToolbox = new Views.CompactToolboxWindow();
        compactToolbox.Show();
        workbench = new Views.WorkbenchWindow();

        if (e.Args.Length > 0)
        {
            mainWindow.CommandRun(e.Args);
        }
    }
}
