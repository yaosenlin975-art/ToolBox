import {
  AssistantRuntimeProvider,
  ComposerPrimitive,
  ThreadPrimitive,
  useExternalStoreRuntime,
  type ExternalStoreAdapter,
  type ThreadMessage,
} from "@assistant-ui/react";
import React, { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { WebViewTransport, type AssistantTransport } from "./bridge";
import { getActiveRun, initialState, reduceAssistantState, shouldSubmitEnter } from "./state";
import type { AssistantRun, AssistantUiState, ChatMessage, Session, ToolStep, WebEnvelope } from "./types";
import "./style.css";

const defaultTransport = new WebViewTransport();
const labels = {
  "zh-CN": {
    newChat: "新会话", clear: "清空", sessions: "会话", rename: "重命名", delete: "删除", cancel: "取消",
    confirmDelete: "确认删除会话？", confirmClear: "确认清空全部会话与记忆？", empty: "开始一段新对话",
    enabledHint: "请先在设置中启用助手", noSession: "新建或选择一个会话后即可开始", retry: "重新连接",
    unavailable: "助手暂不可用。请检查 WebView2 Runtime 与本地助手资源后重试。", loading: "正在连接助手…",
    placeholder: "输入消息…（Enter 发送，Shift+Enter 换行）", send: "发送", stop: "停止", backToBottom: "回到底部",
    process: "执行过程", running: "运行中", completed: "已完成", canceled: "已停止", failed: "失败", started: "开始时间",
    tool: "工具", arguments: "参数", result: "结果", copy: "复制代码", copied: "已复制", noTools: "本轮没有工具调用",
    blockedImage: "图片未加载", renameTitle: "会话名称", deleteConfirm: "确认", emptyRuntime: "等待助手状态…",
  },
  "en-US": {
    newChat: "New chat", clear: "Clear", sessions: "Chats", rename: "Rename", delete: "Delete", cancel: "Cancel",
    confirmDelete: "Delete this conversation?", confirmClear: "Clear all conversations and memory?", empty: "Start a new conversation",
    enabledHint: "Enable Assistant in Settings first", noSession: "Create or select a conversation to begin", retry: "Reconnect",
    unavailable: "Assistant is unavailable. Check WebView2 Runtime and local assistant resources, then retry.", loading: "Connecting to Assistant…",
    placeholder: "Message… (Enter to send, Shift+Enter for a new line)", send: "Send", stop: "Stop", backToBottom: "Back to bottom",
    process: "Execution", running: "Running", completed: "Completed", canceled: "Stopped", failed: "Failed", started: "Started",
    tool: "Tool", arguments: "Arguments", result: "Result", copy: "Copy code", copied: "Copied", noTools: "No tools called this turn",
    blockedImage: "Image not loaded", renameTitle: "Conversation title", deleteConfirm: "Confirm", emptyRuntime: "Waiting for assistant status…",
  },
} as const;
type LocaleLabels = (typeof labels)[keyof typeof labels];

type Action = { type: "host"; event: WebEnvelope } | { type: "local-error"; message: string };

export interface AssistantAppProps {
  transport?: AssistantTransport;
}

export function AssistantApp({ transport = defaultTransport }: AssistantAppProps) {
  const [state, dispatch] = useReducer((current: AssistantUiState, action: Action) => {
    if (action.type === "local-error") return { ...current, error: action.message };
    return reduceAssistantState(current, action.event);
  }, initialState);
  const [viewport, setViewport] = useState<HTMLDivElement | null>(null);
  const [showBackToBottom, setShowBackToBottom] = useState(false);

  useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    try {
      unsubscribe = transport.listen(event => dispatch({ type: "host", event }));
      void transport.send("ready", undefined, {}).catch(error => dispatch({ type: "local-error", message: errorMessage(error) }));
    } catch (error) {
      dispatch({ type: "local-error", message: errorMessage(error) });
    }
    return () => unsubscribe?.();
  }, [transport]);

  useEffect(() => {
    document.documentElement.dataset.theme = state.theme;
    document.documentElement.lang = state.locale;
  }, [state.theme, state.locale]);

  useEffect(() => {
    if (!viewport) return;
    const update = () => setShowBackToBottom(isScrolledFromBottom(viewport));
    const observer = new ResizeObserver(update);
    observer.observe(viewport);
    const content = viewport.querySelector(".thread-content");
    if (content) observer.observe(content);
    viewport.addEventListener("scroll", update, { passive: true });
    update();
    return () => {
      observer.disconnect();
      viewport.removeEventListener("scroll", update);
    };
  }, [viewport, state.messages, state.runsBySession]);

  const send = useCallback(async (type: string, sessionId?: string, payload: unknown = {}) => {
    try {
      return await transport.send(type, sessionId, payload);
    } catch (error) {
      dispatch({ type: "local-error", message: errorMessage(error) });
      throw error;
    }
  }, [transport]);

  const activeRun = getActiveRun(state);
  const threadMessages = useMemo(() => toThreadMessages(state.messages, activeRun), [state.messages, activeRun]);
  const adapter = useMemo<ExternalStoreAdapter<ThreadMessage>>(() => ({
    messages: threadMessages,
    isDisabled: !state.enabled || !state.activeSessionId,
    isSendDisabled: !state.enabled || !state.activeSessionId || activeRun != null,
    isRunning: activeRun != null,
    activeRunId: activeRun?.id,
    onNew: async message => {
      const text = message.content.filter(part => part.type === "text").map(part => part.text).join("");
      if (!text.trim()) return;
      if (!state.activeSessionId) throw new Error(labels[state.locale].noSession);
      await send("message.send", state.activeSessionId, { text });
    },
    onCancel: async () => {
      if (state.activeSessionId && activeRun) await send("run.cancel", state.activeSessionId, { runId: activeRun.id });
    },
  }), [threadMessages, state.enabled, state.activeSessionId, state.locale, activeRun, send]);
  const runtime = useExternalStoreRuntime(adapter);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <main className="assistant-app">
        <SessionSidebar state={state} send={send} />
        <section className="conversation" aria-label={labels[state.locale].sessions}>
          <header className="conversation-header">
            <h1>{activeSessionTitle(state)}</h1>
            {activeRun && <span className="run-pill" role="status">{labels[state.locale].running}</span>}
          </header>
          {state.error && <div className="error-banner" role="alert">{state.error}</div>}
          {!state.ready ? (
            <StatusPanel message={state.error ?? labels[state.locale].loading} action={labels[state.locale].retry} onAction={() => void send("ready")} />
          ) : !state.enabled ? (
            <StatusPanel message={labels[state.locale].enabledHint} action={labels[state.locale].retry} onAction={() => void send("ready")} />
          ) : !state.activeSessionId ? (
            <StatusPanel message={labels[state.locale].noSession} action={labels[state.locale].newChat} onAction={() => void send("session.create")} />
          ) : (
            <>
              <ThreadPrimitive.Root className="thread-root">
                <ThreadPrimitive.Viewport className="thread-viewport" autoScroll ref={setViewport}>
                  <div className="thread-content" aria-live="polite" aria-relevant="additions text">
                    {state.messages.length === 0 ? (
                      <p className="empty-thread">{labels[state.locale].empty}</p>
                    ) : (
                      <ConversationMessages state={state} send={send} />
                    )}
                  </div>
                  {showBackToBottom && <ThreadPrimitive.ScrollToBottom className="scroll-bottom" aria-label={labels[state.locale].backToBottom}>
                    ↓ {labels[state.locale].backToBottom}
                  </ThreadPrimitive.ScrollToBottom>}
                </ThreadPrimitive.Viewport>
              </ThreadPrimitive.Root>
              <Composer activeRun={activeRun} enabled={state.enabled && Boolean(state.activeSessionId)} sendLabel={labels[state.locale].send} stopLabel={labels[state.locale].stop} placeholder={labels[state.locale].placeholder} />
            </>
          )}
        </section>
      </main>
    </AssistantRuntimeProvider>
  );
}

