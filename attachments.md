# File Attachments

How file attachments work in this project's chat, why we implement them the way we do, and what to change when AG-UI's multimodal support matures.

## Requirements

When a user attaches files to a chat message:

1. The user picks files via a browser file picker (multiple allowed); the bytes are uploaded and stored on the backend filesystem.
2. The file's **storage path** is recorded in the persisted chat history, but **must not be sent to the LLM**.
3. Only the **filename** is visible to the model.
4. Attached files render in the message UI as clickable filename chips (download/open links), uniform for all file types.

## State of the AG-UI protocol (as of 2026-06)

AG-UI does **not** yet have a finalized, spec-level recommendation for attachments. It is an open, draft-stage area:

- The intended direction is to change a user message's `content` from a plain **string** to an **`InputContent[]` array** of typed parts: `ImageInputPart`, `DocumentInputPart`, `AudioInputPart`, `VideoInputPart`, each carrying either an `InputContentDataSource` (inline base64: `mimeType` + `data`) or an `InputContentUrlSource` (a URL reference to an uploaded file). The older shape is a single `BinaryInputContent` `{ mimeType, id?, url?, data?, filename? }`.
- This is still a **roadmap proposal**, not a spec — see [#126](https://github.com/ag-ui-protocol/ag-ui/issues/126), [#280](https://github.com/ag-ui-protocol/ag-ui/issues/280), [#847](https://github.com/ag-ui-protocol/ag-ui/issues/847), [#1005](https://github.com/ag-ui-protocol/ag-ui/issues/1005).
- **URL references are deliberately deferred** for security (SSRF/allowlist). The reference implementation (ADK, [#847](https://github.com/ag-ui-protocol/ag-ui/issues/847)) currently supports **base64 inline data only**.
- **Converters strip attachment content today.** assistant-ui filed [#3810](https://github.com/assistant-ui/assistant-ui/issues/3810): `toAgUiMessages()` keeps only text. The .NET SDK has the same gap (see below). This is ecosystem-wide, not specific to one SDK.

### Why the structured path doesn't work for us yet

The .NET `Microsoft.Agents.AI.AGUI` conversion `AGUIChatMessageExtensions.AsChatMessages` is **text-only**: it drops `name`, content-part arrays, and additional properties — only `TextContent`/tool/reasoning content survives. So even if the frontend sent a structured `InputContent[]` with a document part, the backend would discard it before it ever reached an agent.

On top of that, the only implemented structured option (inline base64) would send the file bytes to the model — which conflicts with requirement #2/#3 (path/bytes hidden, only filename visible).

## How we implement it

Because the structured path is unavailable and partly unsafe for our needs, we carry the attachment reference **in the message text** as a hidden marker (mirroring the existing `DetectMcpAppsActivityMiddleware` + `McpAppsActivityInjector` idiom) and resolve it server-side. Conceptually this is the upload-and-reference-by-id approach the protocol is leaning toward — we just smuggle the id through text instead of a structured part.

### Flow

```
┌────────── Frontend (chat.component.ts) ──────────┐
│ 1. User picks files → POST /agents/files          │  multipart upload
│    ← [{ fileId, fileName }]                        │
│ 2. Pending chips shown in composer                 │
│ 3. On send:                                        │
│    - local message keeps CLEAN text + attachments  │  (no marker shown in UI)
│    - wire payload = text + "\n[[agui-attachments:  │
│      <id1>,<id2>]]"                                 │
└────────────────────────┬───────────────────────────┘
                         │ AG-UI (text survives the text-only conversion)
┌────────────────────────▼─── Backend ──────────────────────────────┐
│ 4. AttachmentResolutionMiddleware (runs BEFORE the model call AND   │
│    before history is persisted):                                    │
│    - strips the marker from the user TextContent                    │
│    - appends model-visible "\n[Attached files: a.pdf, b.png]"       │
│    - resolves each fileId via IUploadedFileStore and writes the     │
│      paths into ChatMessage.AdditionalProperties["attachments"]     │
│ 5. Model sees: text + filename line (NO path, NO marker)            │
│ 6. History persists: filename text + paths in AdditionalProperties  │
│    (AdditionalProperties is not forwarded to the model)             │
└─────────────────────────────────────────────────────────────────────┘
```

### Why this satisfies the "path hidden from model" requirement

Builder middleware wraps **outside** the inner `ChatClientAgent` that owns the `ChatHistoryProvider`. The middleware mutates the user message before calling the inner agent, so the **same** message instance flows to both:

- the **model call** — which only serializes known content (the filename text); `ChatMessage.AdditionalProperties` is metadata and is not sent to the model, and
- the **history provider** — which persists `RequestMessages` (including `AdditionalProperties` with the paths) via plain `System.Text.Json`.

### Marker format

```
\n[[agui-attachments:<fileId1>,<fileId2>,...]]
```

Matched end-anchored by the backend regex `@"\n?\[\[agui-attachments:([^\]]*)\]\]\s*$"`. The leading newline is optional. The local frontend view model never contains the marker — only the wire payload does — so the UI shows clean text plus chips.

### Storage

- Files are saved under `backend/UploadedFiles/` as `{guid}` (the GUID is the only path segment — the original filename is never used as a path, preventing traversal).
- A metadata index `UploadedFiles/_index.json` maps `fileId → { storagePath, originalFileName, contentType }`, so downloads and history path-resolution survive a backend restart.
- `UploadedFiles/` is gitignored.

## Files

| File | Role |
|------|------|
| [backend/UploadedFileStore.cs](backend/UploadedFileStore.cs) | `IUploadedFileStore` singleton; saves bytes + persists the metadata index. |
| [backend/FileEndpoints.cs](backend/FileEndpoints.cs) | `POST /agents/files` (multipart, multiple, 50 MB cap) and `GET /agents/files/{fileId}` (streamed download). Under `/agents/*` so the dev proxy forwards them. |
| [backend/AttachmentResolutionMiddleware.cs](backend/AttachmentResolutionMiddleware.cs) | Strips the marker, appends the model-visible filename line, stashes paths in `AdditionalProperties`. |
| [backend/Program.cs](backend/Program.cs) | Registers the store, maps the endpoints, inserts the middleware first in `CreateAgent()`. |
| [frontend/src/app/chat.component.ts](frontend/src/app/chat.component.ts) | File picker, upload, pending chips, marker injection on send, attachment chip rendering. |

## Gotchas / things we learned

- **The incoming `messages` is a lazy iterator** (AG-UI's `AsChatMessages` yields). A middleware that **mutates messages in place must materialize first** (`messages.ToList()`) and forward the same list — otherwise `next(messages)` re-enumerates the lazy source, rebuilds fresh `ChatMessage` instances, and discards the mutation. (Adding/prepending a message, as `StateSnapshotMiddleware` does, is immune; in-place mutation is not.) This caused a silent failure where the raw marker ended up persisted and sent to the model.
- **Downloads are served as `Content-Disposition: attachment`** so nothing renders/executes inline.
- **`GET /agents/files/{fileId}` only ever uses `fileId` as a dictionary key** — the served path comes from the GUID-derived store value, never from the URL.

## Migration path (when the .NET AG-UI SDK supports `InputContent[]`)

Once `AsChatMessages` preserves structured content parts:

1. Replace the text marker with a real `DocumentInputPart` whose source is an `InputContentUrlSource` pointing at the existing `GET /agents/files/{fileId}` endpoint.
2. Update `AttachmentResolutionMiddleware` to read that structured part instead of parsing the marker.
3. The backend file store and download endpoint stay exactly as-is.

Re-check [#126](https://github.com/ag-ui-protocol/ag-ui/issues/126) for when this lands.
