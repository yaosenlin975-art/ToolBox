# ToolBox 修复回归测试报告

> 测试工程师：严过关
> 测试日期：2026-07-14
> 测试范围：13 项修复（WPF 8 项 + LLM 5 项 + 1 项附加编译修复）
> 测试方法：编译验证 + 代码逻辑审查 + 边界条件分析

---

## 一、编译结果

| 指标 | 数值 |
|------|------|
| 编译状态 | **❌ 失败** |
| 错误数 | **2** |
| 警告数 | ~100（均为预存 nullable/event null 警告） |
| 新增错误 | **2**（Agent.cs:77,83） |
| 新增警告 | 0 |

### 错误详情

| # | 文件 | 行号 | 错误码 | 描述 |
|---|------|------|--------|------|
| 1 | `Core/Llm/Agent.cs` | 77 | CS1626 | 无法在包含 catch 子句的 Try 块体中生成值 (`yield return chunk;`) |
| 2 | `Core/Llm/Agent.cs` | 83 | CS1631 | 无法在 catch 子句体中生成值 (`yield return new ChatChunk { Error = ... };`) |

### 错误根因分析

**错误来源：LLM 修复 #5（Agent 重试机制）。**

重试逻辑将原来的 `await foreach` 流式响应包装在 `try/catch` 中。C# 语言限制：`yield return` 不能出现在 `try` 块（当有 `catch` 子句时），也不能出现在 `catch` 块中。

```csharp
// Agent.cs:61-85 — 问题代码结构
for (int retry = 0; retry < 3 && !llmSuccess; retry++)
{
    try
    {
        await foreach (var chunk in provider.ChatAsync(...))
        {
            yield return chunk;  // ❌ CS1626: yield 在 try/catch 中
        }
        llmSuccess = true;
    }
    catch (Exception ex) when (retry < 2 && !cts.Token.IsCancellationRequested)
    {
        yield return new ChatChunk { Error = ... };  // ❌ CS1631: yield 在 catch 中
        await Task.Delay(1000, cts.Token);
    }
}
```

**修复建议**：将流式响应收集到 `List<ChatChunk>` 缓冲区，`try/catch` 结束后再批量 `yield return`。或者使用 `Channel<T>` / `IAsyncEnumerable` 辅助模式绕过限制。

> ⚠️ 注意：WPF 修复日志中标记为"预存错误 (Agent.cs:77,83 — yield return in try/catch，非本次修改范围)"，但 LLM 修复 #5 修改了同一区域（行 58-92），且 LLM 修复日志声称"验证：编译通过"。两处日志矛盾。经代码分析，**当前编译错误确由 LLM 修复 #5 引入**——重试机制把 `yield return` 包裹进了 `try/catch`。

---

## 二、逐项测试结果

### 2.1 WPF 修复（8 项）

| # | 修复项 | 结果 | 说明 | 风险 |
|---|--------|------|------|------|
| 1 | **FileAccessWhitelist 接入** | ✅ 通过 | `read_file/write_file/delete_file/copy_file/move_file/create_directory` 共 6 个方法均添加 `IsAllowed()` 校验；`delete_file` 和 `move_file` 额外调用 `ConfirmDialog.Show()`；`copy_file` 对源/目标分别校验；`using ToolBox.Core.Security` 正确引用 | 🟢 低 |
| 2 | **DefaultSystemPromptBuilder 提示词修正** | ✅ 通过 | OCR 声称、白名单声称、"delete 需确认"声称全部删除；新增工具列表与实际 `FileTools/TodoTools/WebSearchTools` 一致；后续被 LLM 修复 #3 覆盖为当前版本（含 `BuildWithMemory`） | 🟢 低 |
| 3 | **GC.Collect 删除** | ✅ 通过 | `ScrapWindow.CloseScrap()`（行 156-161）仅保留 `closePrepare = true; Close(); return this;`，无 `GC.Collect()` 调用 | 🟢 低 |
| 4 | **WindowManager 条件触发 + ScreenshotTracker 去重** | ✅ 通过 | `WindowManager.Update()`: 新增 `lastFiredForegroundHandle`/`lastFiredTopMostHandle`，仅在句柄变化时触发事件；`WindowInfo` 为 struct，初始 `Handle = IntPtr.Zero`，首次调用安全；`ScreenshotTracker.RecordWindowSwitch`：行 32 `if (appName == lastApp) return;` 正确去重 | 🟢 低 |
| 5 | **MessageBubble 流式渲染** | ✅ 通过 | `_needsRender` 标志正确设置/重置；`AppendContent` 仅追加文本不调 RenderMarkdown；`SetStreaming(false)` 仅当 `_needsRender==true` 时调用一次；`cachedChatFontSize` 缓存字号（初始 -1，首次渲染时加载一次）；`SetMessage` 重置 `_needsRender=false` | 🟢 低 |
| 6 | **ScrapBook 事件转发** | ✅ 通过 | 5 个方法正确转发到 `CacheManager.Instance`：`ScrapAdded`/`ScrapLocationChanged`/`ScrapImageChanged`/`ScrapStyleApplied`/`ScrapStyleRemoved`；`OnScrapActive`/`OnScrapInactive` 保持空（CacheManager 无对应接口，符合设计） | 🟢 低 |
| 7 | **托盘菜单映射** | ✅ 通过 | `CompactToolboxWindow.SwitchToTab`: 所有新旧 ID 均支持（`todos`/`todo`, `assistant`/`chat`, `screenshots`/`screenshot`/`history`），新增 `settings` case 调用 `OpenWorkbench()`；size 查表和 opacity 判断同步支持；`MainWindow`: `ShowHistory→screenshots`, `ShowChatWindow→assistant`, `ShowTodoWindow→todos` | 🟢 低 |
| 8 | **数据目录 TODO** | ✅ 通过 | `SetunaOption.cs:48` 和 `CacheManager.cs:14` 均添加 TODO 注释，行为未变，兼容旧版数据 | 🟢 低 |