function SessionSidebar({ state, send }: { state: AssistantUiState; send: AssistantAppSend }) {
  const text = labels[state.locale];
  const [editing, setEditing] = useState<string | null>(null);
  const [title, setTitle] = useState("");
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);

  const rename = (session: Session) => {
    const value = title.trim();
    if (!value) return;
    void send("session.rename", session.id, { title: value }).then(() => setEditing(null));
  };

  return (
    <aside className="sidebar" aria-label={text.sessions}>
      <div className="sidebar-heading">
        <h2>{text.sessions}</h2>
        <button className="icon-button" aria-label={text.newChat} title={text.newChat} onClick={() => void send("session.create")}>＋</button>
      </div>
      <ul className="session-list">
        {state.sessions.map(session => (
          <li key={session.id} className={session.id === state.activeSessionId ? "session active" : "session"}>
            {editing === session.id ? (
              <form className="rename-form" onSubmit={event => { event.preventDefault(); rename(session); }}>
                <input aria-label={text.renameTitle} autoFocus value={title} maxLength={120} onChange={event => setTitle(event.target.value)} onKeyDown={event => { if (event.key === "Escape") setEditing(null); }} />
                <button type="submit" aria-label={text.rename}>✓</button>
                <button type="button" aria-label={text.cancel} onClick={() => setEditing(null)}>×</button>
              </form>
            ) : confirmDelete === session.id ? (
              <div className="confirm-row">
                <span>{text.confirmDelete}</span>
                <button aria-label={text.deleteConfirm} onClick={() => void send("session.delete", session.id).then(() => setConfirmDelete(null))}>✓</button>
                <button aria-label={text.cancel} onClick={() => setConfirmDelete(null)}>×</button>
              </div>
            ) : (
              <>
                <button className="session-select" title={session.title} onClick={() => void send("session.select", session.id)}>{session.title}</button>
                <div className="session-actions">
                  <button aria-label={`${text.rename}: ${session.title}`} onClick={() => { setTitle(session.title); setEditing(session.id); }}>✎</button>
                  <button aria-label={`${text.delete}: ${session.title}`} onClick={() => setConfirmDelete(session.id)}>×</button>
                </div>
              </>
            )}
          </li>
        ))}
      </ul>
      <div className="sidebar-footer">
        {confirmClear ? (
          <div className="confirm-row">
            <span>{text.confirmClear}</span>
            <button aria-label={text.deleteConfirm} onClick={() => void send("sessions.clear").then(() => setConfirmClear(false))}>✓</button>
            <button aria-label={text.cancel} onClick={() => setConfirmClear(false)}>×</button>
          </div>
        ) : (
          <button className="text-button" onClick={() => setConfirmClear(true)}>{text.clear}</button>
        )}
      </div>
    </aside>
  );
}

