# MCP Apps Architecture

How AgenticTodos renders **MCP apps** — interactive UI components served by an MCP server as
resources — inside the chat. When the agent calls a tool whose MCP metadata carries a
`ui.resourceUri`, the backend turns the tool result into a custom AG-UI `ACTIVITY_SNAPSHOT` event,
and the frontend renders the app's HTML inside a hardened double-iframe sandbox.

This is a living architecture doc: it describes what is in the repo today and links to the source.
For the lower-level SSE interception mechanics it builds on, see [sse-event-injection.md](sse-event-injection.md).

---

## End-to-end flow

```text
User prompt
  → Agent calls an MCP tool (e.g. get_time)                         [backend agent, server-side MCP client]
  → Tool's MCP metadata has ui.resourceUri
  → DetectMcpAppsActivityMiddleware emits an `mcp-activity` marker   [DataContent, application/x-mcp-activity]
  → AG-UI framework serialises it as a TEXT_MESSAGE_CONTENT SSE event
  → ActivitySnapshotInjectionMiddleware (on /agui) replaces it       [via McpAppsActivityInjector]
      with an ACTIVITY_SNAPSHOT event (activityType "mcp-apps")
  → Frontend onActivitySnapshotEvent handler creates an "activity" message
  → <app-mcp-app> reads the app HTML from the MCP server             [browser-side MCP client via /agents/mcp-relay]
  → Renders it in a double-iframe sandbox served from the backend origin
  → AppBridge delivers toolInput + toolResult to the app via postMessage
```

Two independent MCP clients talk to the one MCP server:

| Client | Where | Purpose |
| --- | --- | --- |
| Server-side | Backend agent (`GetTools` in [backend/Program.cs](backend/Program.cs)) | Exposes MCP tools to the LLM |
| Browser-side | `McpClientService` ([frontend/src/app/mcp-client.service.ts](frontend/src/app/mcp-client.service.ts)) | Reads app resources (HTML) and lets apps call server tools |

The browser-side client reaches the MCP server through the backend's `/agents/mcp-relay` proxy
rather than connecting directly.

---

## Solution layout

Defined in [AgenticTodos.slnx](AgenticTodos.slnx) and orchestrated by .NET Aspire
([apphost/AppHost.cs](apphost/AppHost.cs)):

| Project / dir | Role | Dev URL |
| --- | --- | --- |
| `apphost/` | Aspire AppHost — orchestrates everything, wires service discovery | http://localhost:15063 |
| `backend/` | ASP.NET Core host: agents, AG-UI endpoint, MCP relay, sandbox static files | http://localhost:5288 |
| `mcpserver/` | In-repo MCP server hosting the MCP apps | http://localhost:5082 |
| `frontend/` | Angular app (Vite), embeds MCP apps | http://localhost:3000 |
| `cli/` | Command-line AG-UI client | — |
| `tests/` | xUnit tests | — |

The backend discovers the MCP server through Aspire-injected config keys
`services:AgenticTodos-McpServer:https:0` / `…:http:0` (no hardcoded `McpServerUrl`). In dev the
frontend proxies `/agents` to the backend ([frontend/src/proxy.conf.json](frontend/src/proxy.conf.json)).

---

## Backend

### Agent middleware pipeline

Each agent is built in `CreateAgent` ([backend/Program.cs](backend/Program.cs)) as an
`AIAgentBuilder` chain. The MCP-apps-relevant links, in registration order:

```csharp
.Use(runFunc: StateSnapshotMiddleware.RunAsync, runStreamingFunc: StateSnapshotMiddleware.RunStreamingAsync)
.UseDetectMcpAppsActivity()                       // emits mcp-activity markers (this doc)
.UseEUAIActRiskActivity()                          // emits eu-ai-act-activity markers (sibling activity)
.Use(inner => inner.UseEUAIActClassification(classifier))  // classifies the turn (innermost, runs once)
```

`UseDetectMcpAppsActivity()` is an extension member on `AIAgentBuilder` defined in
[backend/DetectMcpAppsActivityMiddleware.cs](backend/DetectMcpAppsActivityMiddleware.cs); it
registers streaming middleware via `agentBuilder.Use(runFunc, runStreamingFunc)`.

### DetectMcpAppsActivityMiddleware — emit the marker

