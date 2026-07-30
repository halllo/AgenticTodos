# AgenticTodos

This experimental application aims to explore the following technologies:

- Microsoft Agent Framework
- AG-UI
- WebMCP
- MCP Apps (`@modelcontextprotocol/ext-apps`)

## Development

The system needs OpenAI and/or Amazon Bedrock credentials. Lets focus on AWS:

```bash
aws iam create-user --user-name agents-experiments
aws iam attach-user-policy --user-name agents-experiments --policy-arn arn:aws:iam::aws:policy/AmazonBedrockFullAccess
aws iam create-access-key --user-name agents-experiments
```

These secrets are managed by dotnet:

```bash
cd backend
dotnet user-secrets set AWSBedrockAccessKeyId ...
dotnet user-secrets set AWSBedrockSecretAccessKey ...
```

Both build steps below run as part of `aspire run` — the `BuildMcpApps` MSBuild target in `mcpserver/AgenticTodos.McpServer.csproj`, and the `dev` npm script Aspire starts the frontend with, which rebuilds `sandbox.js`. By hand:

```bash
# MCP apps (each produces the dist/*.html the MCP server reads at runtime)
for app in get-time-app threejs-app pdf-viewer-app; do (cd mcpserver/$app && npm install && npm run build); done

# Angular sandbox script (produces backend/wwwroot/sandbox.js for the double-iframe MCP app renderer)
cd frontend
npm run build:sandbox
```

Then run it all locally:

```bash
aspire run
```

Test AG-UI against the backend with the CLI:

```bash
cd cli
dotnet run -- agent "Your prompt here"
```

### State round-trip via CLI

AG-UI round-trips arbitrary state between client and server via `STATE_SNAPSHOT` events. `--state` seeds an initial `ConversationState` (`selectedResources` + `counter`); the server injects it as LLM context and echoes it back each turn, the CLI resending the captured snapshot on later turns.

```bash
dotnet run -- agent "What files do I have selected?" \
  --state '{"conversation":{"selectedResources":["readme.md","notes.txt"],"counter":0}}'
```

The `[State: ...]` line after each response shows the current round-tripped state; [`StateSnapshotMiddleware`](backend/StateSnapshotMiddleware.cs) manages it server-side.

### Tests

Plain `dotnet test` is hermetic and free; the tests that cost money or need a running backend are opt-in ([tests/IntegrationFactAttributes.cs](tests/IntegrationFactAttributes.cs)) and otherwise skip. Both, against the Bedrock agent while `aspire run` is up:

```bash
RUN_LIVE_LLM_TESTS=1 AG_UI_ENDPOINT=https://localhost:7038/agents/routed/amazonbedrock/agui dotnet test
```

`RUN_LIVE_LLM_TESTS=1` enables the live-provider tests (each skips again, naming the keys, if its credentials are missing); `AG_UI_ENDPOINT` points the conformance tests at a running endpoint, its alias selecting the agent (`amazonbedrock` or `openai`).

## Problems

### ✅ AmazonBedrockRuntimeClient does not support AdditionalProperties

Amazon Bedrock Runtime client throws this exception, when used with AG-UI:

```log
Amazon.BedrockRuntime.Model.ValidationException: The model returned the following errors: ag_ui_thread_id: Extra inputs are not permitted
---> Amazon.Runtime.Internal.HttpErrorResponseException: Exception of type 'Amazon.Runtime.Internal.HttpErrorResponseException' was thrown.
at Amazon.Runtime.HttpWebRequestMessage.ProcessHttpResponseMessage(HttpResponseMessage responseMessage)
```

