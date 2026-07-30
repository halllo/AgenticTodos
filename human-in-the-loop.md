# Human-in-the-Loop Tool Approval

Pauses the agent before executing selected tools, asks the user for approval in the client (Approve / Always allow / Reject), and resumes the run with the decision. Follows the [Microsoft Agent Framework AG-UI HITL pattern](https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/human-in-the-loop?pivots=programming-language-csharp), adapted to the current package versions.

## How the pieces line up

MEAI does the heavy lifting: a tool wrapped in `ApprovalRequiredAIFunction` is not invoked by `FunctionInvokingChatClient` (FICC, inside `ChatClientAgent`); FICC emits a `ToolApprovalRequestContent` and ends the run. A later run carrying a matching `ToolApprovalResponseContent` executes or rejects it and continues, recreating the `tool_use`/`tool_result` pairing itself so even Amazon Bedrock's strict validation passes.

AG-UI models the pause as an **interrupt**: `RUN_FINISHED` carries `outcome = { type: "interrupt", interrupts: [...] }`, answered on the next run through the `resume` array. [`ToolApprovalInterruptMiddleware`](backend/ToolApprovalInterruptMiddleware.cs) maps between the two worlds.

The Microsoft docs still use the pre-10.4 type names (`FunctionApprovalRequestContent`/`FunctionApprovalResponseContent`): both pairs coexisted in MEAI 10.2/10.3, and 10.4 dropped the old one.

### Why a middleware and not just the SDK

`AGUI.Server` maps `ToolApprovalRequestContent` to an interrupt itself, but only under `!clientToolNames.Contains(name) && (clientToolNames.Count == 0 || isContinuation)` — a tool the client did **not** declare, on a run declaring no client-side tools at all or already a continuation turn. This app always declares WebMCP tools, so a gated server-side tool on the first turn would never reach the user. The middleware converts the request into an `InterruptRequestContent` before the SDK sees it, which the SDK always emits as an interrupt. Hence no `TOOL_CALL_*` events for an unapproved call — the client renders the pending call from the interrupt's `metadata.toolCall` — and reason `confirmation`, not `tool_call`, which would ask the client to correlate the interrupt with a call it has already seen streamed; the AG-UI .NET client silently drops one it cannot match.

**Only server-side tools are converted.** Client tool names come off `RunAgentInput.Tools`; their requests are left to the SDK, whose `TOOL_CALL_*` mapping is the correct one for them — the client executes the tool. A WebMCP call never becomes an approval request anyway: `AGUIToolExtensions.AsAITools(IList<AGUITool>)` goes through `AIFunctionFactory.CreateDeclaration`, so a client tool is an `AIFunctionDeclaration` and *not* an `AIFunction`; `ConfigureForMixedInvocation` wraps only `AIFunction`s in `ApprovalRequiredAIFunction`; FICC cannot invoke a declaration and hands the `FunctionCallContent` straight back. Two paths do produce one, and the exclusion keeps both correct:

