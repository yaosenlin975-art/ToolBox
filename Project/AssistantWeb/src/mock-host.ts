import type { AssistantSnapshot, WebEnvelope } from "./types";

type Listener = (event: MessageEvent<WebEnvelope>) => void;

export class MockWebViewHost {
  private readonly listeners = new Set<Listener>();
  private sequence = 0;
  readonly snapshot: AssistantSnapshot = {
    enabled: true,
    generation: 1,
    sessions: [
      { id: "a1b2c3d4", title: "欢迎使用助手" },
      { id: "e5f6a7b8", title: "会话列表演示" },
    ],
    activeSessionId: "a1b2c3d4",
    messages: [
      { id: "fixture-user", role: "user", content: "请说明这个界面的交互。", runId: "run-completed", timestamp: "2026-10-16T01:00:00Z" },
      { id: "fixture-assistant-1", role: "assistant", content: "## 回复排版\n\nAgent 正文没有气泡。下面是 GFM 表格：\n\n| 项目 | 状态 |\n| --- | --- |\n| Markdown | 已渲染 |\n| 图片 | 已阻止自动加载 |\n\n- 支持列表\n- 支持链接，例如 [OpenAI](https://openai.com)\n\n```ts\nconst ready = true;\n```", runId: "run-completed", timestamp: "2026-10-16T01:00:01Z" },
      { id: "fixture-user-2", role: "user", content: "展示一条失败的工具过程。", runId: "run-failed", timestamp: "2026-10-16T01:01:00Z" },
      { id: "fixture-assistant-2", role: "assistant", content: "本轮工具失败示例保留错误摘要。", runId: "run-failed", timestamp: "2026-10-16T01:01:01Z" },
    ],
    runs: [
      {
        id: "run-completed",
        sessionId: "a1b2c3d4",
        status: "completed",
        startedAt: "2026-10-16T01:00:00Z",
        completedAt: "2026-10-16T01:00:01Z",
        tools: [{ id: "tool-1", name: "web.search", arguments: "{\"query\":\"assistant-ui\"}", status: "completed", result: "找到 3 个结果。" }],
      },
      {
        id: "run-failed",
        sessionId: "a1b2c3d4",
        status: "failed",
        startedAt: "2026-10-16T01:01:00Z",
        completedAt: "2026-10-16T01:01:02Z",
        error: "演示错误：此状态仍可展开查看。",
        tools: [{ id: "tool-2", name: "file.read", arguments: "{\"path\":\"example.txt\"}", status: "failed", error: "文件不在允许目录中。" }],
      },
    ],
  };

  addEventListener(type: "message", listener: Listener): void {
    if (type === "message") this.listeners.add(listener);
  }

  removeEventListener(type: "message", listener: Listener): void {
    if (type === "message") this.listeners.delete(listener);
  }

  postMessage(message: unknown): void {
    if (!isEnvelope(message)) return;
    if (message.type === "ready") {
      this.emit("response", { ok: true, result: this.snapshot }, message.id);
      this.emit("snapshot", this.snapshot);
      return;
    }
    const result = this.execute(message);
    this.emit("response", { ok: true, result }, message.id);
    this.emit("snapshot", this.snapshot);
  }

  emit(type: string, payload: unknown, id = `mock-${++this.sequence}`, sessionId = this.snapshot.activeSessionId ?? undefined, runId?: string): void {
    const data: WebEnvelope = { version: 1, id, type, payload, ...(sessionId ? { sessionId } : {}), ...(runId ? { runId } : {}) };
    const event = { data } as MessageEvent<WebEnvelope>;
    for (const listener of this.listeners) listener(event);
  }

