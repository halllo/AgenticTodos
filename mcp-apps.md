# MCP Apps Architecture

How AgenticTodos renders **MCP apps** — interactive UI components an MCP server serves as resources — inside the chat: a tool whose MCP metadata carries a `ui.resourceUri` has its result turned into a custom AG-UI `ACTIVITY_SNAPSHOT` SSE event, and the frontend renders the app's HTML in a hardened double-iframe sandbox. Frontend (WebMCP) tools can carry apps too; the browser renders those itself — see [Frontend tool apps](#frontend-tool-apps).

Two independent MCP clients talk to the one MCP server: the backend agent's (`GetTools` in [backend/Program.cs](backend/Program.cs)) exposes MCP tools to the LLM; the browser's `McpClientService` ([frontend/src/app/mcp-client.service.ts](frontend/src/app/mcp-client.service.ts)) reads app resources (HTML) and lets apps call server tools, through `/agents/mcp-relay` rather than directly.

## Solution layout

.NET projects in [AgenticTodos.slnx](AgenticTodos.slnx); [apphost/AppHost.cs](apphost/AppHost.cs) orchestrates all of it with .NET Aspire, adding `frontend/` via `builder.AddViteApp(...)` rather than as a solution project. Dev URLs: `apphost/` — Aspire AppHost, service discovery, <http://localhost:15063>; `backend/` — ASP.NET Core agents, AG-UI endpoint, MCP relay, sandbox static files, <http://localhost:5288>; `mcpserver/` — hosts the MCP apps, <http://localhost:5082>; `frontend/` — Angular (Vite), embeds MCP apps, <http://localhost:3000>; `cli/` — AG-UI command-line client; `tests/` — xUnit tests.

The backend finds the MCP server through Aspire-injected keys `services:AgenticTodos-McpServer:https:0` / `…:http:0` (no hardcoded `McpServerUrl`); in dev the frontend proxies `/agents` to the **backend** ([frontend/src/proxy.conf.json](frontend/src/proxy.conf.json)), which is where both the AG-UI endpoint and the relay live.

## Backend

`CreateAgent` ([backend/Program.cs](backend/Program.cs)) builds each agent as an `AIAgentBuilder` chain; the MCP-apps-relevant links, in registration order:

```csharp
.UseStateSnapshot()
.UseDetectMcpAppsActivity()      // emits McpAppActivityContent (this doc)
.UseEUAIActRiskActivity()        // emits EUAIActRiskActivityContent (sibling)
.Use(inner => inner.UseEUAIActClassification(classifier ?? chatClient))  // innermost, runs once
```

### DetectMcpAppsActivityMiddleware

`UseDetectMcpAppsActivity()` — an `AIAgentBuilder` extension member in [backend/DetectMcpAppsActivityMiddleware.cs](backend/DetectMcpAppsActivityMiddleware.cs) (internal static class) registering streaming middleware via `agentBuilder.Use(runFunc, runStreamingFunc)` — tracks `FunctionCallContent` by `CallId` (tool name + serialized arguments), then after each `FunctionResultContent` reads `ProtocolTool.Meta["ui"]["resourceUri"]` off the matching `McpClientTool` on the run's `ChatClientAgentOptions` (no `resourceUri` → skip: an ordinary tool result), resolved via `GetService<McpClientTool>()` not `OfType`, so approval wrapping cannot hide it ([human-in-the-loop.md](human-in-the-loop.md)). `SerializeResult` rebuilds the MCP `CallToolResult` from the `AIContent`s `McpClientTool` returns (each keeps its original `ContentBlock` as `RawRepresentation`, so embedded resources keep their MIME type), and `NormalizeToolResult` coerces anything else to the MCP `CallToolResult` shape `{"content":[{"type":"text","text":…}]}` (already-shaped results, MEAI `TextContent`, JSON strings, raw fallback). The emitted [`McpAppActivityContent`](backend/AguiClientContent.cs) carries `resourceUri`, that normalised `result`, the `toolInput`, and `messageId` — the **tool call id**, so re-emitting replaces the rendered app (`replace: true`) rather than adding a second card for the same call.