type AssistantAppSend = (type: string, sessionId?: string, payload?: unknown) => Promise<unknown>;

function ConversationMessages({ state, send }: { state: AssistantUiState; send: AssistantAppSend }) {
  const messages = state.messages.filter(message => message.role === "user" || message.role === "assistant");
  const runs = [...(state.activeSessionId ? state.runsBySession[state.activeSessionId] ?? [] : [])].sort((a, b) => (a.startedAt ?? "").localeCompare(b.startedAt ?? ""));
  const usedRuns = new Set<string>();
  const nodes = messages.map(message => {
    const run = message.role === "assistant"
      ? (message.runId ? runs.find(item => item.id === message.runId) : undefined) ?? legacyRunForMessage(message, state.activeSessionId ?? "")
      : undefined;
    if (run) usedRuns.add(run.id);
    return <React.Fragment key={message.id}>
      <Message message={message} send={send} locale={state.locale} />
      {run && <RunDetails run={run} locale={state.locale} />}
    </React.Fragment>;
  });
  for (const run of runs) {
    if (!usedRuns.has(run.id)) nodes.push(<RunDetails key={`run-${run.id}`} run={run} locale={state.locale} />);
  }
  return <>{nodes}</>;
}

function Message({ message, send, locale }: { message: ChatMessage; send: AssistantAppSend; locale: "zh-CN" | "en-US" }) {
  const isUser = message.role === "user";
  return (
    <article className={`message-row ${isUser ? "message-row-user" : "message-row-assistant"}`} data-message-id={message.id}>
      <div className={`message ${isUser ? "message-user" : "message-assistant"}`} data-role={message.role}>
        {isUser ? <div className="user-bubble">{message.content ?? ""}</div> : <div className="assistant-body"><MarkdownBody text={message.content ?? ""} send={send} locale={locale} /></div>}
      </div>
    </article>
  );
}

