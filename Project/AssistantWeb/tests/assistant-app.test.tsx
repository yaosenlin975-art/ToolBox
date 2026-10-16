// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { AssistantApp } from "../src/assistant-app";
import type { AssistantTransport } from "../src/bridge";
import type { AssistantSnapshot, WebEnvelope } from "../src/types";

class FakeAssistantTransport implements AssistantTransport {
  private readonly listeners = new Set<(event: WebEnvelope) => void>();
  readonly commands: Array<{ type: string; sessionId?: string; payload: unknown }> = [];

  constructor(readonly snapshot: AssistantSnapshot) {}

  listen(listener: (event: WebEnvelope) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async send(type: string, sessionId?: string, payload: unknown = {}): Promise<unknown> {
    this.commands.push({ type, sessionId, payload });
    if (type === "ready") this.emit("snapshot", this.snapshot);
    return { ok: true };
  }

  emit(type: string, payload: unknown, sessionId = "a1b2c3d4", runId?: string): void {
    const event: WebEnvelope = {
      version: 1,
      id: `test-${this.commands.length}-${this.listeners.size}`,
      type,
      sessionId,
      payload,
      ...(runId ? { runId } : {}),
    };
    for (const listener of this.listeners) listener(event);
  }
}

let root: Root | undefined;
let host: FakeAssistantTransport;
let container: HTMLDivElement;

beforeEach(async () => {
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  Object.defineProperty(globalThis, "ResizeObserver", {
    configurable: true,
    value: class {
      observe(): void { }
      unobserve(): void { }
      disconnect(): void { }
    },
  });
  Object.defineProperty(HTMLElement.prototype, "scrollTo", {
    configurable: true,
    value: () => undefined,
  });
  container = document.createElement("div");
  document.body.appendChild(container);
  host = new FakeAssistantTransport(createSnapshot());
  root = createRoot(container);
  await act(async () => {
    root?.render(<AssistantApp transport={host} />);
  });
});

afterEach(async () => {
  if (root) await act(async () => root?.unmount());
  root = undefined;
  container.remove();
  document.body.replaceChildren();
});

describe("Assistant chat presentation and input", () => {
  it("keeps user bubbles right aligned and renders safe GFM without an assistant card", () => {
    expect(container.querySelector(".message-row-user")).not.toBeNull();
    expect(container.querySelector(".message-row-assistant")).not.toBeNull();
    expect(container.querySelector(".message-row-user")?.classList.contains("message-row-user")).toBe(true);

    const assistant = container.querySelector(".assistant-body");
    expect(assistant?.querySelector("h2")?.textContent).toBe("回答排版");
    expect(assistant?.querySelector("table")).not.toBeNull();
    expect(assistant?.querySelector("ul li")?.textContent).toContain("清单");
    expect(assistant?.querySelector("script, img")).toBeNull();
    expect(assistant?.closest(".message")?.classList.contains("message-assistant")).toBe(true);
    expect(assistant?.classList.contains("user-bubble")).toBe(false);
  });

  it("opens a process while its run is active and folds it after completion", async () => {
    const details = container.querySelector<HTMLDetailsElement>('[data-run-id="run-1"] details');
    expect(details?.open).toBe(false);

    await act(async () => host.emit("run.status", { status: "running", generation: 3 }, "a1b2c3d4", "run-1"));
    expect(details?.open).toBe(true);

    await act(async () => host.emit("run.status", { status: "completed", generation: 3 }, "a1b2c3d4", "run-1"));
    expect(details?.open).toBe(false);
    expect(container.querySelector('[data-run-id="run-1"] .tool-step strong')?.textContent).toBe("file.read");
  });

  it("only shows the return-to-bottom action when the viewport is away from the bottom", async () => {
    const viewport = container.querySelector<HTMLElement>(".thread-viewport");
    expect(viewport).not.toBeNull();
    if (!viewport) return;
    let scrollTop = 500;
    Object.defineProperty(viewport, "scrollHeight", { configurable: true, get: () => 1_000 });
    Object.defineProperty(viewport, "clientHeight", { configurable: true, get: () => 400 });
    Object.defineProperty(viewport, "scrollTop", { configurable: true, get: () => scrollTop, set: value => { scrollTop = Number(value); } });

    await act(async () => viewport.dispatchEvent(new Event("scroll")));
    expect(container.querySelector(".scroll-bottom")).not.toBeNull();

    await act(async () => {
      scrollTop = 550;
      viewport.dispatchEvent(new Event("scroll"));
    });
    expect(container.querySelector(".scroll-bottom")).toBeNull();
  });

  it("does not send Enter during IME composition or reset a draft on another session's run event", async () => {
    const input = container.querySelector<HTMLTextAreaElement>(".composer-input");
    expect(input).not.toBeNull();
    if (!input) return;

    await act(async () => {
      input.dispatchEvent(new CompositionEvent("compositionstart", { bubbles: true }));
      const valueSetter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")?.set;
      valueSetter?.call(input, "正在输入中文");
      input.dispatchEvent(new InputEvent("input", { bubbles: true, data: "文", inputType: "insertText" }));
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true, isComposing: true, keyCode: 229 }));
    });
    expect(host.commands.some(command => command.type === "message.send")).toBe(false);

    await act(async () => {
      host.emit("run.status", { status: "running", generation: 3 }, "e5f6a7b8", "background-run");
    });
    expect(input.value).toBe("正在输入中文");
    expect(host.commands.some(command => command.type === "message.send")).toBe(false);
  });
});

function createSnapshot(): AssistantSnapshot {
  return {
    enabled: true,
    generation: 3,
    sessions: [{ id: "a1b2c3d4", title: "演示会话" }, { id: "e5f6a7b8", title: "后台会话" }],
    activeSessionId: "a1b2c3d4",
    messages: [
      { id: "user-1", role: "user", content: "请看这个例子。", runId: "run-1" },
      {
        id: "assistant-1",
        role: "assistant",
        content: "## 回答排版\n\n| 项目 | 状态 |\n| --- | --- |\n| Markdown | 已渲染 |\n\n- 支持清单\n- <script>alert(1)</script>\n- ![remote](https://example.invalid/image.png)",
        runId: "run-1",
      },
    ],
    runs: [{
      id: "run-1",
      sessionId: "a1b2c3d4",
      status: "completed",
      startedAt: "2026-10-16T01:00:00Z",
      tools: [{ id: "tool-1", name: "file.read", arguments: "{\"path\":\"example.txt\"}", status: "completed", result: "内容" }],
    }],
  };
}