### 2.2 LLM 修复（5 项）

| # | 修复项 | 结果 | 说明 | 风险 |
|---|--------|------|------|------|
| 1 | **ContextCompressor 接入** | ✅ 通过 | Agent 构造函数中初始化 `new ContextCompressor()`（行 26）；`RunAsync` 工具调用循环每轮开始时调用 `CheckAndCompressAsync(messages)`（行 54）；三级压缩逻辑（snip/aggressive/summary）正确分档 | 🟡 中 |
| 2 | **MemoryStore 读写链路** | ✅ 通过 | `BuildMessages()` 调用 `MemoryStore.Instance.GetRelevant(session.Id)`（行 151）；`BuildWithMemory(memories)` 注入记忆到系统提示词（行 154）；`finally` 块中 `runCount % 20 == 0` 时调 `Cleanup()`（行 142-145） | 🟡 中 |
| 3 | **DefaultSystemPromptBuilder 提示词修正** | ✅ 通过 | 删除 OCR/白名单/确认弹窗虚假声称；工具列表与 `FileTools`/`TodoTools`/`WebSearchTools` 实际注册完全一致；`BuildWithMemory` 实现正确——空记忆返回基础提示词，非空则追加记忆段 | 🟢 低 |
| 4 | **AnthropicProvider 工具调用** | ✅ 通过 | `tools` 参数转换为 Anthropic 格式 `{name, description, input_schema}`（行 84-96）；`content_block_start` 检测 `tool_use`（行 137-146）；`content_block_delta` 处理 `text_delta`/`input_json_delta`（行 149-167）；`content_block_stop` 输出 `ToolCall` chunk（行 170-186）；`message_stop` 输出 `IsDone`（行 188-191）；`BuildAnthropicMessage` 正确处理 3 种消息格式；⚠️ CS8767 nullable 警告（tools 参数 nullability 与接口不匹配，无害） | 🟢 低 |
| 5 | **Agent 重试机制** | ❌ **失败** | 3 次重试循环逻辑正确（行 61）；异常捕获+重试延迟正确；但 **编译失败**：`yield return chunk;`（行 77）在 try/catch 中触发 CS1626；`yield return new ChatChunk { Error = ... };`（行 83）在 catch 中触发 CS1631；项目无法编译，**阻塞发布** | 🔴 高 |

### 2.3 附加编译修复

| # | 修复项 | 结果 | 说明 |
|---|--------|------|------|
| A1 | **XAML 双重 BOM** | ✅ 通过 | `CompactToolboxWindow.xaml` 和 `TodoView.xaml` 去除重复 BOM，XAML 编译错误消除 |

---

## 三、回归风险评估

### 🔴 高风险（阻塞发布）

| 风险项 | 描述 | 影响范围 |
|--------|------|----------|
| **Agent.cs 编译错误** | CS1626/CS1631 导致项目无法构建，所有功能不可用 | **全局** |

### 🟡 中风险（需关注）

