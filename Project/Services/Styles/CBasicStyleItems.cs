using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Models;

public class CCloseStyleItem : IStyleItem
{
    public string GetName() => "Close";
    public string GetDisplayName() => "关闭";
    public string GetDescription() => "关闭并移除贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => true;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.CloseScrap();

    public object Clone() => MemberwiseClone();
}

public class CPasteStyleItem : IStyleItem
{
    public string GetName() => "Paste";
    public string GetDisplayName() => "粘贴";
    public string GetDescription() => "从剪贴板粘贴新贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => true;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image != null)
                scrap.Manager?.AddScrap(image,
                    (int)scrap.Left + 20,
                    (int)scrap.Top + 20,
                    image.PixelWidth,
                    image.PixelHeight);
        }
    }

    public object Clone() => MemberwiseClone();
}

public class CDustBoxStyleItem : IStyleItem
{
    public string GetName() => "DustBox";
    public string GetDisplayName() => "回收";
    public string GetDescription() => "将贴图移入回收箱";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Manager?.RemoveScrap(scrap);

    public object Clone() => MemberwiseClone();
}

public class CDustScrapStyleItem : IStyleItem
{
    public string GetName() => "DustScrap";
    public string GetDisplayName() => "从回收箱恢复";
    public string GetDescription() => "从回收箱恢复最近的贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Manager?.RestoreFromDustBox();

    public object Clone() => MemberwiseClone();
}

public class CDustEraseStyleItem : IStyleItem
{
    public string GetName() => "DustErase";
    public string GetDisplayName() => "清空回收箱";
    public string GetDescription() => "清空回收箱中的所有贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Manager?.EraseDustBox();

    public object Clone() => MemberwiseClone();
}

public class CAllHideStyleItem : IStyleItem
{
    public string GetName() => "AllHide";
    public string GetDisplayName() => "隐藏所有";
    public string GetDescription() => "隐藏所有贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Manager?.HideAllScrap();

    public object Clone() => MemberwiseClone();
}

public class CAllShowStyleItem : IStyleItem
{
    public string GetName() => "AllShow";
    public string GetDisplayName() => "显示所有";
    public string GetDescription() => "显示所有贴图";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Manager?.ShowAllScrap();

    public object Clone() => MemberwiseClone();
}

public class COptionStyleItem : IStyleItem
{
    public string GetName() => "Option";
    public string GetDisplayName() => "选项";
    public string GetDescription() => "打开选项设置";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        scrap.Manager?.BindForm?.ShowOptions();
    }

    public object Clone() => MemberwiseClone();
}

public class CShowVersionStyleItem : IStyleItem
{
    public string GetName() => "ShowVersion";
    public string GetDisplayName() => "版本信息";
    public string GetDescription() => "显示版本信息";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        MessageBox.Show(
            $"ToolBox v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}",
            "版本信息",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public object Clone() => MemberwiseClone();
}

public class CShutDownStyleItem : IStyleItem
{
    public string GetName() => "ShutDown";
    public string GetDisplayName() => "退出";
    public string GetDescription() => "退出程序";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        scrap.Manager?.BindForm?.Shutdown();
    }

    public object Clone() => MemberwiseClone();
}

public class CSeparatorStyleItem : IStyleItem
{
    public string GetName() => "Separator";
    public string GetDisplayName() => "---";
    public string GetDescription() => "";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint) { }
    public object Clone() => MemberwiseClone();
}
