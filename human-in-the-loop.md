# Human-in-the-Loop Tool Approval

Pauses the agent before executing selected tools, asks the user for approval in the client (Approve / Always allow / Reject), and resumes the run with the decision. Follows the [Microsoft Agent Framework AG-UI HITL pattern](https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/human-in-the-loop?pivots=programming-language-csharp), adapted to the current package versions.

## Why a bridge is needed

The MEAI foundation already does the heavy lifting: tools wrapped in `ApprovalRequiredAIFunction` are not invoked by `FunctionInvokingChatClient` (FICC, inside `ChatClientAgent`); instead FICC emits a `ToolApprovalRequestContent` and ends the run. A later run containing a matching `ToolApprovalResponseContent` executes (or rejects) the tool and continues — FICC recreates the `tool_use`/`tool_result` pairing itself, so even Amazon Bedrock's strict validation passes.

But the AG-UI hosting layer (`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 1.13.0-preview) only serializes Text/FunctionCall/FunctionResult/Reasoning/Data content — **approval content is silently dropped**. `ToolApprovalBridgeMiddleware` therefore translates approval content into a synthetic client tool call named `request_approval`, which streams as ordinary `TOOL_CALL_START/ARGS/END` events and ends the run exactly like the existing WebMCP frontend tools do.

Note: the Microsoft docs still use the pre-10.4 type names (`FunctionApprovalRequestContent`/`FunctionApprovalResponseContent`); MEAI 10.7 renamed them to `ToolApprovalRequestContent`/`ToolApprovalResponseContent`.

## Files

| File | Role |
|---|---|
| `backend/ToolApprovalBridgeMiddleware.cs` | Agent-level middleware: outbound converts `ToolApprovalRequestContent` → synthetic `request_approval` `FunctionCallContent`; inbound converts the client's tool result back into `ToolApprovalResponseContent` (or `AlwaysApproveToolApprovalResponseContent` for "always allow") and strips re-sent synthetic calls |
| `backend/Program.cs` | Pipeline: `UseToolApprovalBridge()` (outer) → `UseToolApproval(...)` (inner); `EnableNonApprovalRequiredFunctionBypassing = true` on `ChatClientAgentOptions`; config-driven `ApprovalRequiredAIFunction` wrapping in `GetTools` |
| `backend/appsettings.json` | `HumanInTheLoop:ApprovalRequiredTools` — names of gated tools (local functions and MCP tools alike) |
| `backend/ToolApprovalHistoryNormalizer.cs` | Repairs persisted approval content on history load (scrubs completed pairs, auto-rejects orphaned requests) — required because the history store is append-only, see [History replay](#history-replay-why-the-normalizer-is-needed) |
| `backend/DetectMcpAppsActivityMiddleware.cs` | Uses `GetService<McpClientTool>()` instead of `OfType` so MCP-apps rendering still works when an MCP tool is wrapped for approval |
| `frontend/src/app/chat.component.ts` | Approval card (role `'approval'`) with Approve / Always allow / Reject; pauses on run finish, resumes via the same `addMessages` + re-run mechanism as WebMCP tools |
| `cli/Verbs/Agent.cs` | Console `(y)es / (a)lways allow / (n)o` prompt, re-runs until no approval requests remain |
| `tests/ToolApprovalBridgeMiddlewareTests.cs` | Unit tests for both conversion directions and the wire contract |
| `tests/ToolApprovalHistoryNormalizerTests.cs` | Unit tests for the history repairs (scrubbing, orphan rejection, idempotency) |

## Layering

```
CreateAgent pipeline (outer → inner)
  UseOpenTelemetry
► UseToolApprovalBridge()        wire ⇄ MEAI translation (this doc)
► UseToolApproval(options)       Microsoft.Agents.AI.ToolApprovalAgent:
                                 "always allow" rules + one-at-a-time queueing,
                                 persisted as ToolApprovalState in the AgentSession
  AttachmentResolution / OmitEmptySystem / StateSnapshot / DetectMcpApps / EUAIAct…
  ChatClientAgent (EnableNonApprovalRequiredFunctionBypassing = true)
    ├ FileSystemChatHistoryProvider   (persists approval request/response content, append-only;
    │                                  runs ToolApprovalHistoryNormalizer on every load)
    └ FunctionInvokingChatClient      (ApprovalRequiredAIFunction handling)
```

- **`ToolApprovalAgent`** (`UseToolApproval`): when the user answers "always allow", it records a `ToolApprovalRule` in the session's `ToolApprovalState` (persisted by `FileSystemSessionStore`), so future calls to that tool auto-approve without prompting. With multiple pending approvals it surfaces them **one at a time**; the inner agent resumes only once all are answered. Pending requests are held in the session queue, so an abandoned approval (e.g. page reload) is re-presented on the next run instead of corrupting the thread.
- **`EnableNonApprovalRequiredFunctionBypassing`**: FICC escalation is all-or-nothing — if any call in a model response is gated, *all* sibling calls (including WebMCP client tools) become approval requests. This flag injects a decorator that auto-approves the non-gated ones via the session state, so only genuinely gated tools prompt.

## Flow

```
User: "increment the counter"
  ↓
FICC: increment_counter is ApprovalRequired → ToolApprovalRequestContent, run ends
  ↓
ToolApprovalAgent: no matching rule → surface request (queue the rest, if any)
  ↓
Bridge: → FunctionCallContent(callId = requestId, name = "request_approval")
  ↓
AG-UI: TOOL_CALL_START/ARGS/END + RUN_FINISHED       (client tool call, unanswered)
  ↓
Client: approval card → user decides → tool-result message → re-run
  ↓
Bridge: tool result → ToolApprovalResponseContent (or AlwaysApprove… wrapper)
  ↓
ToolApprovalAgent: record rule (if "always"), inject collected responses
  ↓
FICC: executes approved tool / fabricates failed result for rejection → model answers
```

## History replay: why the normalizer is needed

FICC re-processes the **full persisted history** on every turn, and it only accepts approval content in two states: an active request with a matching response, or request/response pairs whose inner tool call is flagged `InformationalOnly` (= already handled, inert). Anything else throws `"ToolApprovalRequestContent found ... no matching ToolApprovalResponseContent"` and the thread is stuck.

On the resume turn FICC repairs the conversation **in place, in memory**: it executes the approved call, appends the recreated `FunctionCallContent`/`FunctionResultContent` pair, and flips `InformationalOnly = true` on the approval request/response contents. Whether that repair survives depends entirely on the persistence model:

- The framework's persisted-approval sample (`Agent_Step22_PersistedToolApprovalReplay`, local `tool_approval_experiments` branch) re-serializes the **whole live object graph after every run**, so the flipped flags land on disk and replay just works — no normalization needed.
- Our `FileSystemChatHistoryProvider` is **append-only**: it re-reads the file and appends only the turn's new messages. The approval request was persisted a turn earlier with `InformationalOnly = false` and is never re-written. It *can't* adopt the sample's approach either: `ChatHistoryProvider.InvokedContext.RequestMessages` explicitly excludes provider-supplied history, so the store never gets the mutated history back after the run.

Result without repair: persisted request stays "active" while its response was persisted as informational → every later turn throws. `ToolApprovalHistoryNormalizer` therefore runs on every history load (`ProvideChatHistoryAsync`) with two idempotent repairs:

1. **Scrub completed pairs** — request/response contents whose tool call already has a `FunctionResultContent` in history are removed. This also matters beyond the throw: even informational approval contents flow to the provider mapper, which drops them, leaving empty messages that Bedrock rejects (OpenAI tolerates them — one more reason the sample never noticed).
2. **Reject orphans** — a request that nothing answers (client disconnected, session file lost the response, user just sent a new message) gets a synthetic rejected response appended, so FICC fabricates a failed result and the conversation continues. The console sample can't hit this case — its loop forces a y/N answer before accepting input — but a web client abandons approvals routinely.

## Wire contract

The bridge is stateless: the request payload carries everything, and the client echoes it back.

`request_approval` tool-call arguments (streamed as `TOOL_CALL_ARGS`):

```json
{
  "id": "ficc_call_abc",
  "tool_call": { "id": "call_abc", "name": "increment_counter", "arguments": {} }
}
```

Tool-result content the client returns (AG-UI tool message, `toolCallId` = `"ficc_call_abc"`):

```json
{
  "id": "ficc_call_abc",
  "approved": true,
  "reason": null,
  "always_approve": "tool",
  "tool_call": { "id": "call_abc", "name": "increment_counter", "arguments": {} }
}
```

`always_approve`: `null`/absent = this call only · `"tool"` = always allow this tool in this conversation · `"tool_with_arguments"` = only with exactly these arguments. Anything that does not match this shape (or whose `id` doesn't equal the tool-call id) passes through the bridge untouched, so regular client-tool results are never misinterpreted.

## Configuration

```json
"HumanInTheLoop": {
  "ApprovalRequiredTools": [ "increment_counter" ]
}
```

Add any tool name (case-insensitive), including MCP tools such as `GetTime` — gating is config-only.

## Constraints

- "Always allow" rules are **per conversation** (stored in the `AgentSession`), not global.
- With multiple pending approvals, the client sees them one at a time — each decision is one AG-UI run round trip.
- On Bedrock with extended thinking, an approval interrupts the assistant turn; the resumed turn re-pairs tool_use/tool_result via FICC, but reasoning-heavy approval turns should be verified when changing reasoning options.
