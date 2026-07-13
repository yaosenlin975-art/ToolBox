using System.IO;
using System.Text;
using ToolBox.Core.Security;

namespace ToolBox.Core.Tools;

public static class FileTools
{
    [Tool("read_file", "读取文件内容（支持文本/自动检测编码）")]
    public static string ReadFile(
        [ToolParam("文件路径")] string path)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(path))
            return "安全限制: 路径不在白名单内: " + path;
        if (!File.Exists(path)) return "文件不存在: " + path;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var encoding = DetectEncoding(bytes);
            return encoding.GetString(bytes);
        }
        catch (Exception ex) { return "读取失败: " + ex.Message; }
    }

    [Tool("write_file", "写入或覆盖文件内容")]
    public static string WriteFile(
        [ToolParam("文件路径")] string path,
        [ToolParam("文件内容")] string content)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(path))
            return "安全限制: 路径不在白名单内: " + path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
            return "已写入: " + path;
        }
        catch (Exception ex) { return "写入失败: " + ex.Message; }
    }

    [Tool("file_exists", "检查文件或目录是否存在")]
    public static string FileExists(
        [ToolParam("文件或目录路径")] string path)
    {
        return File.Exists(path) || Directory.Exists(path) ? "true" : "false";
    }

    [Tool("list_directory", "列出目录内容")]
    public static string ListDirectory(
        [ToolParam("目录路径")] string path)
    {
        if (!Directory.Exists(path)) return "目录不存在: " + path;
        var entries = Directory.GetFileSystemEntries(path);
        return string.Join("\n", entries.Select(e => Path.GetFileName(e)));
    }

    [Tool("create_directory", "创建目录（含中间目录）")]
    public static string CreateDirectory(
        [ToolParam("目录路径")] string path)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(path))
            return "安全限制: 路径不在白名单内: " + path;
        try
        {
            Directory.CreateDirectory(path);
            return "已创建: " + path;
        }
        catch (Exception ex) { return "创建失败: " + ex.Message; }
    }

    [Tool("delete_file", "删除文件或空目录")]
    public static string DeleteFile(
        [ToolParam("文件或目录路径")] string path)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(path))
            return "安全限制: 路径不在白名单内: " + path;
        if (!ConfirmDialog.Show(path, "delete"))
            return "用户取消删除操作: " + path;
        try
        {
            if (File.Exists(path)) { File.Delete(path); return "已删除文件: " + path; }
            if (Directory.Exists(path)) { Directory.Delete(path, false); return "已删除目录: " + path; }
            return "路径不存在: " + path;
        }
        catch (Exception ex) { return "删除失败: " + ex.Message; }
    }

    [Tool("copy_file", "复制文件")]
    public static string CopyFile(
        [ToolParam("源文件路径")] string source,
        [ToolParam("目标文件路径")] string destination)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(source))
            return "安全限制: 源路径不在白名单内: " + source;
        if (!FileAccessWhitelist.Instance.IsAllowed(destination))
            return "安全限制: 目标路径不在白名单内: " + destination;
        try
        {
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(source, destination, true);
            return "已复制: " + source + " -> " + destination;
        }
        catch (Exception ex) { return "复制失败: " + ex.Message; }
    }

    [Tool("move_file", "移动或重命名文件")]
    public static string MoveFile(
        [ToolParam("源文件路径")] string source,
        [ToolParam("目标文件路径")] string destination)
    {
        if (!FileAccessWhitelist.Instance.IsAllowed(source))
            return "安全限制: 源路径不在白名单内: " + source;
        if (!FileAccessWhitelist.Instance.IsAllowed(destination))
            return "安全限制: 目标路径不在白名单内: " + destination;
        if (!ConfirmDialog.Show(source, "move", destination))
            return "用户取消移动操作: " + source;
        try
        {
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(source, destination, true);
            return "已移动: " + source + " -> " + destination;
        }
        catch (Exception ex) { return "移动失败: " + ex.Message; }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        return Encoding.UTF8;
    }
}