function RunDetails({ run, locale }: { run: AssistantRun; locale: "zh-CN" | "en-US" }) {
  const text = labels[locale];
  const [open, setOpen] = useState(run.status === "running");
  useEffect(() => {
    if (run.status !== "running") setOpen(false);
    else setOpen(true);
  }, [run.status]);
  const statusLabel = run.status === "running" ? text.running : run.status === "failed" ? text.failed : run.status === "canceled" ? text.canceled : text.completed;
  return (
    <section className="run-section" data-run-id={run.id}>
      <details open={open} onToggle={event => setOpen(event.currentTarget.open)}>
        <summary>
          <span>{text.process}</span>
          <span className={`status status-${run.status}`}>{statusLabel}</span>
          {run.startedAt && <time dateTime={run.startedAt}>{text.started}: {formatTime(run.startedAt, locale)}</time>}
        </summary>
        {run.tools.length === 0 ? <p className="run-empty">{text.noTools}</p> : run.tools.map(tool => <ToolStepView key={tool.id} tool={tool} locale={locale} />)}
        {run.error && <p className="run-error" role="alert">{run.error}</p>}
      </details>
      {run.error && !open && <p className="run-error run-error-preview">{run.error}</p>}
    </section>
  );
}

function ToolStepView({ tool, locale }: { tool: ToolStep; locale: "zh-CN" | "en-US" }) {
  const text = labels[locale];
  return (
    <div className="tool-step">
      <div className="tool-title"><strong>{tool.name || text.tool}</strong><span className={`status status-${tool.status}`}>{statusLabel(tool.status, text)}</span></div>
      <div className="tool-field"><span>{text.arguments}</span><pre>{formatValue(tool.arguments)}</pre></div>
      {tool.result !== undefined && <div className="tool-field"><span>{text.result}</span><pre>{formatValue(tool.result)}</pre></div>}
      {tool.error && <p className="run-error">{tool.error}</p>}
    </div>
  );
}

function Composer({ activeRun, enabled, sendLabel, stopLabel, placeholder }: { activeRun?: AssistantRun; enabled: boolean; sendLabel: string; stopLabel: string; placeholder: string }) {
  const composing = useRef(false);
  return (
    <ComposerPrimitive.Root className="composer" aria-label={placeholder}>
      <ComposerPrimitive.Input
        aria-label={placeholder}
        className="composer-input"
        placeholder={placeholder}
        disabled={!enabled}
        submitMode="none"
        onCompositionStart={() => { composing.current = true; }}
        onCompositionEnd={() => { composing.current = false; }}
        onKeyDown={event => {
          if (!shouldSubmitEnter(event.nativeEvent, composing.current)) return;
          event.preventDefault();
          event.currentTarget.form?.requestSubmit();
        }}
      />
      {activeRun ? (
        <ComposerPrimitive.Cancel className="stop-button" aria-label={stopLabel} title={stopLabel}>{stopLabel}</ComposerPrimitive.Cancel>
      ) : (
        <ComposerPrimitive.Send className="send-button" aria-label={sendLabel} title={sendLabel} disabled={!enabled}>{sendLabel}</ComposerPrimitive.Send>
      )}
    </ComposerPrimitive.Root>
  );
}

function MarkdownBody({ text, send, locale }: { text: string; send: AssistantAppSend; locale: "zh-CN" | "en-US" }) {
  const components = useMemo<Components>(() => ({
    a: ({ href, children }) => {
      const url = safeHttpUrl(href);
      if (!url) return <span>{children}</span>;
      return <a href={url} rel="noreferrer" onClick={event => {
        event.preventDefault();
        if (event.nativeEvent.isTrusted) void send("link.open", undefined, { url }).catch(() => undefined);
      }}>{children}</a>;
    },
    img: () => <span className="blocked-image" aria-label={labels[locale].blockedImage}>{labels[locale].blockedImage}</span>,
    pre: ({ children }) => <CodeBlock locale={locale}>{children}</CodeBlock>,
  }), [send, locale]);
  return <ReactMarkdown skipHtml remarkPlugins={[remarkGfm]} components={components}>{text}</ReactMarkdown>;
}

