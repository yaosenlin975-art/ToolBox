export type Role = "user" | "assistant" | "tool" | "system";

export interface ChatMessage {
  id: string;
  role: Role;
  content: string | null;
  runId?: string | null;
  timestamp?: string;
  toolCalls?: readonly ToolStep[];
}

export interface Session {
  id: string;
  title: string;
}

export interface ToolStep {
  id: string;
  name: string;
  arguments: unknown;
  status: string;
  result?: unknown;
  error?: string;
  isError?: boolean;
}

export interface AssistantRun {
  id: string;
  sessionId: string;
  status: string;
  tools: readonly ToolStep[];
  error?: string;
  startedAt?: string;
  completedAt?: string;
}

export interface AssistantSnapshot {
  enabled: boolean;
  generation: number;
  sessions: readonly Session[];
  activeSessionId: string | null;
  messages: readonly ChatMessage[];
  runs: readonly AssistantRun[];
}

export interface WebEnvelope {
  version: 1;
  id: string;
  type: string;
  sessionId?: string;
  runId?: string;
  payload: unknown;
}

export interface AssistantUiState {
  ready: boolean;
  enabled: boolean;
  generation: number;
  sessions: readonly Session[];
  activeSessionId: string | null;
  messages: readonly ChatMessage[];
  runsBySession: Readonly<Record<string, readonly AssistantRun[]>>;
  textSequences: Readonly<Record<string, number>>;
  theme: "light" | "dark";
  locale: "zh-CN" | "en-US";
  error: string | null;
}