[OmitAdditionalPropertiesMiddleware.cs](backend/OmitAdditionalPropertiesMiddleware.cs) strips those `AdditionalProperties` off the chat options, selecting by **value type** — the AG-UI server SDK no longer spreads the request over `ag_ui_*` keys but stashes the whole `RunAgentInput` under one internal key (read it back with `ChatOptions.TryGetRunAgentInput()`), and the middleware deliberately offers no by-name option — that key is an SDK internal, not part of its contract. Two app-internal objects ride on `AdditionalProperties` by the time a request reaches the provider, so `propertyValueTypesToOmit: [typeof(RunAgentInput), typeof(StateSnapshotMiddleware.ConversationState)]`: the AG-UI request itself, and `StateSnapshotMiddleware.ConversationState`, this app's own state, published for tools by [`StateSnapshotMiddleware`](backend/StateSnapshotMiddleware.cs) on the run options and then copied into `ChatOptions.AdditionalProperties` by `ChatClientAgent`. Neither belongs in a model request — though `AWSSDK.Extensions.Bedrock.MEAI` 4.0.101.7 happens not to read `ChatOptions.AdditionalProperties` at all, making this defence-in-depth today rather than load-bearing; it *does* read `AIContent.AdditionalProperties`, a different bag the redacted-thinking section below depends on. Covered by [`OmitAdditionalPropertiesMiddlewareTests`](tests/OmitAdditionalPropertiesMiddlewareTests.cs), and end to end against the live provider by [`AmazonBedrockFieldsTests.AppInternalAdditionalProperties_DoNotReachTheModel()`](./tests/AmazonBedrockFieldsTests.cs) — a `RunAgentInput` still on `AdditionalProperties`, the middleware in the pipeline, and the model **answers**.

### ✅ AG-UI Client does not support Angular

