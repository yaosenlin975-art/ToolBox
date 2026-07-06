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

        // 启动画面（主题加载后才能找到 DynamicResource）
        var splash = new Views.SplashWindow();
        if (options.Data.ShowSplashWindow)
        {
            splash.ShowSplash();
            splash.SetStatus(Views.SplashWindow.SplashStatus.Loading, "正在加载...");
        }

        // 初始化定时调度（自动截图 + 每日总结）
        Core.Scheduling.ScheduleManager.ApplyCurrent();

        // 接线活动记录：前台窗口切换时记录到 ScreenshotTracker
        Core.Windows.WindowManager.Instance.WindowActived += (_, info) =>
            Core.Scheduling.ScreenshotTracker.Instance.RecordWindowSwitch(info.ClassName, info.TitleName);


        // 启动完成，关闭启动画面
        if (options.Data.ShowSplashWindow)
        {
            splash.SetStatus(Views.SplashWindow.SplashStatus.Complete, "加载完成");
            splash.AutoCloseAfter(1);
        }
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
