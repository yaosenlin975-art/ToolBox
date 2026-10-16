import type { AssistantRun, AssistantSnapshot, AssistantUiState, WebEnvelope } from "./types";

export const initialState: AssistantUiState = {
  ready: false,
  enabled: false,
  generation: 0,
  sessions: [],
  activeSessionId: null,
  messages: [],
  runsBySession: {},
  textSequences: {},
  theme: "light",
  locale: "zh-CN",
  error: null,
};

export function shouldSubmitEnter(event: Pick<KeyboardEvent, "key" | "shiftKey" | "ctrlKey" | "altKey" | "metaKey" | "isComposing" | "keyCode">, composing: boolean): boolean {
  return event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing && !composing && event.keyCode !== 229;
}

export function reduceAssistantState(state: AssistantUiState, event: WebEnvelope): AssistantUiState {
  const payload = isObject(event.payload) ? event.payload : {};
  if (event.type === "snapshot") {
    const snapshot = payload as Partial<AssistantSnapshot>;
    if (typeof snapshot.generation !== "number") return { ...state, error: "收到无效的助手快照" };
    const activeSessionId = typeof snapshot.activeSessionId === "string" ? snapshot.activeSessionId : null;
    const snapshotRuns = Array.isArray(snapshot.runs) ? snapshot.runs as AssistantRun[] : [];
    const runsBySession = snapshot.generation === state.generation ? { ...state.runsBySession } : {};
    for (const run of snapshotRuns) {
      runsBySession[run.sessionId] = [...(runsBySession[run.sessionId] ?? []).filter(item => item.id !== run.id), run];
    }
    for (const sessionId of Object.keys(runsBySession)) {
      if (!snapshot.sessions?.some(session => session.id === sessionId)) delete runsBySession[sessionId];
    }
    return {
      ...state,
      ready: true,
      enabled: snapshot.enabled === true,
      generation: snapshot.generation,
      sessions: Array.isArray(snapshot.sessions) ? snapshot.sessions : [],
      activeSessionId,
      messages: Array.isArray(snapshot.messages) ? snapshot.messages : [],
      runsBySession,
      textSequences: snapshot.generation === state.generation ? state.textSequences : {},
      error: null,
    };
  }
  if (event.type === "theme") return { ...state, theme: payload.theme === "dark" ? "dark" : "light" };
  if (event.type === "locale") return { ...state, locale: payload.locale === "en-US" ? "en-US" : "zh-CN" };
  if (event.type === "error") {
    if (typeof payload.generation === "number" && payload.generation < state.generation) return state;
    if (event.sessionId && event.sessionId !== state.activeSessionId && event.runId) {
      const runsBySession = { ...state.runsBySession };
      const runs = [...(runsBySession[event.sessionId] ?? [])];
      const run = findOrCreateRun(runs, event.runId, event.sessionId, "failed", payload.message);
      run.status = "failed";
      runsBySession[event.sessionId] = runs;
      return { ...state, generation: Math.max(state.generation, Number(payload.generation ?? state.generation)), runsBySession };
    }
    return { ...state, error: typeof payload.message === "string" ? payload.message : "助手请求失败" };
  }
  if (event.type === "response") {
    return payload.ok === false
      ? { ...state, error: typeof payload.error === "string" ? payload.error : "助手请求失败" }
      : { ...state, error: null };
  }
  if (!event.sessionId || !event.type.startsWith("run.") && event.type !== "text.delta" && event.type !== "tool.status") return state;
  const eventGeneration = typeof payload.generation === "number" ? payload.generation : state.generation;
  if (eventGeneration < state.generation) return state;
  const runsBySession = { ...state.runsBySession };
  const runs = [...(runsBySession[event.sessionId] ?? [])];
  if (event.type === "run.status") {
    const run = findOrCreateRun(runs, event.runId, event.sessionId, payload.status, payload.error);
    if (typeof payload.status === "string") run.status = payload.status;
    if (typeof payload.error === "string") run.error = payload.error;
  } else if (event.type === "tool.status") {
    const run = findOrCreateRun(runs, event.runId, event.sessionId);
    const toolId = typeof payload.toolId === "string" ? payload.toolId : "";
    const toolIndex = run.tools.findIndex(tool => tool.id === toolId);
    const tool: AssistantRun["tools"][number] = {
      id: toolId,
      name: typeof payload.name === "string" ? payload.name : "工具",
      arguments: payload.arguments,
      status: typeof payload.status === "string" ? payload.status : "running",
      ...(payload.result === undefined ? {} : { result: payload.result }),
      ...(typeof payload.error === "string" ? { error: payload.error } : {}),
    };
    run.tools = toolIndex < 0 ? [...run.tools, tool] : run.tools.map((old, index) => index === toolIndex ? tool : old);
  } else if (event.type === "text.delta") {
    if (event.sessionId !== state.activeSessionId || typeof payload.messageId !== "string" || typeof payload.text !== "string") return state;
    const sequenceKey = `${event.sessionId}:${payload.messageId}`;
    const lastSequence = state.textSequences[sequenceKey] ?? -1;
    if (typeof payload.sequence === "number" && payload.sequence <= lastSequence) return state;
    const textSequences = typeof payload.sequence === "number"
      ? { ...state.textSequences, [sequenceKey]: payload.sequence }
      : state.textSequences;
    const messages = [...state.messages];
    const index = messages.findIndex(message => message.id === payload.messageId);
    if (index < 0) {
      messages.push({ id: payload.messageId, role: "assistant", content: payload.text, runId: event.runId, timestamp: new Date().toISOString() });
    } else {
      const old = messages[index];
      messages[index] = { ...old, runId: old.runId ?? event.runId, content: `${old.content ?? ""}${payload.text}` };
    }
    runsBySession[event.sessionId] = runs;
    return { ...state, generation: Math.max(state.generation, eventGeneration), messages, runsBySession, textSequences, error: null };
  }
  runsBySession[event.sessionId] = runs;
  return { ...state, generation: Math.max(state.generation, eventGeneration), runsBySession };
}

export function getActiveRun(state: AssistantUiState): AssistantRun | undefined {
  return [...(state.activeSessionId ? state.runsBySession[state.activeSessionId] ?? [] : [])].reverse().find(run => run.status === "running");
}

export function parseSnapshot(value: unknown): AssistantSnapshot | null {
  if (!isObject(value) || typeof value.enabled !== "boolean" || typeof value.generation !== "number" || !Array.isArray(value.sessions) || !Array.isArray(value.messages)) return null;
  return value as unknown as AssistantSnapshot;
}

function findOrCreateRun(runs: AssistantRun[], id: string | undefined, sessionId: string, status?: unknown, error?: unknown): AssistantRun {
  const runId = id ?? "unknown";
  let run = runs.find(value => value.id === runId);
  if (!run) {
    run = { id: runId, sessionId, status: typeof status === "string" ? status : "running", tools: [], startedAt: new Date().toISOString() };
    runs.push(run);
  }
  if (typeof error === "string") run.error = error;
  return run;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