Copilot Kit supports AG-UI very well but requires next.js, and there is currently no functional Angular support. So we glue `@ag-ui/client` together ourselves: [AgentSubscriber](https://docs.ag-ui.com/sdk/js/client/subscriber) handlers implemented directly, mapped back to the angular frontend in [chat.component.ts](frontend/src/app/chat.component.ts). `@ag-ui/core` is deliberately *not* a direct dependency — nothing in the frontend imports it, and `@ag-ui/client` pins it to its own exact version anyway.

`npm audit` in `frontend/` currently reports **6 moderate** advisories, no high or critical ones, none in `@ag-ui/*`: all six are the same `@hono/node-server` path-traversal advisory (`GHSA-frvp-7c67-39w9`), reached through `@modelcontextprotocol/sdk`, which `@mcp-b/global` / `@mcp-b/transports` and `@angular/cli` pull in.

### ✅ AG-UI endpoint mappings do not support per-request agent selection

The official `.MapAGUIServer()` methods (`.MapAGUI()` before 1.15) bind a single `AIAgent` at map time — passed in directly, or resolved from DI by name (the `string agentName` and `IHostedAgentBuilder` overloads do `GetRequiredKeyedService<AIAgent>(name)` right there, then delegate to the first) — and hold it for the endpoint's lifetime. No per-request selection.

I started a PR for a request-level callback allowing deferred agent selection, <https://github.com/microsoft/agent-framework/pull/2343>; there is now another, <https://github.com/microsoft/agent-framework/pull/3162>. Merge hesitancy comes from perceived inconsistency risks regarding A2A.

The workaround in use is a [`HttpContextRoutingAgent`](https://github.com/microsoft/agent-framework/pull/3162#issuecomment-3754459882) ([backend/AGUIEndpoint.cs](backend/AGUIEndpoint.cs)): a stand-in `AIAgent` resolving the real one per request from the route. Not a great solution, but good enough. (An earlier workaround — reimplementing the endpoint over the then-`internal` AG-UI types via reflection — is gone: since 1.15 the protocol and server types are public, in `AGUI.Abstractions` / `AGUI.Server`.)

Three details it has to get right, all because `MapAGUIServer` captures it **once** at map time; the full argument for each override lives with the code, in that file's XML docs on `HttpContextRoutingAgent`, and all three are pinned by [`HttpContextRoutingAgentTests`](tests/HttpContextRoutingAgentTests.cs).

- `IdCore` returns a route-derived `routed-{alias}` — alias-*shaped* values only (`alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')`, else `"routed"`) — because [`FileSystemSessionStore`](backend/FileSystemSessionStore.cs) keys session files by `agent.Id` while the base `AIAgent.Id` is a fresh `Guid` per instance.
- The real agent is resolved **asynchronously**, the in-flight `Task` rather than its result cached in `HttpContext.Items`, so the SDK's several calls per run share one lookup.
- `Name` is overridden because it *is* the DI key the session store is resolved under (`GetKeyedService<AgentSessionStore>(agent.Name)`), **once at map time, from the root provider** — a scoped store fails at startup with *"Cannot resolve scoped service 'AgentSessionStore' from root provider"*. `AddAGUISessionStore()` therefore registers a singleton stand-in under that key, forwarding each call to the current request's container, which leaves the real store free to use any lifetime ([`AguiSessionStoreLifetimeTests`](tests/AguiSessionStoreLifetimeTests.cs)).

Both halves of the session file name (`{agent.Id}_{threadId}`) are `Uri.EscapeDataString`-escaped *and* length-bounded (an over-long half becomes a truncated SHA-256, keeping under the 255-byte component limit) because `RunAgentInput.ThreadId` arrives verbatim off the wire and the resulting `File.Create` failure would be unreportable — it happens in `SaveSessionAsync`, after the SSE response is committed. Why each guard, in the XML docs on [backend/FileSystemSessionStore.cs](backend/FileSystemSessionStore.cs); pinned by [`FileSystemSessionStoreTests`](tests/FileSystemSessionStoreTests.cs).

The `threadId` still comes straight off the wire, so anyone who guesses one resumes that conversation: fine for a local experiment, not for a multi-user deployment, whose hook is `UseClaimsBasedSessionIsolation(...)` — after which the id reaching the store becomes `{key}::{threadId}`.

### ✅ A failing AG-UI request returned an HTTP 500 instead of an error event

The AG-UI server SDK does not translate exceptions into protocol events: an unknown alias or a startup failure came back as an HTTP 500 with a plain-text body, invisible to clients that only read the event stream. [`AguiRunErrorMiddleware`](backend/AguiRunErrorMiddleware.cs) wraps the AG-UI endpoints and emits `RUN_STARTED` + `RUN_ERROR` over SSE instead — only an `AguiClientException`'s own message goes on the wire (an unknown alias is one), every other exception being logged and reported as the generic *"The agent run could not be started."* Contract: [custom-agui-events.md](custom-agui-events.md#errors); covered by [`AguiRunErrorMiddlewareTests`](tests/AguiRunErrorMiddlewareTests.cs).

### ✅ The state snapshot accumulated in the persisted transcript

[`StateSnapshotMiddleware`](backend/StateSnapshotMiddleware.cs) prepends the conversation state as a system message, and sitting above `ChatClientAgent` that message reached `ChatHistoryProvider.InvokedContext.RequestMessages` — so the append-only history store kept one copy per turn, and later turns replayed a dozen *stale* snapshots ahead of the fresh one: the model reading an outdated value and contradicting the live state, the prompt growing without bound.

Fixed by marking it [`TransientChatMessages.AsTransient()`](backend/TransientChatMessages.cs), which [`IOChatHistoryProvider.StoreChatHistoryAsync`](backend/FileSystemChatHistoryProvider.cs) drops. Both halves are pinned separately, either alone being useless: [`StateSnapshotMiddlewareTests`](tests/StateSnapshotMiddlewareTests.cs) that the injected message really is marked, [`ChatHistoryProviderTests`](tests/ChatHistoryProviderTests.cs) that the store drops marked messages and that they do not accumulate across turns.

### ✅ Rendering MCP Apps in the frontend

When the agent calls a tool carrying `ui.resourceUri` metadata in its MCP definition, [`DetectMcpAppsActivityMiddleware`](backend/DetectMcpAppsActivityMiddleware.cs) emits an `McpAppActivityContent`, which `MapClientContent` ([backend/AGUIEndpoint.cs](backend/AGUIEndpoint.cs)) turns into an `ACTIVITY_SNAPSHOT` event carrying `resourceUri`, `toolInput` and `result` — the frontend's view model calling that last field `toolResult` ([mcp-apps.md](mcp-apps.md)). The app's HTML is rendered in a sandboxed double-iframe over the `@modelcontextprotocol/ext-apps` AppBridge protocol.

Untrusted app HTML never runs in the host page: an outer sandbox page served from the **backend** origin (`:5288`) polices an inner iframe holding the app, so the isolation is backend-origin versus frontend-origin (`:3000`). The browser also runs its own MCP client, against the backend's transparent relay at `/agents/mcp-relay`, letting an app call server tools directly. Sandbox attributes, CSP and the relay's constraints: [mcp-apps.md](mcp-apps.md).

![get-time MCP App](mcp-app-get-time.png)

### ✅ Extended thinking (reasoning) support

The Bedrock agent (Claude Sonnet) sets `ChatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full }` in [`backend/Program.cs`](backend/Program.cs). The `AWSSDK.Extensions.Bedrock.MEAI` adapter maps that to `AdditionalModelRequestFields["thinking"] = { type: "enabled", budget_tokens: N }`, the budget derived from `MaxTokens` (`Low`/`Medium`/`High`/`ExtraHigh` ⇒ 25 %/50 %/75 %/100 %) and clamped at both ends: `budget = Math.Max(1024, (int)(MaxTokens * f))`, then `budget = MaxTokens - 1` whenever that reaches or exceeds `MaxTokens`, because Bedrock requires `budget_tokens < max_tokens`. So `ExtraHigh` is in practice `MaxTokens - 1`. The chain-of-thought arrives as `TextReasoningContent`, which the AG-UI server SDK emits as a distinct block of events:

```text
REASONING_START → REASONING_MESSAGE_START (role:"reasoning") → REASONING_MESSAGE_CONTENT* → [REASONING_ENCRYPTED_VALUE] → REASONING_MESSAGE_END → REASONING_END
```

then the usual `TEXT_MESSAGE_*` answer. `gpt-4o` is not a reasoning model, so the OpenAI agent is left without `ReasoningOptions` (`reasoning_effort` would be a 400).

**Gotcha — `ExtraHigh` needs an explicit `MaxTokens`.** Unset, the adapter picks a fixed budget (`ExtraHigh` ⇒ 32768) and auto-raises `MaxTokens` to `budget × 4` = **131072**, past Claude's 128000 output limit: *"The maximum tokens you requested exceeds the model limit of 128000"*. Hence the pinned `MaxOutputTokens = 128000` (`maxOutputTokens:` on `CreateAgent`); the budget is a ceiling, not a reservation, so the answer still gets whatever thinking doesn't consume. (`Low`/`Medium`/`High` stay ≤ 128000 even without the cap.)

The frontend ([`chat.component.ts`](frontend/src/app/chat.component.ts)) subscribes to `@ag-ui/client`'s `onReasoning*` handlers and renders the thought as a collapsible 🧠 "thought process" disclosure — created lazily on the first content delta (so a repeated start or a fully-redacted/empty block never leaves a stray bubble), streamed live, auto-collapsed on completion. The CLI prints the same deltas inline in grey (the `TextReasoningContent` case in [`cli/Verbs/Agent.cs`](cli/Verbs/Agent.cs)), skipping the text-less update `REASONING_ENCRYPTED_VALUE` maps to, which carries only the provider's signature over the thought.

**Gotcha — redacted-thinking persistence.** The AWS adapter stores a `redacted_thinking` payload as a `byte[]` under `AIContent.AdditionalProperties["RedactedContent"]` and rebuilds the outbound block only while that slot is still a `byte[]`; persisted as JSON ([`FileSystemChatHistoryProvider`](backend/FileSystemChatHistoryProvider.cs)) it degrades to a base64 `JsonElement`, which would break every subsequent turn. [`RedactedReasoningNormalizer`](backend/RedactedReasoningNormalizer.cs) restores it on load; normal thinking is unaffected, its signature living in the plain-string `ProtectedData` ([`RedactedReasoningNormalizerTests`](tests/RedactedReasoningNormalizerTests.cs)).

### ✅ Human-in-the-loop tool approval

Tools listed in `HumanInTheLoop:ApprovalRequiredTools` ([`backend/appsettings.json`](backend/appsettings.json)) pause the agent for user approval before executing — an Approve / Always allow / Reject card in the frontend, a `(y)es / (a)lways this tool / (s)ame arguments always / (n)o` prompt in the CLI. "Always allow" records a per-conversation rule (`Microsoft.Agents.AI.ToolApprovalAgent` state in the session), so that tool never prompts again in the same thread.

AG-UI models the pause as an **interrupt**: the run ends with `RUN_FINISHED { outcome: { type: "interrupt", … } }` and the client answers it through the next run's `resume` array. [`ToolApprovalInterruptMiddleware`](backend/ToolApprovalInterruptMiddleware.cs) maps MEAI's `ToolApprovalRequestContent` onto that, the SDK's own mapping being gated on a two-part condition this app does not reliably satisfy ([why](human-in-the-loop.md#why-a-middleware-and-not-just-the-sdk)). Server-side tools only: a client tool's approval request must keep travelling as an ordinary tool call for the client to execute it. Full write-up: [human-in-the-loop.md](human-in-the-loop.md).

**Gotcha — approval replay with append-only history.** `FunctionInvokingChatClient` repairs a resolved approval in memory only, so the append-only [`FileSystemChatHistoryProvider`](backend/FileSystemChatHistoryProvider.cs) persists a request the next load *would* throw on: *"ToolApprovalRequestContent found ... no matching ToolApprovalResponseContent"*. [`ToolApprovalHistoryNormalizer`](backend/ToolApprovalHistoryNormalizer.cs) repairs on load instead, in three ways — [why each](human-in-the-loop.md#history-replay-why-the-normalizer-is-needed), pinned by [`ToolApprovalHistoryNormalizerTests`](tests/ToolApprovalHistoryNormalizerTests.cs).

### ✅ MCP relay under `/agents` returned 405 for GET

The MCP relay shares the `/agents` prefix with the AG-UI endpoint, and the MCP Streamable HTTP transport needs `GET` (SSE) as well as `POST`; registered as a mapped endpoint (`app.Map("/agents/mcp-relay", ...)`) it answered `GET` with 405. Fixed by making the relay a terminal `app.Use()` middleware branch in [`backend/Program.cs`](backend/Program.cs), which short-circuits before endpoint execution. (`MapAGUIViaHttpRoutingAgent()` is not a catch-all — it maps a single `POST /agents/routed/{alias}/agui` — so the two can no longer compete for a path.)

### ❌ AG-UI Client does not support Amazon Bedrock's parallel tool calls

We have one backend and one frontend tool. Amazon Bedrock returns them as parallel tool calls, which AG-UI returns to the client, before it ends the run:

```text/event-stream
data: {"toolCallId":"tooluse_H9k3VU1mQUGwW0yyDbVydA","toolCallName":"get_current_time","parentMessageId":"ff5c53235e0146c1ada1ae3a2965a96c","type":"TOOL_CALL_START"}

data: {"toolCallId":"tooluse_H9k3VU1mQUGwW0yyDbVydA","delta":"null","type":"TOOL_CALL_ARGS"}

data: {"toolCallId":"tooluse_H9k3VU1mQUGwW0yyDbVydA","type":"TOOL_CALL_END"}

data: {"toolCallId":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q","toolCallName":"change_background_color","parentMessageId":"ff5c53235e0146c1ada1ae3a2965a96c","type":"TOOL_CALL_START"}

data: {"toolCallId":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q","delta":"{\u0022color\u0022:\u0022green\u0022}","type":"TOOL_CALL_ARGS"}

data: {"toolCallId":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q","type":"TOOL_CALL_END"}

data: {"threadId":"84fdb1c8-9c1b-496a-9e7e-cd2648983b28","runId":"d2baec31-9059-4348-9947-2de9721b2cea","result":null,"type":"RUN_FINISHED"}
```

The frontend can only handle one tool, so it creates a single tool result message for one tool call:

```json
[{"id":"ff5c53235e0146c1ada1ae3a2965a96c","role":"assistant","toolCalls":[
  {"id":"tooluse_H9k3VU1mQUGwW0yyDbVydA","type":"function","function":{"name":"get_current_time","arguments":"null"}},
  {"id":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q","type":"function","function":{"name":"change_background_color","arguments":"{\"color\":\"green\"}"}}]},
 {"id":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q","role":"tool","content":"\"Success: Function completed.\"","toolCallId":"tooluse_a1WlhjrIQbqVAKrb1oQo0Q"}]
```

This then fails Amazon Bedrock validation, because the second tool result is missing:

```text/event-stream
data: {"message":"Expected toolResult blocks at messages.4.content for the following Ids: tooluse_H9k3VU1mQUGwW0yyDbVydA","code":"StreamingError","type":"RUN_ERROR"}
```

![parallel tool calls fail](parallel-tool-calls-fail.png)

OpenAI returns tool calls sequentually, which works fine. How can we make Amazon Bedrock return tool calls sequentually and not in parallel?
