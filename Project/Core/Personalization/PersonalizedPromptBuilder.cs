using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace ToolBox.Core.Personalization;

/// <summary>AI 个性化提示词构建器 (P2-03)</summary>
public class PersonalizedPromptBuilder
{
    public static PersonalizedPromptBuilder Instance { get; } = new();

    private PersonalizedPromptBuilder() { }

    /// <summary>构建注入近期活动摘要的个性化系统提示词</summary>
    public string BuildPersonalizedSystemPrompt()
    {
        var summary = GetRecentActivitySummary();
        if (string.IsNullOrEmpty(summary))
            return "";

        return $"## 用户近期活动摘要\n{summary}\n\n请基于以上信息提供更贴心的回答。";
    }

    /// <summary>获取近期活动摘要</summary>
    public string GetRecentActivitySummary()
    {
        // Placeholder - real implementation will query TodoStore, ScreenshotTagStore, etc.
        return "";
    }
}