  private execute(message: WebEnvelope): unknown {
    switch (message.type) {
      case "session.list": return this.snapshot;
      case "session.create": {
        const id = `mock-${++this.sequence}`;
        this.snapshot.sessions = [{ id, title: "新会话" }, ...this.snapshot.sessions];
        this.snapshot.activeSessionId = id;
        this.snapshot.messages = [];
        this.snapshot.runs = [];
        return { id };
      }
      case "session.select":
        this.snapshot.activeSessionId = message.sessionId ?? null;
        this.snapshot.messages = this.snapshot.activeSessionId === "a1b2c3d4" ? this.snapshot.messages : [];
        return { id: this.snapshot.activeSessionId };
      case "session.rename": {
        const title = (message.payload as { title?: string }).title ?? "";
        this.snapshot.sessions = this.snapshot.sessions.map(session => session.id === message.sessionId ? { ...session, title } : session);
        return { id: message.sessionId, title };
      }
      case "session.delete":
        this.snapshot.sessions = this.snapshot.sessions.filter(session => session.id !== message.sessionId);
        if (this.snapshot.activeSessionId === message.sessionId) {
          this.snapshot.activeSessionId = this.snapshot.sessions[0]?.id ?? null;
          this.snapshot.messages = [];
        }
        return { deleted: message.sessionId };
      case "sessions.clear":
        this.snapshot.generation++;
        this.snapshot.sessions = [];
        this.snapshot.activeSessionId = null;
        this.snapshot.messages = [];
        this.snapshot.runs = [];
        return { cleared: true };
      case "message.send": {
        const text = (message.payload as { text?: string }).text ?? "";
        const runId = `run-${this.sequence}`;
        this.snapshot.messages = [...this.snapshot.messages, { id: `user-${++this.sequence}`, role: "user", content: text, runId }];
        this.snapshot.runs = [...this.snapshot.runs, { id: runId, sessionId: this.snapshot.activeSessionId ?? "", status: "running", startedAt: new Date().toISOString(), tools: [] }];
        this.emit("run.status", { status: "running", generation: this.snapshot.generation }, undefined, this.snapshot.activeSessionId ?? undefined, runId);
        window.setTimeout(() => {
          this.emit("tool.status", { toolId: `tool-${this.sequence}`, name: "demo.lookup", arguments: "{\"q\":\"sample\"}", status: "completed", result: "完成。", generation: this.snapshot.generation }, undefined, this.snapshot.activeSessionId ?? undefined, runId);
          this.emit("text.delta", { messageId: `assistant-${this.sequence}`, text: "这是来自 Mock Host 的流式示例回复。", sequence: 1, generation: this.snapshot.generation }, undefined, this.snapshot.activeSessionId ?? undefined, runId);
          this.snapshot.messages = [...this.snapshot.messages, { id: `assistant-${this.sequence}`, role: "assistant", content: "这是来自 Mock Host 的流式示例回复。", runId }];
          const run = this.snapshot.runs.find(item => item.id === runId);
          if (run) { run.status = "completed"; run.tools = [{ id: `tool-${this.sequence}`, name: "demo.lookup", arguments: "{\"q\":\"sample\"}", status: "completed", result: "完成。" }]; }
          this.emit("run.status", { status: "completed", generation: this.snapshot.generation }, undefined, this.snapshot.activeSessionId ?? undefined, runId);
          this.emit("snapshot", this.snapshot);
        }, 500);
        return { runId, status: "running" };
      }
      case "run.cancel": {
        const runId = (message.payload as { runId?: string }).runId;
        const run = this.snapshot.runs.find(item => item.id === runId);
        if (run) run.status = "canceled";
        this.emit("run.status", { status: "canceled", generation: this.snapshot.generation }, undefined, this.snapshot.activeSessionId ?? undefined, runId);
        return { canceled: Boolean(run), runId };
      }
      case "link.open": return { opened: true };
      default: return {};
    }
  }
}

function isEnvelope(value: unknown): value is WebEnvelope {
  return typeof value === "object" && value !== null && (value as WebEnvelope).version === 1 && typeof (value as WebEnvelope).type === "string";
}
