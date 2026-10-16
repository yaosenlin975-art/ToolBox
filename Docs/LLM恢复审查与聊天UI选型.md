# LLM 恢复文档审查与聊天 UI 选型

查阅日期：2026-10-16。审查对象：[恢复LLM助手与Agent运行时.md](恢复LLM助手与Agent运行时.md)。本次只调研、审查，不安装、移植或修改被审文档。

## 结论

**REJECT：当前实施计划存在权限、并发及接口阻断，修订后再实施。**

聊天界面推荐先验证 **assistant-ui + 本地静态前端 + WPF WebView2**，C# 保持 Provider、Agent、会话持久化与工具执行的唯一入口。需要接受 WebView2 依赖、前端构建和受限消息桥；现有计划“零新增 NuGet”须随路线决策调整。

必须保持纯 WPF 时，选择 OpenGptChat 的局部 Markdown/气泡作为移植候选；重做旧页的流式、滚动和工具状态。未找到经本机运行验证、可直接替换 ToolBox ChatView 的完整 WPF 控件。将 React 源码转译为 XAML 要重做布局、状态与生命周期，不能按机械语言转换估算。

## 当前工程与可观察验收

- 当前工程为 .NET 8 Windows/WPF，未引用 WebView2。旧 UI 在 `70c8c00^`；当前 HEAD 为 `75f7f5a`。本轮旧看板任务保持暂停，不沿用交接中的历史未完状态推断当前仓库。
- 现有 `ILlmProvider.ChatAsync` 为 `IAsyncEnumerable<ChatChunk>`，可继续作为 C# 流入口。已有 MarkdownDocument 是有限子集。
- UI 验收：流中可见正文与 Markdown；用户向上阅读时不抢滚动；停止可取消当前轮次；切换会话不串写；工具显示执行中/成功/失败/待确认；代码可复制、表格可读；中文输入法不误发送；中英和 Light/Dark 可即时切换。
- 性能试验：固定设备记录首次打开助手页时间、工作集与流式帧耗时；500 条消息及长代码块下检查滚动与输入。没有实测前不承诺“丝滑”或 <1 秒。

## GitHub 候选

固定版本取自上游 tag/commit 与 package manifest；不同项目版本不等于可互换 NuGet。许可仅报告本次上游代码类型，未审计整个依赖树或复制素材。

