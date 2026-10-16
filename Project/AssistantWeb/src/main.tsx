import { createRoot } from "react-dom/client";
import { AssistantApp } from "./assistant-app";

const root = document.getElementById("root");
if (!root) throw new Error("Assistant root element is missing");
createRoot(root).render(<AssistantApp />);