| 风险项 | 描述 | 影响 |
|--------|------|------|
| **ContextCompressor 短对话误压缩** | 经分析，`tokenEstimator` 用 `text.Length * 0.25` 估算。短对话（~2000 tokens，上下文 131072）ratio ≈ 0.015，远低于 snip 阈值 0.6。**不会误触发**。但如果 `maxInputLength` 被设为较小值（如 4096），可能过早触发压缩。当前默认 131072 安全。 | 低频，取决于模型实际 context window |
| **MemoryStore Cleanup 频率不足** | `runCount` 是 Agent 实例字段，每次新对话创建新 Agent → runCount 从 0 开始。对于短会话用户，Cleanup 可能数周不触发。MemoryStore 数据库持续增长。 | 长期运行后 SQLite 文件膨胀 |
| **WindowManager 句柄为 IntPtr.Zero** | 当桌面处于前台时，`GetForegroundWindow()` 可能返回 `IntPtr.Zero`。`WindowInfo` 为 struct，`Handle = IntPtr.Zero`，与初始 `lastFiredForegroundHandle = IntPtr.Zero` 相同，不会触发事件——**正确行为**。切换到其他窗口后再切回桌面，lastFiredForegroundHandle 已变，事件正常触发。 | 无影响，语义正确 |

### 🟢 低风险（可接受）

| 风险项 | 描述 |
|--------|------|
| **FileAccessWhitelist 合法操作** | `IsAllowed(path)` 由 `FileAccessWhitelist` 内部逻辑决定。白名单非空时，合法路径正常通过；空白名单时见边界条件分析。 |
| **流式渲染延迟** | `_needsRender` 延迟到 `SetStreaming(false)` 时渲染。流式期间用户看到的是原始文本（FlowDocument 追加无格式），流式结束后一次渲染为 Markdown。**逐 token 可见性保持**（通过 `ContentDocument` 默认文本追加），仅 Markdown 语法高亮/格式化延迟到流式结束。 |
| **ScrapBook 事件转发 NullReference** | `CacheManager.Instance` 通过静态属性访问，首次访问时初始化。只要 `CacheManager` 构造函数无异常，转发安全。 |
| **托盘 settings→OpenWorkbench** | `SwitchToTab("settings")` 调用 `OpenWorkbench()` 但未传 tab 参数——可能打开默认页而非设置页。需检查 `OpenWorkbench()` 无参重载行为。 |
| **AnthropicProvider 流边界** | 行 194-195：流结束时若未收到 `message_stop`，手动发送 `IsDone` 作为兜底——防御性编程，正确。 |

---

## 四、边界条件分析

### 4.1 FileAccessWhitelist 空集合

```csharp
// 白名单为空时 IsAllowed 的行为
```

| 场景 | 预期 | 代码行为 | 结论 |
|------|------|----------|------|
| 白名单为空，调用 `read_file` | 应阻止所有文件操作 | 取决于 `FileAccessWhitelist.IsAllowed()` 实现——**未在本次修改范围内审查** | ⚠️ 需确认 |
| 白名单为空，调用 `write_file` | 应阻止 | 同上 | ⚠️ 需确认 |

> **建议**：`FileAccessWhitelist.IsAllowed()` 应在白名单为空时返回 `false`（默认拒绝策略），确保安全。

### 4.2 WindowManager 初始状态

- `WindowInfo` 是 **struct**（`Core/Window/WindowInfo.cs:5`），默认 `Handle = IntPtr.Zero`
- `lastFiredForegroundHandle` 初始 `IntPtr.Zero`
- 首次 `Update()`: `foregroundWindow.Handle (IntPtr.Zero) != hwnd (非零)` → 更新 foregroundWindow → `foregroundWindow.Handle != lastFiredForegroundHandle (Z == Z)` → **不触发事件**
- **结论**：安全，首次调用不会误触发事件。后续仅在窗口真正切换时触发。

### 4.3 工具调用结果截断

```csharp
// Agent.cs:171
toolCall.Result = result?.Length > 3000 ? result[..3000] + "...[截断]" : result;
```

- 截断后返回给 LLM 的是 3000 字符 + `...[截断]`
- `MemoryStore.Save` 使用截断后的 `toolCall.Result`
- **结论**：截断逻辑正确，MemoryStore 和 LLM 上下文一致。

### 4.4 重试期间用户取消

```csharp
// Agent.cs:81
catch (Exception ex) when (retry < 2 && !cts.Token.IsCancellationRequested)
```

- 用户取消时 `cts.Token.IsCancellationRequested == true` → `when` 条件为 false → 异常不被捕获 → 向上传播
- `Task.Delay(1000, cts.Token)` 在取消时抛出 `OperationCanceledException`
- **结论**：取消逻辑正确，取消时不会触发重试。但如果开发者修复编译错误时需要保留此行为。

