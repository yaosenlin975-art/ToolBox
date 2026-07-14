// ColorTools.cs - 取色器 LLM Tool 暴露
// 职责:让 AI 助手能调用 pick_color 获取屏幕任意坐标色值,并查询历史
// 注册位置:ChatView.xaml.cs 构造函数 toolRegistry.Register(typeof(ColorTools))
using System.Linq;

using ToolBox.Core.Tools;

namespace ToolBox.Core.ColorPicker;

public static class ColorTools
{
    /// <summary>获取指定屏幕坐标(物理像素)的色值,同时记录到历史</summary>
    [Tool("pick_color", "获取指定屏幕坐标的颜色值,坐标使用屏幕物理像素(左上为原点)")]
    public static string PickColor(
        [ToolParam("屏幕 X 坐标(物理像素)")] int x,
        [ToolParam("屏幕 Y 坐标(物理像素)")] int y,
        [ToolParam("输出格式: hex/rgb/hsl")] string format = "hex")
    {
        var info = ColorPickerService.Instance.PickColor(x, y);
        ColorHistoryStore.Instance.Add(info);
        return format.ToLowerInvariant() switch
        {
            "rgb" => info.Rgb,
            "hsl" => info.Hsl,
            _ => info.Hex
        };
    }

    /// <summary>查询最近取色历史</summary>
    [Tool("list_colors", "查询最近的取色历史记录")]
    public static string ListColors(
        [ToolParam("返回数量,默认10")] int count = 10)
    {
        var recent = ColorHistoryStore.Instance.GetRecent(count);
        if (recent.Count == 0) return "暂无取色历史";
        return string.Join("\n", recent.Select((c, i) =>
            $"{i + 1}. {c.Hex} | {c.Rgb} | {c.Hsl}"));
    }
}