### What the model sees of an app's result

Without help, the model does not know the user already sees an app's result, and its answer restates it. Only the client knows whether it renders apps, so it says so per run: the frontend sends `forwardedProps: { mcpApps: true }`. For such a run, `UseMcpAppShownNote()` ([backend/McpAppShownNoteMiddleware.cs](backend/McpAppShownNoteMiddleware.cs)), a chat-client middleware below the function-invocation loop, appends a note (`AppShownNote`) to the result of each tool that declares a `ui.resourceUri`: the user already sees this as an app, so do not restate it and answer in one short sentence. Error results get no note, so the model can still explain or retry.

The note is added in transit only: the app and the stored history keep the plain result, and a client that leaves the flag out (e.g. a text-only client) gets full answers. A server helps by keeping the text in `content` usable on its own, with app-only data in embedded resources or `structuredContent`.

For frontend tool apps (`show_todos`) the browser runs the tool and renders the app itself, so `ChatComponent` appends the same note to the result it sends back (the chat bubble keeps the plain result). That note is stored in the history, unlike the backend's.

### Content → ACTIVITY_SNAPSHOT

Unknown to the AG-UI server SDK, `McpAppActivityContent` reaches the `MapContent` fallback on the `AGUIStreamOptions` carried as endpoint metadata; `AGUIEndpoint.MapClientContent` ([backend/AGUIEndpoint.cs](backend/AGUIEndpoint.cs), `McpAppsActivityType`) emits:

```json
{"type":"ACTIVITY_SNAPSHOT","messageId":"…","activityType":"mcp-apps","replace":true,
 "content":{"resourceUri":"ui://…","result":{…},"toolInput":{…}}}
```

It must also be registered for `AIContent` polymorphism (`AddAIContentType` in `AGUIEndpoint.ConfigureAguiJson`) or `rawEvent` serialization fails — that contract, the `WithMetadata(CreateStreamOptions())` / `MapAGUIServer` wiring and adding an event kind: [custom-agui-events.md](custom-agui-events.md).

A sibling activity rides the same mechanism: `UseEUAIActClassification` (`EUAIActClassifier` package) runs once per turn as the innermost agent, and [backend/EUAIActRiskActivityMiddleware.cs](backend/EUAIActRiskActivityMiddleware.cs) (`UseEUAIActRiskActivity()`) turns its verdict — only at `Risk >= High` — into an `EUAIActRiskActivityContent`, mapped to `activityType:"eu-ai-act-risk"` (`EUAIActRiskActivityType`), content `{risk, category, reason}`.

### MCP relay proxy

The browser can't hold MCP server credentials or cross origins freely, so the backend reverse-proxies `/agents/mcp-relay` ([backend/Program.cs](backend/Program.cs)) to `{mcpBaseUrl}/mcp` (Aspire keys above), copying method, body and headers minus `Host` / `Transfer-Encoding` — only to that configured MCP server, so not an open proxy. A terminal `app.Use(...)` branch, not a mapped endpoint: it short-circuits before endpoint execution, so it cannot collide with the single `POST` `/agents/routed/{alias}/agui` from `app.MapAGUIViaHttpRoutingAgent()` (the `GET` 405 that forced it: [README.md](README.md)).

## MCP server (`mcpserver/`)

[mcpserver/Program.cs](mcpserver/Program.cs) registers a stateless HTTP MCP server, mounted at `/mcp`:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithMcpApp<GetTimeApp>().WithMcpApp<ThreejsApp>().WithMcpApp<PdfViewerApp>();
// …
app.MapMcp("/mcp");
```

`WithMcpApp<TApp>()` is a local extension doing `WithTools<TApp>().WithResources<TApp>()`: one class per app, contributing both a tool and the HTML resource its UI renders from. [mcpserver/GetTimeApp.cs](mcpserver/GetTimeApp.cs), minimal:

```csharp
const string URI = "ui://get-time.html";