| 候选及固定版本 | 已读实现与匹配点 | 接入范围/成本 | 缺口与验证等级 |
| --- | --- | --- | --- |
| [assistant-ui](https://github.com/assistant-ui/assistant-ui/tree/e1fedc7673bb5071c04fb2b0478ccef80797d91d)，`@assistant-ui/react 0.15.29`，commit `e1fedc7`；MIT | external-store adapter 支持外部消息状态、onNew/onCancel、工具结果与会话回调；有自定义 store 示例，可保留 C# 后端 | React 18/19；新增本地前端构建、WebView2、消息桥、主题映射。集成成本中等，行为复用范围最大 | 不是 WPF 控件；Cloud 可选，不需要启用。根包包含 assistant-cloud 依赖，不能承诺打包内完全无该代码。API 含实验项，锁版本。读源码/官方文档，未构建运行 |
| [Ant Design X 2.7.0](https://github.com/ant-design/x/tree/9108580ab01add41fa11b8afea911eba5cc30d57)，commit `9108580`；MIT | Bubble.List 有滚动策略；Sender、Conversations、ThoughtChain 和 X Markdown 可拼聊天/工具执行界面 | React ≥18、antd ^6.1.1；同样需 WebView2 与桥接。只选 UI 组件，保留 C# runtime，不引入另一套 Provider | 工具确认、业务状态和会话仍需自己接线，组件设计感较统一但页面组装量更多。读源码/manifest/许可证，未构建运行 |
| [Lobe UI 5.47.1](https://github.com/lobehub/lobe-ui/tree/ece6ce4807c55f73a419ffba64e73410ac3b9714)，commit `ece6ce4`；MIT | ChatList/ChatItem 有编辑、动作、错误和 loading 展示；Markdown 与输入组件可搭配 | React 19、antd ^6.1.1、motion ^12；构建 Node ≥22.22.0。本地前端加桥接，依赖面较大 | 已读 ChatList 直接 data.map，不能据此宣称长会话已虚拟化。不等于移植整个 LobeChat；不作为首选。读局部实现，未运行 |
| [OpenGptChat 1.1.9.0 源码快照](https://github.com/SlimeNull/OpenGptChat/tree/0be1b382e8cf1b9c4546d58cfc2a9cfbc91864d4)，commit `0be1b38`；MIT | net8.0-windows/WPF；ChatPage、ChatBubble、MarkdownViewer、MarkdownWpfRenderer 可作为原生移植入口 | 选取控件/renderer并改主题与数据绑定；不整体带入 App、LiteDB、Hosting、Provider、托盘及热键。局部成本中等，完整应用成本高 | renderer 依赖 Markdig/ColorCode 与 App.GetService；Content 变化会重新解析并替换内容、吞异常，不证明流式性能。Agent 工具状态仍需新增。读源码/csproj/许可证，未构建运行 |

候选维护快照：assistant-ui 2026-10-16、Ant X 2026-10-12、Lobe UI 2026-10-15、OpenGptChat 2026-08-04 有推送。固定源码快照优先于 main 漂移；Lobe main 已为 6.0.0、Ant main manifest 为 2.9.0，本表使用各自稳定 tag，不混用 API。

### 源码证据与演示入口

- assistant-ui：[external-store 示例](https://github.com/assistant-ui/assistant-ui/blob/e1fedc7673bb5071c04fb2b0478ccef80797d91d/examples/with-external-store/app/MyRuntimeProvider.tsx)、[adapter 类型](https://github.com/assistant-ui/assistant-ui/blob/e1fedc7673bb5071c04fb2b0478ccef80797d91d/packages/core/src/runtimes/external-store/external-store-adapter.ts)、[包依赖](https://github.com/assistant-ui/assistant-ui/blob/e1fedc7673bb5071c04fb2b0478ccef80797d91d/packages/react/package.json)、[MIT](https://github.com/assistant-ui/assistant-ui/blob/e1fedc7673bb5071c04fb2b0478ccef80797d91d/LICENSE)、[官方站点](https://www.assistant-ui.com/)。
- Ant X：[BubbleList](https://github.com/ant-design/x/blob/9108580ab01add41fa11b8afea911eba5cc30d57/packages/x/components/bubble/BubbleList.tsx)、[依赖](https://github.com/ant-design/x/blob/9108580ab01add41fa11b8afea911eba5cc30d57/packages/x/package.json)、[MIT](https://github.com/ant-design/x/blob/9108580ab01add41fa11b8afea911eba5cc30d57/packages/x/LICENSE)、[X Markdown MIT](https://github.com/ant-design/x/blob/9108580ab01add41fa11b8afea911eba5cc30d57/packages/x-markdown/LICENSE)、[官网](https://x.ant.design/)。
- Lobe UI：[ChatList](https://github.com/lobehub/lobe-ui/blob/ece6ce4807c55f73a419ffba64e73410ac3b9714/src/chat/ChatList/ChatList.tsx)、[依赖](https://github.com/lobehub/lobe-ui/blob/ece6ce4807c55f73a419ffba64e73410ac3b9714/package.json)、[MIT](https://github.com/lobehub/lobe-ui/blob/ece6ce4807c55f73a419ffba64e73410ac3b9714/LICENSE)、[组件演示](https://ui.lobehub.com/)。
- OpenGptChat：[ChatPage](https://github.com/SlimeNull/OpenGptChat/blob/0be1b382e8cf1b9c4546d58cfc2a9cfbc91864d4/OpenGptChat/Views/Pages/ChatPage.xaml)、[MarkdownViewer](https://github.com/SlimeNull/OpenGptChat/blob/0be1b382e8cf1b9c4546d58cfc2a9cfbc91864d4/OpenGptChat/Controls/MarkdownViewer.cs)、[csproj](https://github.com/SlimeNull/OpenGptChat/blob/0be1b382e8cf1b9c4546d58cfc2a9cfbc91864d4/OpenGptChat/OpenGptChat.csproj)、[MIT](https://github.com/SlimeNull/OpenGptChat/blob/0be1b382e8cf1b9c4546d58cfc2a9cfbc91864d4/LICENSE.txt)。

## 最小集成试验（选型后执行）

1. Workbench assistant 页懒加载一个 WebView2，仅承载打包的 assistant-ui 静态资源；不新增 Next.js 服务、云端历史或另一套模型调用。
2. C# 持有真实 session/message/run/toolCall 状态；Web 只持有显示投影。桥接只支持明确的发送、停止、会话操作与确认动作，消息含 requestId/sessionId/runId，迟到事件按 run 归属处理。
3. 用假流测试文字、代码、表格、错误、工具运行/完成/确认，验证中途切换、停止、重复事件及关闭页面。先不接真实文件工具。
4. 包资源不走 CDN；API key 不进入 Web；校验消息来源、schema、尺寸；阻止外部导航，Markdown/图片不得自动访问任意本地文件或远程跟踪 URL。
5. 测量首次打开和工作集，验证中文输入法、双主题、键盘/读屏、滚动锚点。WebView2 Runtime 缺失与离线分发需有明确行为。

平台依据：[WPF WebView2 接入与消息通信](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf)、[本地资源映射](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content)、[受限桥接与来源校验](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)。以上为拟议试验，未安装或运行。

## 文档审查问题

### 阻断：恢复旧 UI 不满足本轮目标

位置：文档 W4（第 80 行）及 §2。

- 旧 `70c8c00^:Project/Views/Chat/MessageBubble.xaml.cs:134–163` 只累积正文，结束才渲染；`ChatView.xaml.cs:219–264` 每块强制滚底且未处理 ChatChunk.Error。
- `ChatView.xaml:130–134` 是 ScrollViewer + StackPanel，无消息虚拟化；工具卡创建时 result=null，Agent 执行结果后不刷新卡片（旧 ToolCallCard.xaml.cs:39–48、Agent.cs:87–94、138–152）。
- 旧 ChatView 发送后使用可变 currentAgent/currentSession；发送中可切换会话，存在串写风险（旧 ChatView.xaml.cs:120–125、219–235）。旧 UI 无停止入口。
- 修订：W4 改为选定界面与上述验收；C# 恢复范围逐项保留，不能将“原样恢复”作为质量验收。

### 阻断：文件白名单未构成目录权限边界

位置：文档 W2、W5、W6；当前 `Project/Core/Security/FileAccessWhitelist.cs:24–27`、`Project/Core/Tools/FileTools.cs:43–54`。

- 裸 StartsWith 会使白名单 `D:\safe` 接受 `D:\safe-other\x`；FileExists/ListDirectory 未做白名单检查。符号链接/reparse point 的最终目标策略也未定义。
- 修订：统一文件入口权限检查，目录相等或分隔符边界匹配，定义链接与设备路径策略；覆盖前缀同级、相对路径、链接逃逸及只读/写入权限测试，再向 Agent 接线。

### 阻断：工具开放范围与“全部注册”冲突

位置：文档 §2、W2、W5。

- §2 只选文件、网络、todos、看板，W2 又注册剪贴板、截图标签、动作链等。总开关不能表达每类工具的访问与外发许可；共享注册表也不能替代不同调用者的授权。
- 修订：列出内部聊天、内部看板轮次、外部 CLI 各自工具白名单及副作用；固定身份由运行时注入，不能让模型参数伪造。新增权限/外发范围单独提交用户确认。

### 阻断：tools.call 缺少长操作与重复执行契约

位置：文档 W6；当前 TaskIpc.cs:20–21、104–133、ToolRegistry.cs:108–137。

- IPC 请求 30 秒超时，文档允许等待模态确认和 LLM；客户端超时后结果未知，重试可能重复写入。现有反射调用没有接收执行取消令牌。
- 文档没定义 JsonElement 到实际参数类型的转换、未知/缺失参数、工具业务错误与退出码；现有 Convert.ChangeType 不应直接接收未转换 JsonElement。
- 修订：明确有界同步操作与长作业/待确认状态，定义 requestId 幂等、结果查询、取消及关闭行为；只在必要的确认 UI 上进入 Dispatcher，避免同步文件/OCR 工作堵 UI。给出完整 tools 响应与错误 schema。

### 阻断：图片“无需改 Provider”结论不成立

位置：文档 §3.3、W3；当前 OpenAiProvider.cs:221–230、AnthropicProvider.cs:260–279。

- 两者均将读入的任意图片字节标记为 image/png，与 jpg/gif/bmp/webp 入参清单不一致。能携带 ImagePath 不等于所有模型支持所有图片格式。
- 修订：按实际格式识别 MIME，或解码后转换到 Provider 支持格式；验证解码/尺寸/像素、模型能力与错误。定义图片路径权限及“指定图片”的授权，不只检查扩展名和文件大小。

### 阻断：只认领自己的卡片不能只约束 BoardTools

位置：文档 W2、W3、W7。

- 卡片 claim 校验不能限制同一 Agent 可调用的 FileTools、TodoTools、ActionChainTools。W7 未定义项目/工作目录、稳定内部身份、卡片租约续租、失效取消，以及轮次间的隔离。
- 修订：为内部看板轮次建立明确执行上下文和工具授权；派发前及写入时核对仍有效的完整 claim，定义卡片与 inbox 两种租约的续租/回收；输出或任务文本不能改变权限。

### 阻断：关闭与清空没有并发终止协议

位置：文档 W1、W5、W7、§8。

- 删除 chats.json/sessions 与清内存未规定如何停止正在流式生成、自动标题、30 秒保存计时器、内部回帖及记忆写入；“关闭即零调用”也未区分禁止新调用与撤销在途调用。
- 修订：先禁新轮次/工具，取消并等待在途操作，停保存并完成清理，再重置 UI/索引；定义 generation fence 防止迟到回调恢复已删数据；定义 memory.db/WAL/SHM 与图片附件清理范围。验证清空过程中生成/标题/工具完成和重启均无恢复。

### 阻断：internal 分支无法通过现有投递入口

位置：文档 W7；当前 BoardCommandService.cs:84–85、268–291，BoardAgentService.cs:154–170、186–193、470–475。

- comment.add 经 EnqueueAsync 入队；该方法将非 cli 宿主置 Failed/ManualPullRequired，只有 Queued 才送 worker。仅在 worker 增加 internal 分支仍不可达；默认 confirm 策略也未与“自动处理”统一。
- inbox 只返回有 ParentId 的已路由回复（ProjectBoardComments.cs:424–459），不会因创建普通根评论自动获得任务。W7 没有定义首次对话、绑定会话、初始认领的引导过程。
- 每条 inbox 有独立 claimId；ack 必须携带匹配的完整身份和未过期 claim（:207–224）。文档未定义身份跨轮次/重启的生命周期及两类租约的处理。
- 先回帖再 ack 之间崩溃会重复回帖：CommentAdd 每次创建新 ID（:47–59），ack 独立保存（:226–234）。
- 修订：明确 internal 的入口、确认与队列状态、触发范围和稳定身份；按原始评论 ID 保存处理关联，恢复时先找已提交结果再 ack；失败/超时不能直接标为已处理。补“回帖后 ack 前退出”的故障注入测试。

### 建议：发现入口和验收证据需要补齐

- W6 排除 AgentCapabilities 的理由不充分：现有 AgentCapabilityAvailability 已可声明运行时要求。至少让能力发现能找到 tools.list，动态可用性由开关与 tools.get 返回。
- §3.10 未列出库的固定版本、源码/许可与逐项否决证据；“DI 红线”不应泛化为所有 SDK 都强制 DI。后端库选型不能替代本次聊天 UI 选型。
- “冷启动 <1s”“警告基线 200”“639/47 检查基线”等应绑定当前 commit、设备、命令和报告；懒加载不证明首次打开性能。补恢复会话、取消、工具失败/重试、权限撤回、并发看板处理及重复投递的自动检查。
- 文档第 133 行关于工作日志已删除的说明不是本轮已核实事实；执行时只追加当前实际存在的周文件，不恢复被删历史。

## 用户决策点

1. 推荐 assistant-ui + WebView2；备选纯 WPF 局部移植。选定后修订 W4、依赖约束与桥接安全设计，再进入最小试验。
2. 需要移植上游源码时，先按仓库规则确认 LICENSE/NOTICE 文件处理方式；本轮未复制第三方代码或许可证文件。
3. 维持现有设计确认范围，新增工具访问/外发与最终系统提示词按具体内容确认。尚未确认的事实不写成“已确认不再变更”。

未运行候选构建、视觉演示、本机 WebView2 检测或性能测试；本报告不构成可直接发布的移植验证。
