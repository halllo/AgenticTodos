# MCP Apps Implementation Plan for AG-UI Projects

This document describes everything needed to add MCP app rendering support to an existing AG-UI project. It covers the backend (.NET/C#), frontend (TypeScript/Angular), sandbox iframe, and MCP server side.

---

## Overview

MCP apps are interactive UI components served by an MCP server as resources. When an agent calls a tool that has `ui.resourceUri` metadata, the backend emits a custom `ACTIVITY_SNAPSHOT` AG-UI event instead of a plain `TOOL_CALL_RESULT`. The frontend receives this event, reads the HTML from the MCP server, and renders it inside a double-iframe sandbox.

**Full flow:**

```
User prompt
  → Agent calls MCP tool (e.g. get_time)
  → Tool has ui.resourceUri metadata
  → DetectMcpAppsActivityMiddleware emits mcp-activity DataContent
  → SseEventInjectionMiddleware + McpAppsActivityInjector intercepts SSE stream
  → Replaces TEXT_MESSAGE_CONTENT with ACTIVITY_SNAPSHOT event
  → Frontend onActivitySnapshotEvent handler receives event
  → McpAppComponent fetches HTML via /agents/mcp-relay
  → Renders HTML in double-iframe sandbox
  → App receives toolInput + toolResult via AppBridge/postMessage
```

---

## 1. Backend Implementation (C# / ASP.NET Core)

### 1.1 Dependencies

Add to your backend `.csproj`:

```xml
<PackageReference Include="Microsoft.Agents.AI.AGUI" Version="1.4.0-preview.260505.1" />
<PackageReference Include="Microsoft.Agents.AI.Hosting.AGUI.AspNetCore" Version="1.4.0-preview.260505.1" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.2" />
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
```

### 1.2 DetectMcpAppsActivityMiddleware

This agent-level middleware intercepts tool results and, if the tool has `ui.resourceUri` in its MCP metadata, emits a marker that will later be transformed into an `ACTIVITY_SNAPSHOT` event.

Create file: `DetectMcpAppsActivityMiddleware.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// Agent middleware that detects MCP tools with ui.resourceUri metadata and emits
/// mcp-activity markers after each matching tool result.
/// </summary>
public class DetectMcpAppsActivityMiddleware : AIMiddleware
{
    public static AIAgentBuilder UseDetectMcpAppsActivity(AIAgentBuilder builder)
        => builder.UseMiddleware<DetectMcpAppsActivityMiddleware>();

    public override async IAsyncEnumerable<ChatResponseUpdate> InvokeStreamingAsync(
        IList<ChatMessage> messages,
        ChatOptions? chatOptions,
        Func<IList<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>> next,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Map call IDs to (toolName, toolArgs JSON)
        var pendingCalls = new Dictionary<string, (string Name, string Args)>();

        await foreach (var update in next(messages, chatOptions).WithCancellation(cancellationToken))
        {
            // Track function call starts
            foreach (var content in update.Contents.OfType<FunctionCallContent>())
            {
                var argsJson = content.Arguments != null
                    ? JsonSerializer.Serialize(content.Arguments)
                    : "{}";
                pendingCalls[content.CallId] = (content.Name, argsJson);
            }

            yield return update;

            // After each tool result, check for MCP apps metadata
            foreach (var result in update.Contents.OfType<FunctionResultContent>())
            {
                if (!pendingCalls.TryGetValue(result.CallId, out var call))
                    continue;

                // Find the matching MCP tool
                var tools = chatOptions?.ChatOptions?.Tools?.OfType<McpClientTool>() ?? [];
                var matchedTool = tools.FirstOrDefault(t => t.Name == call.Name);
                var resourceUri = matchedTool?.ProtocolTool?.Meta?["ui"]?["resourceUri"]?.ToString();

                if (resourceUri == null)
                    continue;

                // Generate a unique message ID for this activity
                var messageId = Guid.NewGuid().ToString("N");

                // Normalize result to MCP CallToolResult shape
                var rawResult = result.Result?.ToString() ?? "";
                var normalizedResult = NormalizeToolResult(rawResult);

                // Deserialize toolInput args
                JsonNode? toolInputNode = null;
                try { toolInputNode = JsonNode.Parse(call.Args); } catch { }

                // Build mcp-activity marker
                var marker = new JsonObject
                {
                    ["type"] = "mcp-activity",
                    ["messageId"] = messageId,
                    ["resourceUri"] = resourceUri,
                    ["result"] = JsonNode.Parse(normalizedResult),
                    ["toolInput"] = toolInputNode ?? new JsonObject()
                };

                // Emit as DataContent with special MIME type
                // The AGUI framework will emit this as a TEXT_MESSAGE_CONTENT event
                // where SseEventInjectionMiddleware will intercept and transform it
                yield return new ChatResponseUpdate
                {
                    Contents = [new DataContent(
                        marker.ToJsonString(),
                        "application/x-mcp-activity")]
                };

                pendingCalls.Remove(result.CallId);
            }
        }
    }

    internal static string NormalizeToolResult(string raw)
    {
        // If already in MCP shape {"content":[...]}, pass through
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is JsonObject obj && obj["content"] is JsonArray)
                return raw;
        }
        catch { }

        // Wrap plain text in MCP content array
        return JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = raw } }
        });
    }
}
```

### 1.3 SseEventInjectionMiddleware

Generic ASP.NET Core middleware that intercepts the SSE response body, buffers complete events, and passes each one through an injector function.

Create file: `SseEventInjectionMiddleware.cs`

```csharp
using System.Text;

/// <summary>
/// ASP.NET Core middleware that wraps the response body to intercept complete SSE events
/// and optionally replace/suppress them via an injector function.
/// 
/// The injector receives the raw event bytes and returns:
///   null              → suppress the event
///   empty collection  → forward unchanged
///   non-empty         → replace with these events
/// </summary>
public class SseEventInjectionMiddleware(
    RequestDelegate next,
    Func<string, IEnumerable<string>?> injector)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var interceptor = new SseInterceptorStream(originalBody, injector);
        context.Response.Body = interceptor;

        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            // Emit error as SSE event before the stream closes
            context.Response.Body = originalBody;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            var errorEvent = $"data: {{\"type\":\"RUN_ERROR\",\"message\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}\n\n";
            await originalBody.WriteAsync(Encoding.UTF8.GetBytes(errorEvent));
            return;
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        // Flush any remaining buffered bytes
        await interceptor.FlushRemainingAsync();
    }
}

internal class SseInterceptorStream(Stream inner, Func<string, IEnumerable<string>?> injector) : Stream
{
    private readonly List<byte> _buffer = [];

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        _buffer.AddRange(buffer[offset..(offset + count)]);
        await ProcessBufferAsync(ct);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _buffer.AddRange(buffer.ToArray());
        await ProcessBufferAsync(ct);
    }

    private async Task ProcessBufferAsync(CancellationToken ct)
    {
        // SSE events are delimited by \n\n
        while (true)
        {
            var bytes = _buffer.ToArray();
            var str = Encoding.UTF8.GetString(bytes);
            var idx = str.IndexOf("\n\n", StringComparison.Ordinal);
            if (idx < 0) break;

            var eventText = str[..(idx + 2)];
            _buffer.RemoveRange(0, Encoding.UTF8.GetByteCount(eventText));

            var replacement = injector(eventText);
            if (replacement == null)
            {
                // Suppress
            }
            else if (!replacement.Any())
            {
                // Forward unchanged
                await inner.WriteAsync(Encoding.UTF8.GetBytes(eventText), ct);
            }
            else
            {
                // Write replacements
                foreach (var r in replacement)
                    await inner.WriteAsync(Encoding.UTF8.GetBytes(r), ct);
            }
        }
    }

    public async Task FlushRemainingAsync()
    {
        if (_buffer.Count > 0)
        {
            var remaining = _buffer.ToArray();
            _buffer.Clear();
            await inner.WriteAsync(remaining);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
```

### 1.4 McpAppsActivityInjector

Stateless function that transforms `TEXT_MESSAGE_CONTENT` events carrying `mcp-activity` markers into `ACTIVITY_SNAPSHOT` events.

Create file: `McpAppsActivityInjector.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

public static class McpAppsActivityInjector
{
    /// <summary>
    /// Injector function for SseEventInjectionMiddleware.
    /// Returns null to suppress, empty to forward, or replacement events.
    /// </summary>
    public static IEnumerable<string>? TryInjectActivitySnapshot(string sseEvent)
    {
        // Only process data lines
        if (!sseEvent.TrimStart().StartsWith("data:"))
            return [];

        var dataLine = sseEvent.Split('\n')
            .FirstOrDefault(l => l.StartsWith("data:"));
        if (dataLine == null) return [];

        var json = dataLine["data:".Length..].Trim();

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return []; }

        // Must be TEXT_MESSAGE_CONTENT with a delta containing type: "mcp-activity"
        if (node?["type"]?.GetValue<string>() != "TEXT_MESSAGE_CONTENT")
            return [];

        var delta = node["delta"]?.GetValue<string>();
        if (delta == null) return [];

        JsonNode? deltaNode;
        try { deltaNode = JsonNode.Parse(delta); }
        catch { return []; }

        if (deltaNode?["type"]?.GetValue<string>() != "mcp-activity")
            return [];

        // Build ACTIVITY_SNAPSHOT replacement
        var activitySnapshot = new JsonObject
        {
            ["type"] = "ACTIVITY_SNAPSHOT",
            ["messageId"] = deltaNode["messageId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            ["activityType"] = "mcp-apps",
            ["replace"] = true,
            ["content"] = new JsonObject
            {
                ["resourceUri"] = deltaNode["resourceUri"]?.DeepClone(),
                ["result"] = deltaNode["result"]?.DeepClone(),
                ["toolInput"] = deltaNode["toolInput"]?.DeepClone()
            }
        };

        return [$"data: {activitySnapshot.ToJsonString()}\n\n"];
    }
}
```

### 1.5 Program.cs Wiring

In your `Program.cs` (or `Startup.cs`), add:

```csharp
// 1. Register the agent middleware on your agent builder
agentBuilder.UseDetectMcpAppsActivity();  // from DetectMcpAppsActivityMiddleware

// 2. Add the SSE injection middleware on the AGUI endpoint path
//    (run BEFORE app.MapAgentRoutes() or equivalent)
app.UseWhen(
    ctx => ctx.Request.Path.Value?.EndsWith("/agui", StringComparison.OrdinalIgnoreCase) == true,
    branch => branch.UseMiddleware<SseEventInjectionMiddleware>(
        (Func<string, IEnumerable<string>?>)McpAppsActivityInjector.TryInjectActivitySnapshot
    )
);

// 3. Add MCP relay proxy - forwards /agents/mcp-relay/** to the real MCP server
app.Use(async (HttpContext ctx, RequestDelegate next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/agents/mcp-relay"))
    {
        await next(ctx);
        return;
    }

    var mcpServerUrl = builder.Configuration["McpServerUrl"] ?? "http://localhost:5100";
    var suffix = ctx.Request.Path.Value!.Replace("/agents/mcp-relay", "");
    var targetUrl = $"{mcpServerUrl.TrimEnd('/')}{suffix}{ctx.Request.QueryString}";

    using var httpClient = new HttpClient();
    var forward = new HttpRequestMessage(
        new HttpMethod(ctx.Request.Method),
        targetUrl);

    // Copy request body
    if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
    {
        forward.Content = new StreamContent(ctx.Request.Body);
        if (ctx.Request.ContentType != null)
            forward.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
    }

    // Copy request headers
    foreach (var header in ctx.Request.Headers)
    {
        if (!forward.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value))
            forward.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
    }

    var response = await httpClient.SendAsync(
        forward,
        HttpCompletionOption.ResponseHeadersRead,
        ctx.RequestAborted);

    ctx.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers.Concat(response.Content.Headers))
    {
        if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            ctx.Response.Headers[header.Key] = header.Value.ToArray();
    }

    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});
```

---

## 2. MCP Server: Tool Metadata

For a tool to trigger MCP app rendering, it must have `ui.resourceUri` in its MCP metadata pointing to the HTML resource.

```csharp
[McpServerTool, Description("Gets the current time.")]
[McpMeta("ui", JsonValue = """{"resourceUri":"ui://my-app.html"}""")]
public IEnumerable<ContentBlock> GetTime()
{
    var now = DateTime.Now.ToString("T");
    return [new TextContentBlock { Text = now }];
}

// Register the HTML as an MCP resource
[McpServerResource(UriTemplate = "ui://my-app.html", MimeType = "text/html")]
public string GetAppResource() => File.ReadAllText("my-app.html");
```

The resource URI scheme (`ui://`) is a convention - the actual URI is whatever the MCP server registers. The frontend uses it to call `readResource({ uri })` on the MCP client.

---

## 3. Frontend Implementation (TypeScript/Angular)

### 3.1 Dependencies

Add to `package.json`:

```json
{
  "@ag-ui/client": "^0.0.53",
  "@ag-ui/core": "^0.0.53",
  "@mcp-b/global": "^2.3.1",
  "@mcp-b/transports": "^2.3.1",
  "@modelcontextprotocol/ext-apps": "^1.7.1"
}
```

### 3.2 McpClientService

Singleton Angular service that creates and maintains an MCP client connection to `/agents/mcp-relay`.

Create file: `mcp-client.service.ts`

```typescript
import { Injectable } from '@angular/core';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';
import type { Tool, Resource } from '@modelcontextprotocol/sdk/types.js';

export interface McpServerInfo {
  client: Client;
  tools: Map<string, Tool>;
  resources: Map<string, Resource>;
  appHtmlCache: Map<string, string>;
}

@Injectable({ providedIn: 'root' })
export class McpClientService {
  private serverInfoPromise: Promise<McpServerInfo> | null = null;

  getServerInfo(): Promise<McpServerInfo> {
    if (!this.serverInfoPromise) {
      this.serverInfoPromise = this.connect();
    }
    return this.serverInfoPromise;
  }

  private async connect(): Promise<McpServerInfo> {
    const url = new URL('/agents/mcp-relay', window.location.href);

    // Try Streamable HTTP first (Firefox-compatible), fall back to SSE
    for (const Transport of [StreamableHTTPClientTransport, SSEClientTransport]) {
      try {
        const client = new Client({ name: 'MyApp', version: '1.0.0' });
        await client.connect(new (Transport as any)(url));
        return await this.buildInfo(client);
      } catch {
        // try next transport
      }
    }
    throw new Error('Failed to connect to MCP server via any transport');
  }

  private async buildInfo(client: Client): Promise<McpServerInfo> {
    const [toolsResult, resourcesResult] = await Promise.all([
      client.listTools(),
      client.listResources(),
    ]);

    return {
      client,
      tools: new Map(toolsResult.tools.map(t => [t.name, t])),
      resources: new Map(resourcesResult.resources.map(r => [r.uri, r])),
      appHtmlCache: new Map(),
    };
  }
}
```

### 3.3 McpAppComponent

Angular component that renders an MCP app in a double-iframe sandbox.

Create file: `mcp-app.component.ts`

```typescript
import {
  Component, input, ElementRef, ViewChild,
  AfterViewInit, inject, signal
} from '@angular/core';
import { McpClientService } from './mcp-client.service';
import {
  AppBridge, PostMessageTransport, loadSandboxProxy,
  RESOURCE_MIME_TYPE
} from '@modelcontextprotocol/ext-apps';
import type { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import { HOST_STYLES } from './host-styles'; // see section 3.5

const SANDBOX_URL = 'http://localhost:5288/sandbox.html'; // adjust to your sandbox server
const SANDBOX_READY_TIMEOUT_MS = 10_000;

@Component({
  selector: 'app-mcp-app',
  standalone: true,
  template: `
    <iframe
      #iframeEl
      style="width:100%;border:none;display:block;"
      [style.height.px]="height()"
    ></iframe>
    @if (error()) {
      <div class="error">{{ error() }}</div>
    }
  `
})
export class McpAppComponent implements AfterViewInit {
  @ViewChild('iframeEl') iframeRef!: ElementRef<HTMLIFrameElement>;

  resourceUri = input.required<string>();
  toolInput   = input<Record<string, unknown>>({});
  toolResult  = input<unknown>(null);

  height = signal(200);
  error  = signal<string | null>(null);

  private mcpClientService = inject(McpClientService);

  async ngAfterViewInit() {
    const iframe = this.iframeRef.nativeElement;
    try {
      const serverInfo = await this.mcpClientService.getServerInfo();

      // Read the app HTML resource from the MCP server
      let html = serverInfo.appHtmlCache.get(this.resourceUri());
      if (!html) {
        const resource = await serverInfo.client.readResource({ uri: this.resourceUri() });
        const content = resource.contents[0];
        if (content.mimeType !== RESOURCE_MIME_TYPE) {
          throw new Error(`Unexpected MIME type: ${content.mimeType}`);
        }
        html = typeof content.text === 'string' ? content.text : '';
        serverInfo.appHtmlCache.set(this.resourceUri(), html);
      }

      // Configure CSP and permissions (adjust as needed for your app)
      const csp = [
        "default-src 'none'",
        "script-src 'unsafe-inline'",
        "style-src 'unsafe-inline'",
        "connect-src 'self'",
      ].join('; ');
      const permissions: string[] = []; // e.g. ['camera', 'microphone']

      // Load the outer sandbox iframe and wait for it to signal ready
      const loaded = await loadSandboxProxy(
        iframe,
        SANDBOX_URL,
        csp,
        permissions,
        SANDBOX_READY_TIMEOUT_MS
      );

      if (!loaded) throw new Error('Sandbox failed to initialize');

      // Create AppBridge for bidirectional communication
      const appBridge = new AppBridge(serverInfo.client, {
        hostStyles: HOST_STYLES,
        onHeightChange: (h: number) => this.height.set(h),
      });

      await appBridge.connect(
        new PostMessageTransport(iframe.contentWindow!)
      );

      // Send app HTML into the inner iframe via the sandbox relay
      await appBridge.sendSandboxResourceReady({ html, csp, permissions });

      // Pass tool input and result to the app
      appBridge.sendToolInput({ arguments: this.toolInput() });

      const result = this.toolResult();
      if (result != null) {
        appBridge.sendToolResult(result as CallToolResult);
      }
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : String(e));
    }
  }
}
```

### 3.4 ACTIVITY_SNAPSHOT Event Handling in Chat Component

In your existing chat component where you handle AG-UI events, add a handler for `ACTIVITY_SNAPSHOT`. The AG-UI client library fires `onActivitySnapshotEvent` for events with `type: "ACTIVITY_SNAPSHOT"`.

```typescript
// In your MessageViewModel or equivalent interface:
interface MessageViewModel {
  role: 'user' | 'assistant' | 'tool' | 'activity';
  content: string;
  activityType?: string;
  resourceUri?: string;
  toolInput?: Record<string, unknown>;
  toolResult?: unknown;
  messageId?: string;
}

// In your agent run configuration:
const runConfig = {
  // ... existing event handlers ...

  onActivitySnapshotEvent: ({ event }: { event: ActivitySnapshotEvent }) => {
    const content = event.content as Record<string, unknown>;
    const resourceUri = typeof content?.['resourceUri'] === 'string'
      ? content['resourceUri']
      : undefined;

    const vm: MessageViewModel = {
      role: 'activity',
      content: JSON.stringify(content, null, 2),
      activityType: event.activityType,
      resourceUri,
      messageId: event.messageId,
      toolInput: content?.['toolInput'] as Record<string, unknown> | undefined,
      toolResult: content?.['result'],
    };

    this.messages.push(vm);
  },
};
```

In your chat template:

```html
@for (message of messages; track $index) {
  @if (message.role === 'activity' && message.activityType === 'mcp-apps') {
    <app-mcp-app
      [resourceUri]="message.resourceUri!"
      [toolInput]="message.toolInput ?? {}"
      [toolResult]="message.toolResult"
    />
  } @else {
    <!-- existing message rendering -->
  }
}
```

### 3.5 Host Styles

Create `host-styles.ts` with CSS custom properties that get passed to MCP apps for consistent theming:

```typescript
export const HOST_STYLES: Record<string, string> = {
  '--color-background': 'light-dark(#ffffff, #1a1a1a)',
  '--color-background-subtle': 'light-dark(#f5f5f5, #2a2a2a)',
  '--color-text-primary': 'light-dark(#111111, #f0f0f0)',
  '--color-text-secondary': 'light-dark(#666666, #a0a0a0)',
  '--color-border': 'light-dark(#e0e0e0, #3a3a3a)',
  '--color-accent': '#0070f3',
  '--font-sans': 'system-ui, -apple-system, sans-serif',
  '--font-mono': 'ui-monospace, monospace',
  '--border-radius-sm': '4px',
  '--border-radius-md': '8px',
  '--border-radius-lg': '12px',
  // Add more as needed
};
```

---

## 4. Sandbox Server and sandbox.ts

The outer sandbox iframe is served from a **separate origin** (e.g., `http://localhost:5288`). This provides cross-origin isolation.

### 4.1 What the sandbox server needs to serve

The sandbox server must serve two files with strict HTTP headers:
- `/sandbox.html` — HTML shell that loads `sandbox.js`
- `/sandbox.js` — The compiled relay script (compiled from `sandbox.ts`)

**Required HTTP headers on `sandbox.html`:**
```
Content-Security-Policy: default-src 'none'; script-src 'self'; frame-src 'none';
X-Frame-Options: ALLOWALL
```

(The CSP here is for the outer sandbox page itself — the inner iframe gets its own CSP set at the point of injection.)

### 4.2 sandbox.ts

This script runs inside the outer iframe. It:
1. Validates the embedding host origin
2. Creates an inner iframe with strict sandbox attributes
3. Relays postMessage bidirectionally between host and inner iframe
4. Handles `ui/notifications/sandbox-resource-ready` by injecting HTML into the inner iframe

```typescript
// sandbox.ts - compiled to sandbox.js and served from the sandbox origin

const ALLOWED_REFERRER_PATTERN = /^https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?(\/|$)/;

let expectedHostOrigin: string | null = null;
let innerIframe: HTMLIFrameElement | null = null;

function init() {
  // Validate the embedding host
  if (!document.referrer || !ALLOWED_REFERRER_PATTERN.test(document.referrer)) {
    console.error('[sandbox] Embedding domain not allowed:', document.referrer);
    return;
  }

  const hostUrl = new URL(document.referrer);
  expectedHostOrigin = hostUrl.origin;

  // Create inner iframe
  innerIframe = document.createElement('iframe');
  innerIframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms');
  innerIframe.style.cssText = 'width:100%;height:100%;border:none;display:block;';
  document.body.style.cssText = 'margin:0;padding:0;overflow:hidden;';
  document.body.appendChild(innerIframe);

  // Listen for messages from both host and inner iframe
  window.addEventListener('message', handleMessage);

  // Notify host that sandbox is ready
  window.parent.postMessage(
    { jsonrpc: '2.0', method: 'ui/notifications/sandbox-proxy-ready', params: {} },
    expectedHostOrigin
  );
}

async function handleMessage(event: MessageEvent) {
  if (!expectedHostOrigin || !innerIframe) return;

  if (event.source === window.parent) {
    // Message from host → relay to inner iframe (or handle special messages)
    if (event.origin !== expectedHostOrigin) return;

    const data = event.data;
    if (data?.method === 'ui/notifications/sandbox-resource-ready') {
      // Special: inject HTML into inner iframe
      const { html, csp, permissions } = data.params ?? {};

      // Apply permissions via allow attribute
      if (permissions?.length) {
        innerIframe.setAttribute('allow', permissions.join('; '));
      }

      // Write HTML into inner iframe
      const doc = innerIframe.contentDocument
        ?? innerIframe.contentWindow?.document;
      if (doc) {
        doc.open();
        doc.write(html ?? '');
        doc.close();
      } else {
        // Fallback: srcdoc
        innerIframe.srcdoc = html ?? '';
      }
    } else {
      // Relay other messages to inner iframe
      innerIframe.contentWindow?.postMessage(data, '*');
    }
  } else if (event.source === innerIframe.contentWindow) {
    // Message from inner iframe → relay to host
    window.parent.postMessage(event.data, expectedHostOrigin);
  }
}

init();
```

Compile with esbuild or vite:
```bash
esbuild src/sandbox.ts --bundle --outfile=wwwroot/sandbox.js --format=esm
```

### 4.3 sandbox.html

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <title>Sandbox</title>
</head>
<body>
  <script type="module" src="/sandbox.js"></script>
</body>
</html>
```

**Important:** The CSP on this page must be set via HTTP headers (not `<meta>` tags) to be tamper-proof.

---

## 5. MCP App Development (app side)

MCP apps are plain HTML files with JavaScript that use the `@modelcontextprotocol/ext-apps` library to receive tool inputs and results.

### 5.1 Example MCP App (get-time app)

```typescript
// get-time.ts
import { createApp, type CallToolResult } from '@modelcontextprotocol/ext-apps';

const app = createApp();

// Called when the host sends tool result data
app.onToolResult((result: CallToolResult) => {
  const text = result.content
    .filter(c => c.type === 'text')
    .map(c => c.text)
    .join('');
  document.getElementById('time')!.textContent = text;
});

// Call a server tool on demand
document.getElementById('refresh')?.addEventListener('click', async () => {
  const result = await app.callServerTool('get_time', {});
  // onToolResult will be called automatically
});
```

```html
<!-- get-time.html (compiled output from get-time.ts) -->
<!DOCTYPE html>
<html>
<head>
  <style>
    body { font-family: var(--font-sans, sans-serif); }
    /* Use host CSS variables for theming */
  </style>
</head>
<body>
  <p>Current time: <span id="time">...</span></p>
  <button id="refresh">Refresh</button>
  <script type="module" src="./get-time.js"></script>
</body>
</html>
```

---

## 6. Configuration Summary

| Setting | Where | Example Value |
|---|---|---|
| MCP server URL | `appsettings.json` or env | `http://localhost:5100` |
| Sandbox server URL | Frontend constant in `McpAppComponent` | `http://localhost:5288` |
| AGUI endpoint path | `Program.cs` middleware condition | `/agui` |
| MCP relay path | `Program.cs` middleware | `/agents/mcp-relay` |
| Tool metadata key | MCP server `[McpMeta]` attribute | `"ui"` → `{"resourceUri":"..."}` |
| Activity type | `McpAppsActivityInjector` | `"mcp-apps"` |

---

## 7. Security Considerations

1. **Double-iframe isolation**: The outer sandbox runs at a different origin to isolate the MCP app from the host. Never serve both from the same origin.

2. **Origin validation**: `sandbox.ts` must validate `document.referrer` against an allowlist before accepting any messages or relaying content.

3. **Sandbox attribute**: The inner iframe must have `sandbox="allow-scripts allow-same-origin allow-forms"`. Do not add `allow-top-navigation` or `allow-popups-to-escape-sandbox`.

4. **CSP enforcement**: CSP must be set via HTTP response headers on `sandbox.html`, not `<meta>` tags. Meta-tag CSP can be bypassed by injected content.

5. **MCP relay**: The `/agents/mcp-relay` endpoint should only forward to your trusted MCP server. Do not expose it as an open proxy.

6. **Resource MIME type validation**: Always validate the `mimeType` of the MCP resource before rendering it as HTML. The `@modelcontextprotocol/ext-apps` library exports `RESOURCE_MIME_TYPE` for this.

---

## 8. Testing

### Backend Unit Tests

Test `McpAppsActivityInjector.TryInjectActivitySnapshot`:
- Input: `TEXT_MESSAGE_CONTENT` event with `mcp-activity` delta → returns `ACTIVITY_SNAPSHOT`
- Input: `TEXT_MESSAGE_CONTENT` with non-mcp delta → returns empty (forward)
- Input: other event types → returns empty (forward)
- Input: malformed JSON → returns empty (forward)
- Input: `mcp-activity` marker → result is suppressed (returns null)

Test `SseEventInjectionMiddleware`:
- Events buffered across multiple `WriteAsync` calls
- Multi-event SSE streams handled correctly
- Early error exceptions converted to `RUN_ERROR` SSE events

### Integration Tests

Verify that a full agent run with an MCP-tool-calling agent:
1. Emits `TOOL_CALL_START/ARGS/END` events
2. Emits `ACTIVITY_SNAPSHOT` (not `TOOL_CALL_RESULT`) for tools with `ui.resourceUri`
3. Does NOT emit the internal `mcp-activity` TEXT_MESSAGE_CONTENT to clients

### Frontend E2E Tests

- McpAppComponent renders in a double iframe
- Tool result is displayed after AppBridge delivers it
- Clicking "refresh" calls back to the MCP server via the relay

---

## 9. Order of Implementation

1. **Add NuGet packages** to backend `.csproj`
2. **Create `DetectMcpAppsActivityMiddleware.cs`** and wire with `.UseDetectMcpAppsActivity()` on agent builder
3. **Create `SseEventInjectionMiddleware.cs`** and `SseInterceptorStream`
4. **Create `McpAppsActivityInjector.cs`**
5. **Wire middleware and MCP relay in `Program.cs`**
6. **Add `ui.resourceUri` metadata** to the relevant MCP server tools and register the HTML resource
7. **Add npm packages** to frontend
8. **Create `mcp-client.service.ts`**
9. **Create `mcp-app.component.ts`** and `host-styles.ts`
10. **Add `ACTIVITY_SNAPSHOT` handler** in chat component and update template
11. **Write and compile `sandbox.ts`** → `sandbox.js`
12. **Serve `sandbox.html` + `sandbox.js`** from a separate origin with correct CSP headers
13. **Write and compile MCP app HTML** (e.g., `get-time.ts`) and register as MCP resource
14. **Test end-to-end**
