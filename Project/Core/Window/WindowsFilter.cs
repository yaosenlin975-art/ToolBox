namespace ToolBox.Core.Windows;

public interface IWindowFilter
{
    bool IsFilter(WindowInfo windowInfo);
}

public class WindowsFilter : IWindowFilter
{
    private struct FilterInfo
    {
        public string TitleName;
        public string ClassName;

        public FilterInfo(string className)
        {
            TitleName = null;
            ClassName = className;
        }

        public FilterInfo(string titleName, string className)
        {
            TitleName = titleName;
            ClassName = className;
        }
    }

    private static readonly FilterInfo[] filterInfos = new FilterInfo[]
    {
        new FilterInfo("TXGuiFoundation"),
        new FilterInfo("TXMenuWindow", "TXGuiFoundation"),
        new FilterInfo("ScreenShotWnd"),
        new FilterInfo("SnapshotWnd"),
        new FilterInfo("CToolBarWnd"),
    };

    public bool IsFilter(WindowInfo windowInfo)
    {
        var titleName = windowInfo.TitleName;
        var className = windowInfo.ClassName;

        foreach (var item in filterInfos)
        {
            bool? flag1 = item.TitleName != null ? item.TitleName == titleName : null;
            bool? flag2 = item.ClassName != null ? item.ClassName == className : null;

            if (flag1.HasValue && flag2.HasValue)
            {
                if (flag1.Value && flag2.Value) return true;
            }

            var result = flag1 ?? (flag2 ?? false);
            if (result) return true;
        }
        return false;
    }
}