function CodeBlock({ children, locale }: { children: React.ReactNode; locale: "zh-CN" | "en-US" }) {
  const [copied, setCopied] = useState(false);
  const text = extractText(children);
  const labelsForLocale = labels[locale];
  return (
    <div className="code-block">
      <button className="copy-code" aria-label={labelsForLocale.copy} onClick={() => {
        void navigator.clipboard.writeText(text).then(() => {
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1_500);
        }).catch(() => setCopied(false));
      }}>{copied ? labelsForLocale.copied : labelsForLocale.copy}</button>
      <pre>{children}</pre>
    </div>
  );
}

function StatusPanel({ message, action, onAction }: { message: string; action: string; onAction: () => void }) {
  return <div className="status-panel" role="status"><p>{message}</p><button className="primary-button" onClick={onAction}>{action}</button></div>;
}

function toThreadMessages(messages: readonly ChatMessage[], activeRun?: AssistantRun): ThreadMessage[] {
  const visible = messages.filter(message => message.role === "user" || message.role === "assistant");
  return visible.map((message, index) => {
    const createdAt = message.timestamp ? new Date(message.timestamp) : new Date(0);
    const text = message.content ?? "";
    if (message.role === "user") return {
      id: message.id,
      createdAt: Number.isNaN(createdAt.valueOf()) ? new Date(0) : createdAt,
      role: "user" as const,
      content: [{ type: "text" as const, text }],
      attachments: [],
      metadata: { custom: {} },
    };
    const isRunning = index === visible.length - 1 && activeRun != null;
    return {
      id: message.id,
      createdAt: Number.isNaN(createdAt.valueOf()) ? new Date(0) : createdAt,
      role: "assistant" as const,
      content: [{ type: "text" as const, text, status: isRunning ? { type: "running" as const } : { type: "complete" as const } }],
      status: isRunning ? { type: "running" as const } : { type: "complete" as const, reason: "stop" as const },
      metadata: { unstable_state: null, unstable_annotations: [], unstable_data: [], steps: [], custom: {} },
    };
  });
}

function activeSessionTitle(state: AssistantUiState): string {
  return state.sessions.find(session => session.id === state.activeSessionId)?.title ?? labels[state.locale].sessions;
}

function safeHttpUrl(value?: string): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") && !url.username && !url.password ? url.toString() : null;
  } catch {
    return null;
  }
}

function formatValue(value: unknown): string {
  if (typeof value === "string") {
    try { return JSON.stringify(JSON.parse(value), null, 2); }
    catch { return value; }
  }
  try { return JSON.stringify(value, null, 2) ?? ""; }
  catch { return String(value); }
}

function formatTime(value: string, locale: "zh-CN" | "en-US"): string {
  const time = new Date(value);
  return Number.isNaN(time.valueOf()) ? value : time.toLocaleString(locale);
}

function extractText(children: React.ReactNode): string {
  return React.Children.toArray(children).map(child => {
    if (typeof child === "string" || typeof child === "number") return String(child);
    if (React.isValidElement<{ children?: React.ReactNode }>(child)) return extractText(child.props.children);
    return "";
  }).join("");
}

function statusLabel(status: string, text: LocaleLabels): string {
  if (status === "running") return text.running;
  if (status === "failed") return text.failed;
  if (status === "canceled") return text.canceled;
  return text.completed;
}

function legacyRunForMessage(message: ChatMessage, sessionId: string): AssistantRun | undefined {
  if (!message.toolCalls?.length) return undefined;
  return {
    id: `legacy-${message.id}`,
    sessionId,
    status: message.toolCalls.some(tool => tool.isError || tool.status === "failed") ? "failed" : "completed",
    tools: message.toolCalls.map(tool => ({
      id: tool.id,
      name: tool.name,
      arguments: tool.arguments,
      status: tool.isError || tool.status === "failed" ? "failed" : "completed",
      ...(tool.result === undefined ? {} : { result: tool.result }),
      ...(tool.error === undefined && !tool.isError ? {} : { error: tool.error ?? "调用失败" }),
    })),
  };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "助手宿主不可用";
}

function isScrolledFromBottom(viewport: HTMLDivElement): boolean {
  return viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight > 80;
}
