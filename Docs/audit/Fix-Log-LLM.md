# LLM 修复日志

> 修复人：AI/LLM 专家 智言溪
> 日期：2026-07-14
> 范围：Core/Llm/ Agent.cs, ContextCompressor.cs, DefaultSystemPromptBuilder.cs, ISystemPromptBuilder.cs；Core/Providers/ AnthropicProvider.cs；Core/Memory/ MemoryStore.cs

---

## 修复1：ContextCompressor 接入 Agent 管线（死代码复活）

- **文件**：`Core/Llm/Agent.cs:15,26,54`
- **问题**：`ContextCompressor`（snip/aggressive/summary 三级压缩）已完整实现但 Agent 从未调用。长对话不会自动压缩，依赖 LLM 供应商 token 超限报错。对应 Architecture-Audit #4（上下文管理）、design.md 偏离 #10。
- **修改**：
  1. 新增 `contextCompressor` 字段，构造函数中初始化 `new ContextCompressor()`
  2. 在工具调用循环每轮开始时（发送 LLM 请求前）调用 `await contextCompressor.CheckAndCompressAsync(messages)` 检查并压缩上下文
- **验证**：编译通过（2 个 XAML 预存错误不影响 C# 编译）

---

## 修复2：MemoryStore 读取链路接入（死代码复活）

- **文件**：`Core/Llm/Agent.cs:149-158, 141-144`
- **问题**：`MemoryStore.Save` 在 `ExecuteToolAsync`（Agent.cs:173）中被调用，但 `GetRelevant` 从未被调用——记忆只写不读，数据库无限增长。`ISystemPromptBuilder.BuildWithMemory` 也从未被调用。对应 Architecture-Audit #5（记忆系统）、死代码 #10、#11。
- **修改**：
  1. `BuildMessages()` 中调用 `MemoryStore.Instance.GetRelevant(session.Id)` 获取相关记忆
  2. 将 `promptBuilder.Build()` 改为 `promptBuilder.BuildWithMemory(memories)`，注入记忆到系统提示词
  3. `ExecuteToolAsync`（Agent.cs:173）已有的 MemoryStore.Save 写入端保持不动，现在读写链路完整
  4. `finally` 块中新增定期清理逻辑：`runCount % 20 == 0` 时调用 `MemoryStore.Instance.Cleanup()`，静默忽略清理失败
- **验证**：编译通过

---

## 修复3：DefaultSystemPromptBuilder 提示词修正

- **文件**：`Core/Llm/DefaultSystemPromptBuilder.cs:7-21`
- **问题**：提示词声称有"OCR 识别图片文字"工具、"文件写入前确认路径在白名单内"、"delete_file 操作始终需要用户确认"——全部不实。对应 Architecture-Audit #1（安全层脱节）、#7（OCR 幻觉）。
- **修改**：
  1. 删除 OCR 声称——ToolRegistry 中无 OCR 工具
  2. 删除白名单校验声称——`FileAccessWhitelist` 虽有实现但未接入 FileTools
  3. 删除"delete_file 需要用户确认"——`ConfirmDialog` 虽有实现但未接入
  4. 新增具体工具名列表：Todo（add_todo / list_todos / complete_todo / delete_todo / update_todo）、文件（read_file / write_file / file_exists / list_directory / create_directory / delete_file / copy_file / move_file）、搜索（web_search）
  5. 新增实用规则：文件操作前用 file_exists 检查路径、工具失败时建议替代方案
  6. 优化输出格式：中文回复、代码块标注语言、结构化信息用表格
- **验证**：编译通过；提示词与 `FileTools`/`TodoTools`/`WebSearchTools` 实际工具完全一致

---

## 修复4：AnthropicProvider 实现工具调用

- **文件**：`Core/Providers/AnthropicProvider.cs:64-257`（全文重写 ChatAsync + BuildAnthropicMessage）
- **问题**：`ChatAsync` 不发送 `tools` 参数、不解析 `tool_use` 响应。Anthropic 模型无法触发任何工具调用。对应 Architecture-Audit #13。
- **修改**：
  1. **发送 tools 参数**：将 `ToolInfo` 列表转换为 Anthropic 工具格式 `{name, description, input_schema}`，input_schema 复用 `ToolInfo.ToJsonSchema()`
  2. **消息格式转换**（`BuildAnthropicMessage`）：
     - `role="tool"` → `role="user"` + content 为 `[{type: "tool_result", tool_use_id, content}]`
     - `role="assistant"` + `ToolCalls` → content 为 `[{type: "text", text}, {type: "tool_use", id, name, input}]`
     - 普通文本消息 → `{role, content}` 字符串
  3. **SSE 流解析**：
     - `content_block_start`（type="tool_use"）→ 记录 tool_use id/name
     - `content_block_delta`（type="text_delta"）→ yield Text chunk
     - `content_block_delta`（type="input_json_delta"）→ 累加 JSON 片段
     - `content_block_stop`（工具块结束）→ yield ToolCall chunk（含完整 arguments）
     - `message_stop` → yield IsDone=true chunk
  4. 删除旧内部类（AnthropicRequest/AnthropicMessage/AnthropicStreamChunk/AnthropicDelta），改用 `Dictionary<string, object>` 灵活构建 Anthropic 复杂 content 格式
- **验证**：编译通过；与 `ILlmProvider` 接口兼容，`Agent.RunAsync` 可正常接收 ToolCall chunk

---

## 修复5：Agent 重试机制

- **文件**：`Core/Llm/Agent.cs:58-92`
- **问题**：design.md:199 声称"每轮最多 3 次重试"，Agent.cs 有 10 轮循环但无重试逻辑。LLM API 调用失败时异常直接向上传播。对应 design.md 偏离 #9。
- **修改**：
  1. `ChatAsync` 调用外包一层 `for (int retry = 0; retry < 3 && !llmSuccess; retry++)` 循环
  2. `catch (Exception ex) when (retry < 2 && !cts.Token.IsCancellationRequested)` — 非最后一次重试且未取消时捕获异常
  3. 异常时 yield `ChatChunk { Error = "请求失败，正在重试 (N/3): ..." }` 通知 UI
  4. 重试前 `await Task.Delay(1000, cts.Token)` 等待 1 秒
  5. 3 次全部失败后 yield `ChatChunk { Error = "请求失败，已达最大重试次数（3次）" }` 并 `break` 退出工具调用循环
  6. 重试成功前 `currentText`/`lastChunk` 重置，避免脏数据
- **验证**：编译通过

---

## 修复汇总

| # | 标题 | 类型 | 文件 |
|---|------|------|------|
| 1 | ContextCompressor 接入 Agent 管线 | 死代码复活 | Agent.cs, ContextCompressor.cs |
| 2 | MemoryStore 读取链路接入 | 死代码复活 | Agent.cs, MemoryStore.cs |
| 3 | DefaultSystemPromptBuilder 提示词修正 | 安全/一致性 | DefaultSystemPromptBuilder.cs |
| 4 | AnthropicProvider 工具调用实现 | 功能补全 | AnthropicProvider.cs |
| 5 | Agent 重试机制 | 功能补全 | Agent.cs |

**总计**：5 项修复，涉及 4 个文件，编译通过（2 个 XAML 预存错误不受影响）。
