using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Diagnostics;

namespace ToolBox.Core.ContextAware;

/// <summary>应用程序类型</summary>
public enum AppType
{
    Unknown,
    Browser,        // 浏览器
    Ide,            // IDE (VS Code, Visual Studio, etc.)
    Editor,         // 文本编辑器
    Office,         // Office (Word, Excel, etc.)
    Terminal,       // 终端
    Email,          // 邮件客户端
    Chat,           // 聊天工具
    Media,          // 媒体播放器
    FileManager,    // 文件管理器
    Design          // 设计工具 (Figma, etc.)
}

/// <summary>上下文推荐操作</summary>
public class ContextualAction
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Description { get; set; } = "";
    public Action<string?> Execute { get; set; } = _ => { };
}

/// <summary>上下文感知服务 (P2-02)</summary>
public class ContextAwareService
{
    public static ContextAwareService Instance { get; } = new();

    private ContextAwareService() { }

    /// <summary>检测当前前台窗口的应用程序类型</summary>
    public AppType DetectForegroundApp()
    {
        try
        {
            var hwnd = ToolBox.Core.Native.NativeMethods.GetForegroundWindow();
            ToolBox.Core.Native.NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName.ToLowerInvariant();

            return name switch
            {
                "chrome" or "firefox" or "msedge" or "brave" or "opera" => AppType.Browser,
                "code" or "devenv" or "rider" or "webstorm" or "idea" => AppType.Ide,
                "winword" or "excel" or "powerpnt" or "outlook" => AppType.Office,
                "terminal" or "cmd" or "powershell" or "wt" or "bash" or "zsh" => AppType.Terminal,
                "explorer" or "totalcmd" or "freecommander" => AppType.FileManager,
                "notepad" or "sublime" or "notepad++" => AppType.Editor,
                "slack" or "telegram" or "wechat" or "whatsapp" or "discord" => AppType.Chat,
                "figma" or "photoshop" or "illustrator" => AppType.Design,
                "vlc" or "wmplayer" or "musicbee" => AppType.Media,
                _ => AppType.Unknown
            };
        }
        catch
        {
            return AppType.Unknown;
        }
    }

    /// <summary>根据应用程序类型获取推荐操作</summary>
    public List<ContextualAction> GetRecommendedActions(AppType appType, string? selectedText)
    {
        var actions = new List<ContextualAction>();

        if (appType == AppType.Ide && !string.IsNullOrEmpty(selectedText))
        {
            actions.Add(new ContextualAction
            {
                Id = "ai_analyze",
                Label = "发送到 AI",
                Icon = "🤖",
                Description = "将代码发送到 AI 分析"
            });
            actions.Add(new ContextualAction
            {
                Id = "save_snippet",
                Label = "保存为片段",
                Icon = "💾",
                Description = "保存为代码片段"
            });
            actions.Add(new ContextualAction
            {
                Id = "find_refs",
                Label = "查找引用",
                Icon = "🔍",
                Description = "查找代码引用"
            });
        }
        else if (appType == AppType.Browser)
        {
            actions.Add(new ContextualAction
            {
                Id = "save_link",
                Label = "保存链接",
                Icon = "🔗",
                Description = "保存当前页面链接"
            });
            actions.Add(new ContextualAction
            {
                Id = "screenshot_page",
                Label = "截取页面",
                Icon = "📸",
                Description = "长截取当前页面"
            });
            actions.Add(new ContextualAction
            {
                Id = "ai_summarize",
                Label = "AI 摘要",
                Icon = "📝",
                Description = "总结页面内容"
            });
        }
        else if (appType == AppType.Office)
        {
            actions.Add(new ContextualAction
            {
                Id = "transform_text",
                Label = "转换文本",
                Icon = "🔄",
                Description = "格式化选中文本"
            });
            actions.Add(new ContextualAction
            {
                Id = "copy_clean",
                Label = "纯文本复制",
                Icon = "📋",
                Description = "清除格式后复制"
            });
        }

        return actions;
    }

    /// <summary>获取当前上下文推荐操作</summary>
    public List<ContextualAction> GetCurrentRecommendation(string? selectedText)
    {
        return GetRecommendedActions(DetectForegroundApp(), selectedText);
    }
}