[backend/DetectMcpAppsActivityMiddleware.cs](backend/DetectMcpAppsActivityMiddleware.cs) (internal
static class). While streaming agent updates it:

1. Tracks `FunctionCallContent` by `CallId` (capturing tool name + serialized arguments).
2. After each `FunctionResultContent`, looks up the matching `McpClientTool` on the current run's
   `ChatClientAgentOptions` and reads `ProtocolTool.Meta["ui"]["resourceUri"]`. No `resourceUri` →
   skip (normal tool result).
3. Normalises the result to the MCP `CallToolResult` shape `{"content":[{"type":"text","text":…}]}`
   via `NormalizeToolResult` (handles already-shaped results, MEAI `TextContent`, JSON strings, and
   raw fallback).
4. Emits the marker as `DataContent(bytes, "application/x-mcp-activity")`:

   ```json
   {"type":"mcp-activity","messageId":"…","resourceUri":"ui://…","result":{…},"toolInput":{…}}
   ```

The `application/x-mcp-activity` MIME type matters: the AG-UI framework routes `DataContent` with
this type through a `TEXT_MESSAGE_CONTENT` event (rather than `STATE_SNAPSHOT`), which is exactly
where the SSE injector looks. `NormalizeToolResult` is covered by
[tests/ToolResultNormalizerTests.cs](tests/ToolResultNormalizerTests.cs).

### SSE injection — marker → ACTIVITY_SNAPSHOT

The marker rides out as a `TEXT_MESSAGE_CONTENT` SSE event and is rewritten before the client sees
it, by middleware registered only on `/agui` paths ([backend/Program.cs](backend/Program.cs)):

```csharp
app.UseWhen(
    ctx => ctx.Request.Path.Value?.EndsWith("/agui", StringComparison.OrdinalIgnoreCase) == true,
    branch => branch.UseMiddleware<ActivitySnapshotInjectionMiddleware>());
```

- [backend/SseEventInjectionMiddleware.cs](backend/SseEventInjectionMiddleware.cs) — **abstract**
  base. Swaps `Response.Body` for a nested `SseInterceptorStream`, applies the subclass's
  `Inject(eventJson)` to each `data:` event (`null` → suppress, empty → forward unchanged,
  non-empty → replace), and converts eager downstream exceptions into a `RUN_STARTED` + `RUN_ERROR`
  pair. Full mechanics in [sse-event-injection.md](sse-event-injection.md).
- [backend/ActivitySnapshotInjectionMiddleware.cs](backend/ActivitySnapshotInjectionMiddleware.cs) —
  the concrete subclass. Its `Inject` delegates to the static `TryInject`, which tries each
  activity-snapshot injector in order: **MCP apps first, then EU AI Act risk**. The first to suppress
  (`null`) or claim (non-empty) wins; an empty result means "not mine — try the next".

[backend/McpAppsActivityInjector.cs](backend/McpAppsActivityInjector.cs) (`TryInjectActivitySnapshot`)
matches `TEXT_MESSAGE_CONTENT` whose `delta` parses to a `type:"mcp-activity"` marker and replaces it
with:

```json
{"type":"ACTIVITY_SNAPSHOT","messageId":"…","activityType":"mcp-apps","replace":true,
 "content":{"resourceUri":"ui://…","result":{…},"toolInput":{…}}}
```

Routing and the injector contract are covered by
[tests/EUAIActRiskActivityInjectorTests.cs](tests/EUAIActRiskActivityInjectorTests.cs) and
[tests/SseEventInjectionMiddlewareTests.cs](tests/SseEventInjectionMiddlewareTests.cs).

### Sibling activity: EU AI Act risk

The same marker → SSE-injection mechanism powers a second activity type. The classifier
(`UseEUAIActClassification`, from the `EUAIActClassifier` package) runs once per turn as the
innermost agent; its verdict bubbles out to
[backend/EUAIActRiskActivityMiddleware.cs](backend/EUAIActRiskActivityMiddleware.cs)
(`UseEUAIActRiskActivity()`), which — only when `Risk >= High` — emits an `eu-ai-act-activity`
marker (MIME `application/x-eu-ai-act-activity`).
[backend/EUAIActRiskActivityInjector.cs](backend/EUAIActRiskActivityInjector.cs) then rewrites it to
an `ACTIVITY_SNAPSHOT` with `activityType:"eu-ai-act-risk"` and content `{risk, category, reason}`.
It shares `ActivitySnapshotInjectionMiddleware` with the MCP-apps injector.