[McpServerTool, Description("Gets the current time.")]
[McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]   // ← links tool → UI resource
public IEnumerable<ContentBlock> GetTime() => [new TextContentBlock { Text = $"{DateTime.Now}" }];

[McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
public async Task<string> GetTimeUIResource() =>
    await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "get-time-app", "dist", "get-time.html"));
```

The four registered apps, tools by **wire** name (not C# method name):

| App | Tool(s) | Resource URI | HTML built from |
| --- | --- | --- | --- |
| [GetTimeApp](mcpserver/GetTimeApp.cs) | `get_time` | `ui://get-time.html` | `get-time-app/dist/` |
| [ThreejsApp](mcpserver/ThreejsApp.cs) | `show_threejs_scene`, `learn_threejs` | `ui://threejs/mcp-app.html` | `threejs-app/dist/` |
| [PdfViewerApp](mcpserver/PdfViewerApp.cs) | `display_pdf`, `read_pdf_bytes` | `ui://pdf-viewer/mcp-app.html` | `pdf-viewer-app/dist/` |
| [A2uiApp](mcpserver/A2uiApp.cs) | `render_a2ui`, `show_a2ui_counter`, `increase_counter` (app-only) | `ui://a2ui/app.html` | `a2ui-app/dist/` |

