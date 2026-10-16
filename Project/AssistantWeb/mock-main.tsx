import { createRoot } from "react-dom/client";
import { AssistantApp } from "./src/assistant-app";
import { MockWebViewHost } from "./src/mock-host";
import type { WebEnvelope } from "./src/types";

const host = new MockWebViewHost();
window.chrome = { webview: host };

const controls = document.getElementById("mock-controls");
if (controls) {
  const addButton = (name: string, action: () => void) => {
    const button = document.createElement("button");
    button.textContent = name;
    button.onclick = action;
    controls.appendChild(button);
  };
  addButton("Light", () => host.emit("theme", { theme: "light" }));
  addButton("Dark", () => host.emit("theme", { theme: "dark" }));
  addButton("中文", () => host.emit("locale", { locale: "zh-CN" }));
  addButton("English", () => host.emit("locale", { locale: "en-US" }));
  addButton("Other-session run", () => host.emit("run.status", { status: "running", generation: host.snapshot.generation }, undefined, "e5f6a7b8", "background-run"));
  addButton("Mock event", () => {
    const event: WebEnvelope = { version: 1, id: "fixture", type: "error", sessionId: "e5f6a7b8", runId: "background-run", payload: { code: "DEMO", message: "Background session error", generation: host.snapshot.generation } };
    host.emit(event.type, event.payload, event.id, event.sessionId, event.runId);
  });
}

const root = document.getElementById("root");
if (!root) throw new Error("Assistant root element is missing");
createRoot(root).render(<AssistantApp />);