### MCP relay proxy

The browser can't hold MCP server credentials or cross origins freely, so the backend exposes a
transparent reverse proxy at `/agents/mcp-relay` ([backend/Program.cs](backend/Program.cs)). It
forwards to `{mcpBaseUrl}/mcp` (resolved from the Aspire service-discovery keys above), copying the
method, body, and headers (minus `Host` / `Transfer-Encoding`).

> **Ordering:** it is registered with `app.Use(...)` **before** `app.MapAGUIViaHttpRoutingAgent()`,
> which otherwise intercepts all `/agents/*` paths.

### CSP for the sandbox

Before static files, a middleware sets a `Content-Security-Policy` header on `/sandbox.html`, built
from a `?csp=` query param ([backend/McpUiResourceCsp.cs](backend/McpUiResourceCsp.cs):
`ToMcpUiResourceCsp` / `BuildHeader`). The `McpUiResourceCsp` record carries
`resourceDomains` / `connectDomains` / `frameDomains` / `baseUriDomains`, which an app declares in
its resource `_meta.ui`. Setting CSP via an HTTP header (not a `<meta>` tag) keeps it tamper-proof.

---

## MCP server (`mcpserver/`)

[mcpserver/Program.cs](mcpserver/Program.cs) registers a stateless HTTP MCP server and mounts it at
`/mcp`:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithMcpApp<GetTimeApp>()
    .WithMcpApp<ThreejsApp>()
    .WithMcpApp<PdfViewerApp>();
// …
app.MapMcp("/mcp");
```

`WithMcpApp<TApp>()` is a local extension that does `WithTools<TApp>().WithResources<TApp>()` — i.e.
each app class contributes both a tool and the HTML resource the tool's UI renders from.

An MCP app is a single class. [mcpserver/GetTimeApp.cs](mcpserver/GetTimeApp.cs) is the minimal
example:

```csharp
public class GetTimeApp
{
    const string URI = "ui://get-time.html";

    [McpServerTool, Description("Gets the current time.")]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]   // ← links tool → UI resource
    public IEnumerable<ContentBlock> GetTime() => [new TextContentBlock { Text = $"{DateTime.Now}" }];

    [McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
    public async Task<string> GetTimeUIResource() =>
        await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "get-time-app", "dist", "get-time.html"));
}
```

The three registered apps:

| App | Tool(s) | Resource URI | HTML built from |
| --- | --- | --- | --- |
| [GetTimeApp](mcpserver/GetTimeApp.cs) | `GetTime` | `ui://get-time.html` | `get-time-app/dist/` |
| [ThreejsApp](mcpserver/ThreejsApp.cs) | `ShowThreejsScene`, `LearnThreejs` | `ui://threejs/mcp-app.html` | `threejs-app/dist/` |
| [PdfViewerApp](mcpserver/PdfViewerApp.cs) | `display_pdf`, `read_pdf_bytes` | `ui://pdf-viewer/mcp-app.html` | `pdf-viewer-app/dist/` |

Conventions: resources use the `ui://` URI scheme and the MIME type `text/html;profile=mcp-app`
(the frontend validates this against `RESOURCE_MIME_TYPE` from `@modelcontextprotocol/ext-apps`
before rendering). Apps that need extra origins (e.g. the PDF viewer loads PDF.js from a CDN) declare
them in the resource's `_meta.ui` CSP domains, which flow through to the sandbox CSP.

---

## Frontend (`frontend/`)

Key packages (versions in [frontend/package.json](frontend/package.json)): `@ag-ui/client` /
`@ag-ui/core`, `@modelcontextprotocol/sdk`, `@modelcontextprotocol/ext-apps`, Angular.

### McpClientService

[frontend/src/app/mcp-client.service.ts](frontend/src/app/mcp-client.service.ts) — a `providedIn:
'root'` singleton that connects once (`infoPromise ??= connect()`) to `/agents/mcp-relay`, trying
`StreamableHTTPClientTransport` then falling back to `SSEClientTransport`. It exposes the MCP
`Client` plus maps of tools/resources and an `appHtmlCache`.

### ACTIVITY_SNAPSHOT handling