### 4.5 ContextCompressor token 估算精度

```csharp
// ContextCompressor.cs:101
return (int)(text.Length * 0.25);
```

- 中英文混合文本，粗略估算。对于中文（1 字符 ≈ 1-2 tokens），低估约 2-4x；对于英文（1 字符 ≈ 0.25 tokens），较准确。
- 可能的风险：中文长对话时实际 token 数远超估算值，压缩触发时机偏晚。
- **结论**：🟡 低风险，`maxInputLength` 默认 131072 足够大，即使低估也不易超限。

---

## 五、发现的新问题

### 问题 1：🔴 Agent.cs 编译错误 — 阻塞发布

| 属性 | 详情 |
|------|------|
| **严重度** | 🔴 P0 — 阻塞 |
| **文件** | `Core/Llm/Agent.cs:77,83` |
| **类型** | 编译错误 CS1626 / CS1631 |
| **引入** | LLM 修复 #5（Agent 重试机制） |
| **根因** | `yield return` 在 `try/catch` 块内及 `catch` 块中 |
| **修复方案** | 收集 chunk 到 `List<ChatChunk>` 缓冲区，try/catch 结束后 `foreach yield return` |

### 问题 2：🟡 ContextCompressor.ApplySummaryCompression 无意义的 async

| 属性 | 详情 |
|------|------|
| **严重度** | 🟡 P2 — 代码质量 |
| **文件** | `Core/Llm/ContextCompressor.cs:79` |
| **问题** | `ApplySummaryCompression` 标记为 `async Task` 但方法体内无 `await`，编译器会生成不必要的状态机 |
| **修复方案** | 移除 `async` 关键字，返回 `Task.CompletedTask`；或将调用处的 `await` 改为同步调用 |

### 问题 3：🟡 Agent.runCount 生命周期短导致 Cleanup 频率低

| 属性 | 详情 |
|------|------|
| **严重度** | 🟡 P2 — 功能衰减 |
| **文件** | `Core/Llm/Agent.cs:18,141-145` |
| **问题** | `runCount` 是实例字段，每次 `new Agent()` 从 0 开始。短会话用户可能数周不触发 Cleanup |
| **修复方案** | 改为 `MemoryStore.Instance` 上的静态计数器，或基于时间（如每天首次运行时清理） |

### 问题 4：🟢 托盘 settings 映射可能打开默认页

| 属性 | 详情 |
|------|------|
| **严重度** | 🟢 P3 — 体验 |
| **文件** | `Views/CompactToolboxWindow.xaml.cs:125-127` |
| **问题** | `case "settings": OpenWorkbench();` — 无参调用可能打开默认 tab 而非 settings 页 |
| **修复方案** | 改为 `OpenWorkbench("settings");`（需确认 `OpenWorkbench` 是否接受 "settings" 参数） |

---

## 六、测试统计

| 类别 | 数量 |
|------|------|
| 总修复项 | 14（WPF 8 + LLM 5 + 附加 1） |
| ✅ 通过 | 13 |
| ❌ 失败 | 1（Agent 重试机制 — 编译错误） |
| 🔴 高风险项 | 1 |
| 🟡 中风险项 | 2 |
| 🟢 低风险项 | 5 |
| 发现新问题 | 4（1 P0 + 2 P2 + 1 P3） |

---

## 七、最终结论

### 🔴 阻塞发布 — 需修复后重新验证

**阻塞原因**：`Core/Llm/Agent.cs` 的编译错误 CS1626/CS1631 导致项目无法构建。该错误由 LLM 修复 #5（Agent 重试机制）引入。

**通过项（13/14）**：其余 13 项修复代码逻辑正确，边界条件分析通过，回归风险可控。

**待修复**：

| 优先级 | 修复项 | 工作量 |
|--------|--------|--------|
| P0 | Agent.cs 编译错误 — 重构 yield return 绕过 try/catch 限制 | ~30 分钟 |
| P2 | ContextCompressor 移除无意义 async | ~5 分钟 |
| P2 | Cleanup 频率改为基于时间或静态计数器 | ~15 分钟 |
| P3 | settings 映射传参 | ~5 分钟 |

**修复后验证步骤**：
1. `dotnet build` 确认 0 错误
2. 如 Agent.cs 重构了流式响应缓冲逻辑，需验证流式输出仍为逐 chunk 延迟（非批量）
3. 人工验证：启动 → 发起 LLM 对话 → 构造 API 失败场景观察重试提示 → 取消对话

---

*报告结束。*
