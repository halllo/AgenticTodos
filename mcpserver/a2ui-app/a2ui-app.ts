import {
  App,
  applyDocumentTheme,
  applyHostFonts,
  applyHostStyleVariables,
  type McpUiHostContext,
} from "@modelcontextprotocol/ext-apps";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { MessageProcessor, setMarkdownRenderer, type ActionPayload } from "@a2ui/web_core/v0_9";
import { basicCatalog } from "@a2ui/lit/v0_9";
import { renderMarkdown } from "@a2ui/markdown-it";
import { html, render } from "lit";
import { repeat } from "lit/directives/repeat.js";

// A2UI in MCP Apps (https://a2ui.org/guides/a2ui-in-mcp-apps/): the server returns A2UI messages
// as an embedded resource of this MIME type; this app renders them itself, and routes user actions
// either to an app-only server tool (tools/call) or, for agent-authored UIs, back to the agent.
const A2UI_MIME_TYPES = ["application/a2ui+json", "application/json+a2ui"];

const surfacesEl = document.getElementById("surfaces")!;
const statusEl = document.getElementById("status")!;

// Action names the server handles itself, announced next to the payload (see A2uiApp.cs).
// Any other action is sent to the agent as a user message, so it can answer with new UI.
const serverActions = new Set<string>();

setMarkdownRenderer(renderMarkdown);
const processor = new MessageProcessor([basicCatalog], onAction);
processor.onSurfaceCreated(renderSurfaces);
processor.onSurfaceDeleted(renderSurfaces);

const app = new App({ name: "A2UI App", version: "1.0.0" }, {});

app.onhostcontextchanged = applyHostContext;

app.ontoolresult = (result) => {
  if (result.isError) {
    setStatus(textOf(result) ?? "Tool failed.", true);
    return;
  }
  processResult(result as CallToolResult);
};

app.ontoolcancelled = ({ reason }) => setStatus(`Cancelled: ${reason ?? "no result"}`, true);

await app.connect();
const hostContext = app.getHostContext();
if (hostContext) applyHostContext(hostContext);

function applyHostContext(ctx: McpUiHostContext): void {
  if (ctx.theme) applyDocumentTheme(ctx.theme);
  if (ctx.styles?.variables) applyHostStyleVariables(ctx.styles.variables);
  if (ctx.styles?.css?.fonts) applyHostFonts(ctx.styles.css.fonts);
  // Let the A2UI basic catalog follow the host's palette.
  const root = document.documentElement.style;
  root.setProperty("--a2ui-color-primary", "var(--color-text-info, #0969da)");
  root.setProperty("--a2ui-color-background", "var(--color-background-primary, #fff)");
  root.setProperty("--a2ui-color-border", "var(--color-border-primary, #d0d7de)");
}

function processResult(result: CallToolResult): boolean {
  const resource = result.content?.find(
    (c) => c.type === "resource" && A2UI_MIME_TYPES.includes(c.resource.mimeType ?? ""),
  );
  if (resource?.type !== "resource" || !("text" in resource.resource)) return false;

  const actions = (resource.resource._meta?.["a2ui"] as { serverActions?: string[] } | undefined)?.serverActions;
  actions?.forEach((a) => serverActions.add(a));

  try {
    processor.processMessages(JSON.parse(resource.resource.text));
    setStatus("");
  } catch (e) {
    console.error("[A2UI App] failed to process A2UI messages", e);
    setStatus(`Invalid A2UI payload: ${e instanceof Error ? e.message : String(e)}`, true);
  }
  return true;
}

async function onAction(action: ActionPayload): Promise<void> {
  try {
    if (serverActions.has(action.name)) {
      const result = await app.callServerTool({ name: action.name, arguments: action.context });
      if (result.isError) setStatus(textOf(result) ?? `${action.name} failed.`, true);
      else processResult(result);
      return;
    }

    setStatus(`Sent "${action.name}" to the agent...`);
    const { isError } = await app.sendMessage({
      role: "user",
      content: [
        {
          type: "text",
          text:
            `[A2UI action] The user triggered "${action.name}" on surface "${action.surfaceId}" ` +
            `(component "${action.sourceComponentId}") with context:\n` +
            JSON.stringify(action.context, null, 2),
        },
      ],
    });
    setStatus(isError ? "The host could not deliver the action to the agent." : "", isError);
  } catch (e) {
    console.error("[A2UI App] action failed", e);
    setStatus(`Action "${action.name}" failed: ${e instanceof Error ? e.message : String(e)}`, true);
  }
}

function renderSurfaces(): void {
  const surfaces = [...processor.getSurfaces().values()];
  render(
    html`${repeat(
      surfaces,
      (s) => s.id,
      (s) => html`<a2ui-surface .surface=${s}></a2ui-surface>`,
    )}`,
    surfacesEl,
  );
}

function setStatus(text: string, isError = false): void {
  statusEl.textContent = text;
  statusEl.classList.toggle("error", isError);
}

function textOf(result: { content?: { type: string; text?: string }[] }): string | undefined {
  return result.content?.find((c) => c.type === "text")?.text;
}