[frontend/src/app/chat.component.ts](frontend/src/app/chat.component.ts) registers
`onActivitySnapshotEvent` inside the AG-UI `agent.subscribe(...)` callback. It branches on
`event.activityType` and upserts a message keyed by `event.messageId` (`upsertActivityMessage`
replaces in place on re-send, otherwise appends):

- `"mcp-apps"` → `role: 'activity'`, carrying `resourceUri`, `toolInput`, `toolResult`.
- `"eu-ai-act-risk"` → `role: 'risk'`, carrying `{ risk, category, reason }`.

The template renders each role:

```html
@if (message.role === 'activity') {
  <span class="chat__toolIndicator">MCP App · {{ message.resourceUri }}</span>
  <app-mcp-app [resourceUri]="message.resourceUri!" [toolInput]="message.toolInput ?? {}" [toolResult]="message.toolResult" />
} @else if (message.role === 'risk') {
  <span class="chat__riskBadge">EU AI Act · {{ message.risk?.risk }} risk</span>
  …
}
```

### McpAppComponent

[frontend/src/app/mcp-app.component.ts](frontend/src/app/mcp-app.component.ts) (selector
`app-mcp-app`). Inputs: `resourceUri` (required), `toolInput`, `toolResult`. On init it:

1. Gets the shared MCP client and `readResource({ uri: resourceUri })`; validates
   `mimeType === RESOURCE_MIME_TYPE`; extracts the HTML (`text` or base64 `blob`) and any CSP /
   permissions from the resource `_meta.ui`.
2. Calls `loadSandboxProxy(iframe, SANDBOX_URL, …)`, which points the iframe at the sandbox page with
   the CSP passed as `?csp=<json>` and waits for the `ui/notifications/sandbox-proxy-ready` handshake
   (10s timeout).
3. Constructs an `AppBridge` (host context: theme, platform, `styles.variables` from `HOST_STYLE_VARIABLES`,
   display modes) and `connect`s it over a `PostMessageTransport`. Handlers cover `onsizechange`
   (resize the iframe), `onrequestdisplaymode`, and `onopenlink`.
4. `sendSandboxResourceReady({ html, csp, permissions })`, then `sendToolInput(...)` and
   `sendToolResult(...)` (or `sendToolCancelled` when there is no result).

> `SANDBOX_URL` is currently the hardcoded dev constant `http://localhost:5288/sandbox.html`. It must
> point at the **backend** origin (where the sandbox is served), which is deliberately different from
> the frontend origin to get cross-origin isolation. Revisit this for non-dev deployments.

`HOST_STYLE_VARIABLES` ([frontend/src/app/host-styles.ts](frontend/src/app/host-styles.ts)) is a set
of `McpUiStyles` CSS custom properties (colors, fonts, radii, shadows — using `light-dark()`) passed
to every app for consistent theming.

---

## Sandbox (double-iframe)

The MCP app HTML is untrusted, so it never runs in the host page. It runs in an **inner** iframe
created and policed by an **outer** sandbox page on a different origin.

- Source: [frontend/src/sandbox.ts](frontend/src/sandbox.ts).
- Build: `npm run build:sandbox` (esbuild) → `backend/wwwroot/sandbox.js`, served alongside
  `backend/wwwroot/sandbox.html` by the backend's `app.UseStaticFiles()`. There is **no separate
  sandbox server** — the cross-origin isolation comes from the sandbox living on the backend origin
  (`:5288`) while the host app is on the frontend origin (`:3000`).

What `sandbox.ts` does inside the outer iframe:

1. Validates `document.referrer` against `^http:\/\/(localhost|127\.0\.0\.1)(:|\/|$)` and pins the
   expected host origin; rejects everything else.
2. Creates the inner iframe with `sandbox="allow-scripts allow-same-origin allow-forms"` (and an
   `allow` attribute derived from requested permissions). Never adds `allow-top-navigation` or
   `allow-popups-to-escape-sandbox`.
3. Posts `ui/notifications/sandbox-proxy-ready` to the host, then relays postMessages both
   directions, validating origin on each hop (host messages against the host origin, inner messages
   against its own origin).
4. On `ui/notifications/sandbox-resource-ready`, injects the app HTML via `document.write()`
   (falling back to `srcdoc`; `document.write` is needed for libraries like CesiumJS).

The outer page's CSP is set by the backend header from the `?csp=` param (see
[backend/McpUiResourceCsp.cs](backend/McpUiResourceCsp.cs)).

