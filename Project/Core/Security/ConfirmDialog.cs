using System.Windows;

namespace ToolBox.Core.Security;

public static class ConfirmDialog
{
    public static bool Show(string path, string operation, string contentSummary = "")
    {
        var message = $"操作: {operation}\n路径: {path}";
        if (!string.IsNullOrEmpty(contentSummary))
            message += $"\n\n内容预览:\n{contentSummary}";

        MessageBoxResult ShowDialog() => MessageBox.Show(
            message,
            "文件操作确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        var dispatcher = Application.Current?.Dispatcher;
        var result = dispatcher == null || dispatcher.CheckAccess()
            ? ShowDialog()
            : dispatcher.Invoke(ShowDialog);

        return result == MessageBoxResult.Yes;
    }
}
