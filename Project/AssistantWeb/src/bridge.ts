import type { WebEnvelope } from "./types";

interface WebViewMessageApi {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent<WebEnvelope>) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<WebEnvelope>) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewMessageApi };
  }
}

export interface AssistantTransport {
  listen(listener: (event: WebEnvelope) => void): () => void;
  send(type: string, sessionId?: string, payload?: unknown): Promise<unknown>;
}

export class WebViewTransport implements AssistantTransport {
  listen(listener: (event: WebEnvelope) => void): () => void {
    const webview = window.chrome?.webview;
    if (!webview) throw new Error("未连接到 ToolBox 助手宿主");
    const handle = (event: MessageEvent<WebEnvelope>) => {
      if (isEnvelope(event.data)) {
        dispatchResponse(event.data);
        listener(event.data);
      }
    };
    webview.addEventListener("message", handle);
    return () => webview.removeEventListener("message", handle);
  }

  send(type: string, sessionId?: string, payload: unknown = {}): Promise<unknown> {
    const webview = window.chrome?.webview;
    if (!webview) return Promise.reject(new Error("未连接到 ToolBox 助手宿主"));
    const envelope: WebEnvelope = {
      version: 1,
      id: crypto.randomUUID(),
      type,
      ...(sessionId ? { sessionId } : {}),
      payload,
    };
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        window.removeEventListener(`assistant-response-${envelope.id}`, onResponse);
        reject(new Error("助手宿主响应超时"));
      }, 30_000);
      const onResponse = (event: Event) => {
        window.clearTimeout(timer);
        const detail = (event as CustomEvent<{ ok: boolean; result?: unknown; error?: string }>).detail;
        window.removeEventListener(`assistant-response-${envelope.id}`, onResponse);
        if (detail.ok) resolve(detail.result);
        else reject(new Error(detail.error ?? "助手请求失败"));
      };
      window.addEventListener(`assistant-response-${envelope.id}`, onResponse, { once: true });
      try {
        webview.postMessage(envelope);
      } catch (error) {
        window.clearTimeout(timer);
        window.removeEventListener(`assistant-response-${envelope.id}`, onResponse);
        reject(error instanceof Error ? error : new Error("助手消息发送失败"));
      }
    });
  }
}

export function dispatchResponse(envelope: WebEnvelope): void {
  if (envelope.type !== "response") return;
  const payload = typeof envelope.payload === "object" && envelope.payload !== null
    ? envelope.payload as { ok?: unknown; result?: unknown; error?: unknown }
    : {};
  window.dispatchEvent(new CustomEvent(`assistant-response-${envelope.id}`, {
    detail: {
      ok: payload.ok === true,
      result: payload.result,
      error: typeof payload.error === "string" ? payload.error : undefined,
    },
  }));
}

function isEnvelope(value: unknown): value is WebEnvelope {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const envelope = value as Partial<WebEnvelope>;
  return envelope.version === 1 && typeof envelope.id === "string" && typeof envelope.type === "string";
}
