[@QA 测试工程师](mention://agent/6b82caf1-818d-4a84-b54d-2af47dccbb16) 请完成 CSH-5 验收（自动巡检发现：你的验收任务 08-05 01:46 中断在验证过程中，未产出结论，重发）。

**当前状态**：issue 置 in_review（01:13 交接），等待验收结论。

**改动摘要**（前端工程师评论 2026-08-04 10:18，提交 `804f96f`）：
1. MarkdownDocument.cs：5 个硬编码 Brush 改为 `ResolveBrush()` 从主题资源动态获取（TextPrimaryBrush/TextSecondaryBrush/TextTertiaryBrush/CodeKwBrush/CodeBgBrush），保留 fallback
2. MessageBubble.xaml.cs：`cachedChatFontSize` per-instance 缓存已存在，`ToolBoxOption.Load()` 仅首次读盘（审查确认无需修改）
3. TodoStore.cs + TodoTools.cs：6 个 `[Tool]` 方法改 async，移除 9 处 `Task.Run().GetAwaiter().GetResult()` sync 包装器，周期补算 Timer 改 async lambda
4. ScheduleManager.cs：`CaptureFullScreen` 改用 `VirtualScreenWidth/Height` + `VirtualScreenLeft/Top`，支持多显示器负坐标

**验收标准**（见 issue 描述）：
- [ ] 暗色主题下 Markdown 渲染（标题/正文/代码/引用）颜色清晰可读，Light 无回归
- [ ] 流式输出期间无重复磁盘 IO（ChatFontSize 缓存生效）
- [ ] TodoStore 无 `GetAwaiter().GetResult()` 残留
- [ ] 多显示器环境下定时截图覆盖全部屏幕
- [ ] 双主题下各功能无回归

通过置 done，未通过列出缺陷清单回退 @前端工程师 修复。