- **A repeat call on a continuation turn.** Once a client tool has returned a result this turn, `AGUI.Server.ProcessContinuation` replaces it with an `ApprovalRequiredAIFunction` over a proxy replaying that result. Converting would show an approval card for a frontend tool, then return the stale result instead of calling it again.
- **Sibling escalation** — FICC escalates a client tool called alongside a gated server tool; see [below](#sibling-escalation-one-extra-round-trip).

## Files

| File | Role |
| --- | --- |
| `backend/ToolApprovalInterruptMiddleware.cs` | The conversion both ways; inbound it upgrades the SDK-decoded response to `AlwaysApproveToolApprovalResponseContent` for a standing rule |
| `backend/Program.cs` | Pipeline `UseToolApprovalInterrupts()` (outer) → `UseToolApproval(...)` (inner); config-driven `ApprovalRequiredAIFunction` wrapping in `GetTools` |
| `backend/appsettings.json` | `HumanInTheLoop:ApprovalRequiredTools` — gated tool names, local functions and MCP tools alike |
| `backend/ToolApprovalHistoryNormalizer.cs` | The three repairs — see [History replay](#history-replay-why-the-normalizer-is-needed) |
| `backend/OmitEmptyMessagesMiddleware.cs` | Drops messages carrying nothing a model can be shown, `InterruptResponseContent` included — see [Constraints](#constraints) |
| `backend/DetectMcpAppsActivityMiddleware.cs` | `GetService<McpClientTool>()` rather than `OfType`, so MCP-apps rendering survives an MCP tool wrapped for approval |
| `frontend/src/app/chat.component.ts` | Approval card (role `'approval'`) with Approve / Always allow / Reject; WebMCP calls and interrupts share one `PendingClientCall` list, drained into tool messages, and into `resume` entries via `buildResumeArray` |
| `cli/Verbs/Agent.cs` | Console `(y)es / (a)lways this tool / (s)ame arguments always / (n)o` prompt; re-runs until nothing is pending; pins one AG-UI thread id per REPL so that session's standing rules survive — see [Client-side tools in the CLI](#client-side-tools-in-the-cli) |
| `tests/ToolApprovalInterruptMiddlewareTests.cs` | Both directions; a client-declared tool's request passes through unconverted, a server-side one becomes an interrupt. The `Sdk_*` tests pin the exclusion's basis: client tools arrive as declarations on a first turn, as an approval-required replay proxy on a continuation turn |
| `tests/ToolApprovalSiblingEscalationTests.cs` | Which half is deferred in either order, and the three-run sequence recovering both |
| `tests/ToolApprovalHistoryNormalizerTests.cs` | Scrubbing, orphan rejection, idempotency |

## Layering

```text
CreateAgent pipeline (outer → inner)
  UseOpenTelemetry
► UseToolApprovalInterrupts()    wire ⇄ MEAI translation (this doc)
► UseToolApproval(options)       Microsoft.Agents.AI.ToolApprovalAgent:
                                 "always allow" rules + one-at-a-time queueing,
                                 persisted as ToolApprovalState in the AgentSession
  AttachmentResolution / OmitEmptyMessages / StateSnapshot / DetectMcpApps / EUAIAct…
  ChatClientAgent
    ├ FileSystemChatHistoryProvider   (persists approval request/response content, append-only;
    │                                  runs ToolApprovalHistoryNormalizer on every load)
    └ FunctionInvokingChatClient      (ApprovalRequiredAIFunction handling)
```

- **`ToolApprovalAgent`** (`UseToolApproval`): "always allow" records a `ToolApprovalRule` in the session's `ToolApprovalState` (persisted by `FileSystemSessionStore`), so later calls to that tool auto-approve without prompting. Requests not yet surfaced wait in `ToolApprovalState.QueuedApprovalRequests`, popped on the next run — but only those: one already surfaced and then abandoned (page reload, agent switch, failed run) never returns as a card, and its persisted copy is [answered with a synthetic rejection](#history-replay-why-the-normalizer-is-needed) on the thread's next run, leaving the gated call refused rather than the thread stuck.
- **Approval-not-required bypassing**: FICC escalation is all-or-nothing — if any call in a model response is gated, *all* siblings (WebMCP client tools included) become approval requests. `ApprovalNotRequiredFunctionBypassingChatClient` auto-approves the escalated siblings by default, so only genuinely gated tools prompt. (Opt out with `ChatClientAgentOptions.DisableApprovalNotRequiredFunctionBypassing`; up to 1.13 this was the opposite, opt-in `EnableNonApprovalRequiredFunctionBypassing`.) **It does not cover client tools**: candidates are `options.Tools` concatenated with `FunctionInvokingChatClient.AdditionalTools`, then `OfType<AIFunction>()` minus the approval-required ones — and a WebMCP tool is an `AIFunctionDeclaration`, so neither source can supply one.

### Sibling escalation: one extra round trip

Call a gated server tool **and** a WebMCP tool in one response and the two rules compose: FICC escalates both, bypassing rescues neither (the server tool is genuinely gated, the client tool is not an `AIFunction`), and `ToolApprovalAgent` surfaces only the first, queueing the second in `ToolApprovalState`. Whichever the model listed second is deferred by a run — gated server tool first, the interrupt goes out now and the WebMCP call is queued; WebMCP tool first, `TOOL_CALL_*` goes out now and the approval card is queued.

`PrepareInboundMessagesAsync` pops the queued request on the next run and returns it *without invoking the inner agent*. The first case end to end (`ToolApprovalSiblingEscalationTests` pins all three runs):

1. Interrupt for `increment_counter`; the `add_todo` request is queued.
2. The decision arrives with this run's messages and is collected into `CollectedApprovalResponses` — `PrepareInboundMessagesAsync` harvests responses only while the queue is still non-empty, which is why that happens here and not on run 1. The queued `add_todo` request is re-surfaced, passes through unconverted, and the SDK maps it to `TOOL_CALL_*`: the browser executes it now.
3. The tool result arrives, `InjectCollectedResponses` replays the approval, FICC finally runs `increment_counter`, and the model answers.

Cost: one extra round trip, and an inverted order — the client tool runs *after* the approval decision. Step 3 only survives because of the normalizer: the `add_todo` approval request persisted in run 1 is an orphan by step 3 and FICC throws `"no matching ToolApprovalResponseContent"` on it, which [repair 3](#history-replay-why-the-normalizer-is-needed) answers with a synthetic rejection. (Run 2 persists nothing: `ToolApprovalAgent` returns the popped request without invoking the inner agent.)

`ChatOptions.AllowMultipleToolCalls = false` avoids the escalation entirely (FICC's own suggestion for this hazard) at the cost of one round trip per tool call. Not currently set.

## Flow

```text
User: "increment the counter"
  ↓
FICC: increment_counter is ApprovalRequired → ToolApprovalRequestContent, run ends
  ↓
ToolApprovalAgent: no matching rule → surface request (queue the rest, if any)
  ↓
Middleware: → InterruptRequestContent(requestId), metadata.toolCall = pending call
  ↓
AG-UI: RUN_FINISHED { outcome: { type: "interrupt", interrupts: [...] } }
  ↓
Client: approval card → user decides → resume entry → re-run
  ↓
AGUI.Server: resume payload (with toolCall) → ToolApprovalRequestContent + ToolApprovalResponseContent
  ↓
Middleware: upgrade to AlwaysApprove… when the payload asked for a standing rule
  ↓
ToolApprovalAgent: record rule (if "always"), inject collected responses
  ↓
FICC: executes approved tool / fabricates failed result for rejection → model answers
```

## Client-side tools in the CLI

The CLI declares its one client-side tool, `change_background_color`, as a **declaration** (`AIFunctionFactory.CreateDeclaration`) rather than the invocable `AIFunction` it also holds — the same shape the frontend's WebMCP tools have. `AGUIChatClient` puts it on the request's `tools` array from `ChatOptions.Tools` either way, so the model still sees it, but no client-side FICC can invoke it and FICC hands the call back to the caller instead of answering mid-run. That keeps one AG-UI run to one HTTP request: the executable `AIFunction` would make the local FICC loop *inside* the run and re-send the turn's messages in a second request, landing them twice in the server-owned history.

A finished run leaves the CLI the two kinds of pending work the frontend keeps in its `PendingClientCall` list — both collected during the run, answered after it, never mid-run:

| Pending | Resolved by | Travels back as |
| --- | --- | --- |
| client-side tool call | invoking the local `AIFunction` | a `ChatRole.Tool` message → `{ "role": "tool", "toolCallId": … }` |
| approval interrupt | the console `y / a / s / n` prompt | a trailing `ChatMessage(ChatRole.User, [...InterruptResponseContent])` |

`AGUIChatClient` scans the **last** message only, moves the interrupt responses onto a cloned `ChatOptions` and strips the message before sending — the supported handover, as opposed to writing the SDK's internal `agui_interrupt_responses` key — so the carrier goes after any tool-result messages.

Two kinds of interrupt the CLI cannot answer are reported and then **dropped without a response**: a `reason` other than `confirmation` (`InterruptReasons` also defines `input_required`, a request for data rather than a decision), and `metadata` without an object-shaped `toolCall` carrying a string `callId` and `name`. Answering either is worse than dropping — `AGUI.Server.TryDecodeToolApprovalResume` coerces *any* resume payload holding a `toolCall` into an approval request/response pair whatever the interrupt asked for, and bails out unless that `toolCall` deserializes to a non-null `AGUIToolCallInfo`, so `toolCall: null` leaves the gated call unanswered anyway. Both checks are argued at their call site in [`cli/Verbs/Agent.cs`](cli/Verbs/Agent.cs).

**Dropping is a deliberate divergence from the frontend**, which *declines* both cases with a `{ status: 'cancelled' }` resume entry — see [Constraints](#constraints). The difference is in the client libraries, not the intent: `@ag-ui/client` keeps its own ledger of open interrupts and `AbstractAgent.onInitialize` rejects the next run over anything left in it, so the browser must close them explicitly; `AGUI.Client` keeps no such ledger — the `resume` array is built purely from the `InterruptResponseContent` found on the last message — so the CLI's next turn goes out normally, and the request nothing answered is closed server-side by [the normalizer's third repair](#history-replay-why-the-normalizer-is-needed). Dropping is also what lets the CLI's `do…while` terminate: it repeats only while there is something to send.

A turn therefore looks on the wire exactly like the same turn from the browser:

```text
run 1   messages: [system, user]   tools: [change_background_color]   → TOOL_CALL_* for change_background_color
run 2   messages: [tool result]    tools: [change_background_color]   → RUN_FINISHED, outcome interrupt
run 3   messages: []               resume: [{ interruptId, … }]       → text answer
```

A failed run drops both kinds of pending work and strips the spent interrupt-response carrier — the counterpart of the frontend's `resetPendingWork()`. Its interrupts can never be answered, results for its tool calls must not reach a later run, and a carrier that is no longer last goes out as an ordinary user message whose text is the content's `ToString()`. The turn's real messages stay: a failed run never reached the server's history.

## History replay: why the normalizer is needed

FICC re-processes the **full persisted history** on every turn and accepts approval content in two states only: an active request with a matching response, or request/response pairs whose inner tool call is flagged `InformationalOnly` (= already handled, inert). Anything else throws `"ToolApprovalRequestContent found ... no matching ToolApprovalResponseContent"` and the thread is stuck.

On the resume turn FICC repairs the conversation **in place, in memory**: it executes the approved call, appends the recreated `FunctionCallContent`/`FunctionResultContent` pair, and flips `InformationalOnly = true` on the approval contents. Whether the repair survives depends on the persistence model. The framework's persisted-approval sample (`Agent_Step22_PersistedToolApprovalReplay`, local `tool_approval_experiments` branch) re-serializes the **whole live object graph after every run**, so the flipped flags land on disk and replay just works. Our `FileSystemChatHistoryProvider` is **append-only** — it re-reads the file and appends only the turn's new messages, so the approval request, persisted a turn earlier with `InformationalOnly = false`, is never re-written. Nor could it adopt the sample's approach: `ChatHistoryProvider.InvokedContext.RequestMessages` explicitly excludes provider-supplied history, so the store never gets the mutated history back.

The persisted request therefore stays "active" while its response was persisted as informational, and every later turn throws. `ToolApprovalHistoryNormalizer` runs on every history load (`ProvideChatHistoryAsync`) with three idempotent repairs:

1. **Scrub completed pairs** — request/response contents whose tool call already has a `FunctionResultContent` in history are removed. This matters beyond the throw: even informational approval contents reach the provider mapper, which drops them, leaving empty messages Bedrock rejects (OpenAI tolerates them — one more reason the sample never noticed).
2. **Drop re-supplied requests** — `AGUI.Server` rebuilds a *complete* pair from the resume payload, duplicating the request this session-backed history already holds. FICC indexes approval requests by id and throws `"An item with the same key has already been added"` on the duplicate, so the historical copy gives way to the one arriving with the turn.
3. **Reject orphans** — a request nothing answers (client disconnected, session file lost the response, user just sent a new message) gets a synthetic rejected response appended, so FICC fabricates a failed result and the conversation continues. The console sample can't hit this — its loop forces a y/N answer before accepting input — but a web client abandons approvals routinely.

## Wire contract

Stateless: the interrupt carries everything, and the client echoes the pending call back. One entry of `outcome.interrupts` on `RUN_FINISHED`:

```json
{
  "id": "ficc_call_abc",
  "reason": "confirmation",
  "toolCallId": "call_abc",
  "message": "Approval required for tool call: increment_counter",
  "metadata": { "toolCall": { "callId": "call_abc", "name": "increment_counter", "arguments": {} } },
  "responseSchema": { "…": "JSON Schema of the payload below" }
}
```

One entry of the next run's `resume` array:

```json
{
  "interruptId": "ficc_call_abc",
  "status": "resolved",
  "payload": {
    "toolCall": { "callId": "call_abc", "name": "increment_counter", "arguments": {} },
    "approved": true,
    "alwaysApprove": "tool"
  }
}
```

The `toolCall` echo lets `AGUI.Server` rebuild the MEAI approval pair with no server-side correlation memory. `alwaysApprove`: `null`/absent = this call only · `"tool"` = always allow this tool in this conversation · `"tool_with_arguments"` = only with exactly these arguments. An unrecognized value degrades to a one-shot approval; a rejection stays a rejection even when combined with `alwaysApprove`.

There is deliberately **no `reason`** in the payload: the SDK decodes it into `AGUIToolApprovalResumePayload`, which models only `approved`, `toolCall` and `result`, so a reason could never reach the approval content. (MEAI's `ToolApprovalRequestContent.CreateResponse` does take one — it is the AG-UI wire format that has no field for it.)

## Configuration

```json
"HumanInTheLoop": {
  "ApprovalRequiredTools": [ "increment_counter" ]
}
```

Any tool name (case-insensitive), MCP tools such as `get_time` included — gating is config-only.

The name has to be the **MCP wire name**, not the C# method name: `GetTools` in [`backend/Program.cs`](backend/Program.cs) gates on `AIFunction.Name`, and the ModelContextProtocol server derives the wire name by snake-casing the method (`GetTimeApp.GetTime()` ⇒ `get_time`), so an entry of `"GetTime"` matches nothing and the tool stays ungated — silently, because an unmatched name is not an error.

## Constraints

- "Always allow" rules are **per conversation** (in the `AgentSession`), not global. Multiple pending approvals reach the client one at a time, one AG-UI run round trip each, and the inner agent resumes only once all are answered.
- A gated server tool and a WebMCP tool in the **same** model response cost one extra round trip, the client tool running after the approval decision rather than alongside it — see [Sibling escalation](#sibling-escalation-one-extra-round-trip).
- The AG-UI client refuses to start a run that leaves open interrupts unanswered, so a cancelled or failed run must clear `agent.pendingInterrupts`. **Every** way a run can end badly clears it — `onRunErrorEvent` inline, and `resetPendingWork()` for `runAgent` throwing or neither terminal event arriving (each path argued at its call site in [`chat.component.ts`](frontend/src/app/chat.component.ts)) — and drops that run's pending tool calls too, or the next run's finish handler would execute a call the dead run streamed and post its result under a `toolCallId` the server has no record of.
- For the same reason an interrupt the client cannot make sense of must be **declined**, not ignored: `defaultApplyEvents` assigns `pendingInterrupts` from the run-finished event *after* the subscribers return, so an interrupt nothing queued still counts as open — the resume never happens and the user's next message is rejected with *"Thread has N pending interrupt(s) not addressed by resume"*. `declineInterrupt` closes it with a `{ status: 'cancelled' }` resume entry instead.
- **A declined interrupt leaves residue on the server, and nothing there reads it.** `RunAgentInputExtensions.ToChatRequestContext` emits an `InterruptResponseContent` per `resume` entry whose payload is not a decodable tool approval — a `{ status: 'cancelled' }` entry has no payload at all — collected into a single `ChatRole.User` message. `TryDecodeToolApprovalResume` looks only at `payload`, never at `AGUIResume.Status`, so the *decision* never becomes a `ToolApprovalResponseContent`; repair 3 above resolves the abandoned request instead, on the next history load. The carrier would reach Bedrock with every content block dropped by the provider mapper and be rejected, so [`OmitEmptyMessagesMiddleware`](backend/OmitEmptyMessagesMiddleware.cs) counts `InterruptResponseContent` as empty and removes it — above `ChatClientAgent`, so [`LoggingMiddleware`](backend/LoggingMiddleware.cs) should never see one, though it survives if it does: `AIJsonUtilities.DefaultOptions` covers only the built-in `AIContent` hierarchy and cannot be extended (read-only, closed `[JsonPolymorphic]` set), so it traces over a copy carrying this app's own registrations and degrades to content type names.
- On Bedrock with extended thinking an approval interrupts the assistant turn; FICC re-pairs `tool_use`/`tool_result` on the resumed turn, but reasoning-heavy approval turns should be verified when changing reasoning options — see the Bedrock findings in [README.md](README.md).