`[McpServerTool]` without an explicit `Name` snake-cases the method (`GetTime()` ⇒ `get_time`, `ShowThreejsScene()` ⇒ `show_threejs_scene`; `PdfViewerApp`'s methods are already snake_case, so those two pass through unchanged). Outside the class only the wire name counts: the model, `app.callServerTool({ name: "get_time" })` in [get-time-app/get-time.ts](mcpserver/get-time-app/get-time.ts), `HumanInTheLoop:ApprovalRequiredTools` ([human-in-the-loop.md](human-in-the-loop.md#configuration)).

Conventions: the `ui://` URI scheme; MIME type `text/html;profile=mcp-app`, which the frontend validates against `RESOURCE_MIME_TYPE` from `@modelcontextprotocol/ext-apps`; extra origins in the resource's `_meta.ui.csp` domains, flowing into the sandbox CSP. Only the PDF viewer needs any: PDF.js is bundled (`pdfjs-dist` pinned in [pdf-viewer-app/package.json](mcpserver/pdf-viewer-app/package.json), inlined by `vite-plugin-singlefile`, worker included) but at runtime fetches its Standard-14 font data from unpkg.com twice — `fetch()` for the bytes, a `FontFace` `url()` — hence both `connectDomains` and `resourceDomains`.

**Tool visibility:** a tool whose `_meta.ui.visibility` leaves out `"model"` (e.g. `[McpMeta("ui", JsonValue = """{"visibility":["app"]}""")]`) is filtered out in `GetTools` ([backend/Program.cs](backend/Program.cs)), so only its app can call it, through the relay. Note `GetTools` runs once per backend start: restart the backend after adding tools.

**Adding an app:** copy the `GetTimeApp` shape (`[McpServerTool]` + `[McpMeta]` `"ui"` → `{"resourceUri":"ui://…"}`, `[McpServerResource]` returning built HTML), register with `.WithMcpApp<YourApp>()`, build a self-contained HTML/JS bundle at that `dist/` path using `@modelcontextprotocol/ext-apps` app-side for `toolInput`/`toolResult` and server-tool calls. The frontend needs nothing for a server app: any tool result carrying `ui.resourceUri` flows through `DetectMcpAppsActivityMiddleware` → `mcp-apps` snapshot → `<app-mcp-app>`.

## Frontend (`frontend/`)

Key packages ([frontend/package.json](frontend/package.json) pins versions): `@ag-ui/client`, `@modelcontextprotocol/client` (MCP SDK v2, which `@modelcontextprotocol/ext-apps` 2.x and `@mcp-b/*` 6.x are built on), `@modelcontextprotocol/server` (pinned to the exact version `@mcp-b` pins, so the browser bundle carries one copy; [Frontend tool apps](#frontend-tool-apps)), `@modelcontextprotocol/ext-apps`, `@mcp-b/global` / `@mcp-b/transports`, Angular — and deliberately not `@ag-ui/core` ([README.md](README.md)).

**`McpClientService`** ([frontend/src/app/mcp-client.service.ts](frontend/src/app/mcp-client.service.ts)): `providedIn: 'root'` singleton, connects once (`infoPromise ??= connect()`) to `/agents/mcp-relay` — `StreamableHTTPClientTransport`, falling back to `SSEClientTransport` — exposing the MCP `Client`, maps of tools/resources, an `appHtmlCache`.

**ACTIVITY_SNAPSHOT handling** ([frontend/src/app/chat.component.ts](frontend/src/app/chat.component.ts)): `onActivitySnapshotEvent`, registered in the AG-UI `agent.subscribe(...)` callback, branches on `event.activityType`, upserting by `event.messageId` (`upsertActivityMessage` replaces in place on re-send, else appends): `"mcp-apps"` → `role: 'activity'` with `resourceUri`, `toolInput`, `toolResult`, templated as a `chat__toolIndicator` label (`MCP App · {{ message.resourceUri }}`) + `<app-mcp-app [resourceUri] [toolInput] [toolResult] />`; `"eu-ai-act-risk"` → `role: 'risk'` with `{ risk, category, reason }` as a `chat__riskBadge` (`EU AI Act · {{ message.risk?.risk }} risk`).

**`McpAppComponent`** ([frontend/src/app/mcp-app.component.ts](frontend/src/app/mcp-app.component.ts), selector `app-mcp-app`; inputs `resourceUri` (required), `toolInput`, `toolResult`, `source` — `'server'` (default) or `'frontend'`). On init: `readResource({ uri: resourceUri })` on the client `source` picks — the shared relay client, or the tab's WebMCP client for frontend tool apps; the `AppBridge` gets the same client, so an app's tool calls reach the server it came from; `mimeType === RESOURCE_MIME_TYPE` validated; HTML from `text` or base64 `blob`, CSP / permissions from `_meta.ui.csp` / `_meta.ui.permissions` (falling back to `meta.ui`); `loadSandboxProxy(iframe, csp, permissions, abortSignal)` points the iframe at `SANDBOX_URL`, CSP as `?csp=<json>`, awaiting the `ui/notifications/sandbox-proxy-ready` handshake (10s timeout); an `AppBridge` — host context: theme, platform, `styles.variables` from `HOST_STYLE_VARIABLES`, display modes — `connect`s over a `PostMessageTransport`, handling `onsizechange` (resize the iframe), `onrequestdisplaymode`, `onopenlink`, `onmessage` (an app's `ui/message` text becomes a user turn via the chat's `sendAppMessage` input, refused with `isError` while a run or approval is pending); then `sendSandboxResourceReady({ html, csp, permissions })`, `sendToolInput(...)`, `sendToolResult(...)` — or `sendToolCancelled` when there is no result. `HOST_STYLE_VARIABLES` ([frontend/src/app/host-styles.ts](frontend/src/app/host-styles.ts)): `McpUiStyles` CSS custom properties (colors, fonts, radii, shadows, `light-dark()`) passed to every app for consistent theming.

> `SANDBOX_URL` is a hardcoded dev constant, `http://localhost:5288/sandbox.html` — the **backend** origin, deliberately not the frontend one. Revisit for non-dev deployments.

**Side panel** — each app card in the chat has an *Open in side panel* button, so the app stays visible while the chat moves on. `AppPanelService` ([frontend/src/app/app-panel.service.ts](frontend/src/app/app-panel.service.ts)) holds the moved apps, keyed by the activity's `messageId`, with everything `<app-mcp-app>` needs. `AppPanelComponent` ([frontend/src/app/app-panel.component.ts](frontend/src/app/app-panel.component.ts)) stacks them in a third, resizable column of `App` ([frontend/src/app/app.ts](frontend/src/app/app.ts)), which is hidden while it is empty. An app lives in one place at a time: while it is in the panel, its chat card shows a placeholder and *Back to chat* returns it. Moving an app recreates its iframe, so the app restarts from the original tool input and result, and any state it held only in the iframe is lost (state kept on the server, like the A2UI counter, survives). Apps in the panel send `ui/message` through the chat's `sendAppMessage`, the same as apps in the chat.

**Opening an app without the chat** — the *Apps* button in the chat header (`AppLauncherComponent`, [frontend/src/app/app-launcher.component.ts](frontend/src/app/app-launcher.component.ts)) lists the MCP server's app resources (`text/html;profile=mcp-app`, with the `Title` and description each `[McpServerResource]` declares) and opens the picked one straight into the side panel. There is no tool call: the frontend reads the `ui://` resource itself, and `McpAppComponent` sends the app neither tool input nor result (nor a cancellation). So an app must cope with starting empty: the A2UI app shows "No surface yet", Three.js its default scene, the time app its *Get Server Time* button; the PDF viewer waits for a file.

The chat drives such an app afterwards, in two directions:

- **Model → app.** A tool call whose `ui.resourceUri` matches an app open in the panel (from the launcher or moved there) is delivered to that app (`AppPanelService.deliver`; the most recently opened one if several match) as fresh tool input and result, instead of rendering a second copy in the chat; the chat card only says *Updated the app in the side panel* and offers *Show in chat*. The A2UI app replaces a surface whose `createSurface` id it already shows.
- **App → model.** Each run sends an AG-UI `context` entry listing the panel's apps, the tools that drive each, and what each app last reported about itself through `ui/update-model-context` (the A2UI app reports its surface ids and current data models, so the model sees what the user typed). The AG-UI server SDK does not pass `context` to the model; `ClientContextProvider` ([backend/ClientContextProvider.cs](backend/ClientContextProvider.cs)), the agent's `AIContextProvider`, does, as per-run instructions that never enter the stored history.

## Frontend tool apps

Frontend tools — WebMCP tools registered in the page and executed by the browser ([human-in-the-loop.md](human-in-the-loop.md) covers how their calls flow) — can render an MCP app the same way. The backend has no part in it: AG-UI tool definitions carry no `_meta`, and the browser already holds the arguments and the result when it runs the tool.

**Registration** — `WebmcpService` ([frontend/src/app/webmcp.service.ts](frontend/src/app/webmcp.service.ts)): `registerAppTool({ name, description, inputSchema, resourceUri, execute })` and `registerAppResource({ uri, name, html, csp?, permissions? })`, wrapping the ext-apps server helpers `registerAppTool` / `registerAppResource` — the TypeScript counterparts of `[McpMeta("ui", …)]` and `[McpServerResource]` on the C# side. They register on `document.modelContext.mcpServer`, the MCP SDK `McpServer` behind `@mcp-b`'s `BrowserMcpServer`, **not** through `document.modelContext.registerTool`: `@mcp-b` 6.0.3 builds the MCP tool from name, description, schemas and annotations only and drops `_meta`, so the link to the app would be lost. The trade-off: such tools reach MCP clients on the tab (the chat, the MCP-B extension) but are not mirrored to a browser's native `navigator.modelContext`. Once `@mcp-b` forwards `_meta` ([WebMCP-org/npm-packages#387](https://github.com/WebMCP-org/npm-packages/pull/387)), app tools can move back to plain `registerTool` and nothing downstream changes. `inputSchema` is JSON Schema, adapted with `fromJsonSchema` from `@modelcontextprotocol/server` — the reason that package is a direct dependency.

**Discovery** is the standard MCP one: `initializeClient()` keeps each tool's `_meta` from WebMCP `listTools`, and `uiResourceUriFor(name)` reads `_meta.ui.resourceUri` (or the legacy flat `ui/resourceUri` key ext-apps writes alongside it). So any tool on the tab's WebMCP server that declares an app gets one, not only those registered through `WebmcpService`. The client advertises the MCP Apps extension (`io.modelcontextprotocol/ui`, `EXTENSION_ID`) in its capabilities for servers that check `getUiCapability`.

**Rendering** — [frontend/src/app/chat.component.ts](frontend/src/app/chat.component.ts): in the `onRunFinishedEvent` invoke loop, after a WebMCP tool runs, a tool that declares an app gets an `activity` message via `upsertActivityMessage`, keyed by the tool call id like the backend's snapshots, with `toolInput`, the raw `CallToolResult` (`invokeTool` returns it next to the text the model gets; a failed call becomes an `isError` result), and `appSource: 'frontend'` → `<app-mcp-app [source]="'frontend'">`. Same double-iframe sandbox on the backend origin as server apps.

**Bundles** live in `frontend/src/mcp-apps/` and are built by `npm run build:apps` (esbuild, like `build:sandbox`; chained into `start` / `dev` / `build`) into `frontend/public/mcp-apps/` (git-ignored). A resource's `html` callback fetches the bundle and inlines it into one HTML document (`inlineAppHtml` in [frontend/src/app/app.ts](frontend/src/app/app.ts)), since an app resource must be self-contained.

The example: `show_todos` ([frontend/src/app/app.ts](frontend/src/app/app.ts), resource `ui://todos/todos-app.html`, app [frontend/src/mcp-apps/todos-app.ts](frontend/src/mcp-apps/todos-app.ts)) returns the left panel's todos as JSON, and its app lists them and adds new ones by calling `add_todo` then `show_todos` through the bridge.

**Adding a frontend app:** write the app in `frontend/src/mcp-apps/` against `@modelcontextprotocol/ext-apps` (`App`, `ontoolresult`, `callServerTool`), add it to `build:apps`, then `registerAppResource` + `registerAppTool` with the matching `ui://` URI before `initializeClient()`.

## A2UI apps

[A2uiApp](mcpserver/A2uiApp.cs) follows [A2UI in MCP Apps](https://a2ui.org/guides/a2ui-in-mcp-apps/): a tool returns [A2UI](https://a2ui.org/) v0.9 messages (`createSurface`, `updateComponents`, `updateDataModel`, `deleteSurface`) as an embedded resource with MIME type `application/a2ui+json`, and the app ([mcpserver/a2ui-app/a2ui-app.ts](mcpserver/a2ui-app/a2ui-app.ts)) renders them itself with the `@a2ui/lit` basic catalog and a `MessageProcessor` from `@a2ui/web_core/v0_9`. The host does nothing A2UI-specific.

- `render_a2ui` — **dynamic**: the LLM writes the messages (its description is a short v0.9 + basic catalog primer). The server checks that each message has exactly one known kind, stamps `version`, defaults `catalogId` to the basic catalog, and returns an `isError` result the model can correct otherwise.
- `show_a2ui_counter` / `increase_counter` — the guide's counter sample, with a fixed surface.

A2UI button actions (`{"event":{"name":…,"context":…}}`) arrive in the app as an `ActionPayload` with data bindings already resolved, and are routed in one of two ways. If the action name is listed in the payload resource's `_meta.a2ui.serverActions`, the app sends `tools/call` to that app-only server tool and applies the A2UI messages it returns (the counter). Any other action goes back to the agent as a `ui/message` user turn, `[A2UI action] … with context: {…}`, so the model can reply in text or call `render_a2ui` again.

## Sandbox and CSP (double-iframe)

Untrusted app HTML never runs in the host page: it runs in an **inner** iframe created and policed by an **outer** sandbox page on a different origin — never serve the sandbox from the frontend origin. [frontend/src/sandbox.ts](frontend/src/sandbox.ts) → `npm run build:sandbox` (esbuild) → `backend/wwwroot/sandbox.js`, served beside `backend/wwwroot/sandbox.html` by `app.UseStaticFiles()`: **no separate sandbox server** — the isolation is backend origin (`:5288`) versus frontend origin (`:3000`).

`sandbox.ts`, in the outer iframe:

1. Validates `document.referrer` against `^http:\/\/(localhost|127\.0\.0\.1)(:|\/|$)`, pins the expected host origin, rejects everything else.
2. Creates the inner iframe with `sandbox="allow-scripts allow-same-origin allow-forms"` plus an `allow` attribute from requested permissions; never `allow-top-navigation` or `allow-popups-to-escape-sandbox`. (It replaces the attribute with a host-supplied `params.sandbox` on `sandbox-resource-ready` — this host never sends one, so that set is what the inner frame gets.)
3. Posts `ui/notifications/sandbox-proxy-ready` to the host, then relays postMessages both ways, checking origin every hop: host messages against the host origin, inner ones against its own.
4. On `ui/notifications/sandbox-resource-ready`, injects the HTML via `document.write()` (`srcdoc` fallback; `document.write` is needed for libraries like CesiumJS).

The outer page's CSP is a `Content-Security-Policy` **HTTP header** — not a `<meta>` tag, which injected content can bypass — set on `/sandbox.html` from that URL's `?csp=` param by a backend middleware ordered before static files ([backend/McpUiResourceCsp.cs](backend/McpUiResourceCsp.cs): `ToMcpUiResourceCsp` / `BuildHeader`). The `McpUiResourceCsp` record carries `resourceDomains` / `connectDomains` / `frameDomains` / `baseUriDomains`, declared in the resource's **`_meta.ui.csp`** object — sibling of `_meta.ui.permissions`, one level below `_meta.ui` itself, read by `mcp-app.component.ts` as `uiMeta?.csp`; domains directly on `_meta.ui` yield no CSP at all rather than an error. `BuildHeader` drops any domain containing a character that could break out of a directive (`;`, quote, whitespace, newline) rather than trusting the app.

## Testing

`DetectMcpAppsActivityMiddleware.NormalizeToolResult` shapes: [tests/DetectMcpAppsActivityMiddlewareTests.cs](tests/DetectMcpAppsActivityMiddlewareTests.cs). Which verdicts the risk middleware surfaces (`High` and above only) and its one-per-run latch: [tests/EUAIActRiskActivityMiddlewareTests.cs](tests/EUAIActRiskActivityMiddlewareTests.cs). A full agent run emitting `ACTIVITY_SNAPSHOT` without leaking the payload as chat text: [tests/ActivitySnapshotConformanceTests.cs](tests/ActivitySnapshotConformanceTests.cs). Mapping and wiring — [tests/AguiClientContentMappingTests.cs](tests/AguiClientContentMappingTests.cs), [tests/AguiEndpointWiringTests.cs](tests/AguiEndpointWiringTests.cs) — are described in [custom-agui-events.md](custom-agui-events.md).

The conformance test needs a **running** backend and real LLM calls, so [tests/IntegrationFactAttributes.cs](tests/IntegrationFactAttributes.cs) skips it unless `AG_UI_ENDPOINT` names one (`AG_UI_ENDPOINT=http://localhost:5288/agents/routed/openai/agui dotnet test`). The same file gates live provider tests behind `RUN_LIVE_LLM_TESTS=1`, and even then skips them (naming the offenders) if provider keys are missing from configuration/user-secrets — otherwise they fail inside a provider constructor with an opaque `ArgumentNullException` that reads like a broken test. So plain `dotnet test` is hermetic and free.

## Related docs

[custom-agui-events.md](custom-agui-events.md), [README.md](README.md) (overview, dev setup, problems log), [AGENTS.md](AGENTS.md) (running via Aspire), [attachments.md](attachments.md) (uploads/attachments).