---

## Configuration

| Setting | Where | Value |
| --- | --- | --- |
| MCP server endpoint | Aspire service discovery | `services:AgenticTodos-McpServer:https:0` / `…:http:0` → `{base}/mcp` |
| MCP relay path | [backend/Program.cs](backend/Program.cs) | `/agents/mcp-relay` |
| AG-UI endpoint | [backend/AGUIEndpoint.cs](backend/AGUIEndpoint.cs) | `/agents/routed/{alias}/agui` |
| Sandbox URL (host side) | [frontend/.../mcp-app.component.ts](frontend/src/app/mcp-app.component.ts) | `http://localhost:5288/sandbox.html` (dev) |
| Sandbox files | served from | `backend/wwwroot/sandbox.{html,js}` |
| Tool → UI link | MCP server `[McpMeta]` | `"ui"` → `{"resourceUri":"ui://…"}` |
| Resource MIME | MCP server `[McpServerResource]` | `text/html;profile=mcp-app` |
| Activity types | injectors | `mcp-apps`, `eu-ai-act-risk` |

---

## Security considerations

1. **Double-iframe isolation** — the app runs in an inner iframe inside an outer sandbox page on a
   different origin from the host. Never serve the sandbox from the frontend origin.
2. **Origin validation** — `sandbox.ts` validates `document.referrer` and checks the origin on every
   relayed message in both directions.
3. **Inner sandbox attributes** — `allow-scripts allow-same-origin allow-forms` only.
4. **CSP via HTTP header** — set on `/sandbox.html` by the backend from the `?csp=` param, not a
   `<meta>` tag (meta CSP is bypassable by injected content). Per-app domains come from the
   resource's declared `_meta.ui`.
5. **MCP relay** — `/agents/mcp-relay` forwards only to the configured MCP server; it is not an open
   proxy.
6. **Resource MIME validation** — the frontend checks `mimeType === RESOURCE_MIME_TYPE` before
   treating resource content as renderable HTML.

---

## Testing

| File | Covers |
| --- | --- |
| [tests/SseEventInjectionMiddlewareTests.cs](tests/SseEventInjectionMiddlewareTests.cs) | `McpAppsActivityInjector` rewriting; middleware plumbing (eager-error → `RUN_STARTED`+`RUN_ERROR`, body restore) |
| [tests/EUAIActRiskActivityInjectorTests.cs](tests/EUAIActRiskActivityInjectorTests.cs) | EU AI Act injector + composed routing via `ActivitySnapshotInjectionMiddleware.TryInject` |
| [tests/ToolResultNormalizerTests.cs](tests/ToolResultNormalizerTests.cs) | `DetectMcpAppsActivityMiddleware.NormalizeToolResult` shapes |
| [tests/ActivitySnapshotConformanceTests.cs](tests/ActivitySnapshotConformanceTests.cs) | Integration: a full agent run emits `ACTIVITY_SNAPSHOT` (not the raw marker); requires the `AG_UI_ENDPOINT` env var |

---

## Adding a new MCP app

1. **Server:** add an app class under `mcpserver/` with a `[McpServerTool]` carrying
   `[McpMeta("ui", JsonValue = """{"resourceUri":"ui://…"}""")]` and an `[McpServerResource]`
   (`MimeType = "text/html;profile=mcp-app"`) returning the built HTML. Register it with
   `.WithMcpApp<YourApp>()` in [mcpserver/Program.cs](mcpserver/Program.cs).
2. **App HTML:** build a self-contained HTML/JS bundle (use `@modelcontextprotocol/ext-apps` on the
   app side to receive `toolInput`/`toolResult` and call server tools) to the `dist/` path the
   resource reads from. Declare any extra origins it needs in the resource's `_meta.ui` CSP domains.
3. **Frontend:** nothing app-specific is required — any tool whose result carries `ui.resourceUri`
   flows through `DetectMcpAppsActivityMiddleware` → `mcp-apps` snapshot → `<app-mcp-app>`
   automatically.

---

## Related docs

- [sse-event-injection.md](sse-event-injection.md) — the SSE interception mechanism this builds on.
- [README.md](README.md) — project overview and dev setup.
- [AGENTS.md](AGENTS.md) — how to run the app via Aspire.
- [attachments.md](attachments.md) — file upload/attachment handling.
