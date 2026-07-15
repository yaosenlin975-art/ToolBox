using System.IO;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.ActionChain.Nodes;

public class SaveToFileNode : IActionNode
{
    public string NodeName => "保存到文件";
    public string NodeType => "SaveToFile";
    public string NodeIcon => "💾";

    public Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        if (context.Screenshot == null)
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = "无截图数据" });
        try
        {
            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ToolBox");
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(saveDir, fileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(context.Screenshot));
            using var fs = new FileStream(filePath, FileMode.Create);
            encoder.Save(fs);
            return Task.FromResult(new ActionNodeResult { IsSuccess = true, Output = filePath, Metadata = new() { ["filePath"] = filePath } });
        }
        catch (Exception ex) { return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = ex.Message }); }
    }
}
