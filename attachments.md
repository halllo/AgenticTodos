# File Attachments

How file attachments work in this project's chat, why we implement them the way we do, and what to change when AG-UI's multimodal support matures.

## Requirements

1. Files are picked with a browser file picker (multiple allowed), uploaded, and stored on the backend filesystem.
2. The file's **storage path** is recorded in the persisted chat history but **must not be sent to the LLM**.
3. Only the **filename** is visible to the model.
4. Attachments render in the message UI as clickable filename chips (download/open links), uniform for all file types.

## State of the AG-UI protocol (as of 2026-07)

An open, draft-stage area: AG-UI has no finalized, spec-level recommendation, only a **roadmap proposal** — [#126](https://github.com/ag-ui-protocol/ag-ui/issues/126), [#280](https://github.com/ag-ui-protocol/ag-ui/issues/280), [#847](https://github.com/ag-ui-protocol/ag-ui/issues/847), [#1005](https://github.com/ag-ui-protocol/ag-ui/issues/1005).

- **Intended direction:** a user message's `content` changes from a plain **string** to an **`InputContent[]` array** of typed parts — `ImageInputPart`, `DocumentInputPart`, `AudioInputPart`, `VideoInputPart` — each carrying an `InputContentDataSource` (inline base64: `mimeType` + `data`) or an `InputContentUrlSource` (a URL reference to an uploaded file). The older shape is a single `BinaryInputContent` `{ mimeType, id?, url?, data?, filename? }`.
- **URL references are deliberately deferred** for security (SSRF/allowlist), so the reference implementation (ADK, [#847](https://github.com/ag-ui-protocol/ag-ui/issues/847)) is **base64-inline-only** — the one implemented structured option, and it would send the file bytes to the model, conflicting with requirements 2 and 3.
- **Converters strip attachment content today**, ecosystem-wide rather than per-SDK: assistant-ui's `toAgUiMessages()` keeps only text ([#3810](https://github.com/assistant-ui/assistant-ui/issues/3810)), and the .NET SDK has the same gap (see below).

> **Update (AG-UI .NET SDK 0.0.3).** Part of the SDK-side blocker is gone. `AGUIChatMessageExtensions.AsChatMessages` was **text-only** in `Microsoft.Agents.AI.AGUI` (dropping `name`, content-part arrays and additional properties); its successor in `AGUI.Abstractions` keeps `name` as `ChatMessage.AuthorName` and maps a user message's **`binary`** parts (`AGUIBinaryInputContent`) to `UriContent` (`url` set) or `DataContent` (`data` set), with the filename in `AdditionalProperties["filename"]`. The typed media parts (`image`/`document`/`audio`/`video`, i.e. `AGUIMediaInputContent`) are a **sibling** of `AGUIBinaryInputContent`, not a subclass, and are **still silently dropped**, so the `DocumentInputPart` migration below is not yet possible. A `binary` part with a `url` would survive the conversion today, but URL references stay deferred protocol-side for SSRF reasons.

## How we implement it

The attachment reference travels **in the message text** as a hidden marker, resolved server-side — conceptually the upload-and-reference-by-id approach the protocol is leaning toward, with the id smuggled through text instead of a structured part. It is the *inbound* half of a problem the repo solves twice, and the halves share no mechanism: outbound, the protocol has a seam, so extra data travels as a real content type mapped to a real event ([custom-agui-events.md](custom-agui-events.md)). Text-smuggling is what is left when the seam exists in one direction only.

### Flow

**Frontend** ([chat.component.ts](frontend/src/app/chat.component.ts)): files picked → `POST /agents/files` (multipart) → `[{ fileId, fileName }]`; pending chips in the composer; on send the local message keeps **clean** text plus its attachments (the UI never shows a marker) while the wire payload is `text + "\n[[agui-attachments:<id1>,<id2>]]"` — text, so it survives the text-only conversion.

**Backend**: [`AttachmentResolutionMiddleware`](backend/AttachmentResolutionMiddleware.cs) runs **before the model call and before history is persisted**: it strips the marker from the user `TextContent`, appends a model-visible `"\n[Attached files: a.pdf, b.png]"`, resolves each `fileId` via `IUploadedFileStore`, and writes the paths into `ChatMessage.AdditionalProperties["attachments"]`. The model sees text plus the filename line (no path, no marker); history persists that text plus the paths.

One edit serves both consumers because builder middleware wraps **outside** the inner `ChatClientAgent` that owns the `ChatHistoryProvider`: the **same** mutated instance reaches the model call — which serializes only known content, `ChatMessage.AdditionalProperties` being metadata that is not forwarded — and the history provider, which persists `RequestMessages` via plain `System.Text.Json`.

### Marker format

`\n[[agui-attachments:<fileId1>,<fileId2>,...]]`, matched end-anchored by the backend regex `@"\n?\[\[agui-attachments:([^\]]*)\]\]\s*$"`; the leading newline is optional.

## Storage

- Files are saved under `backend/UploadedFiles/` as `{guid}` — the GUID is the only path segment, so the original filename is never used as a path, preventing traversal.
- The metadata index `UploadedFiles/_index.json` lets downloads and history path-resolution survive a backend restart. On disk it is a JSON **array** of `{ "FileId", "StoragePath", "OriginalFileName", "ContentType" }` — PascalCase, because `UploadedFileStore` serializes `index.Values.ToList()` with no naming policy; the `fileId → info` dictionary is the in-memory shape only, rebuilt from the array on load by keying each entry on its own `FileId`.
- `UploadedFiles/` is gitignored.

## Files

| File | Role |
|------|------|
| [backend/UploadedFileStore.cs](backend/UploadedFileStore.cs) | `IUploadedFileStore` singleton; saves bytes, persists the metadata index. |
| [backend/FileEndpoints.cs](backend/FileEndpoints.cs) | `POST /agents/files` (multipart, multiple, 50 MB cap) and `GET /agents/files/{fileId}` (streamed download). Under `/agents/*` so the dev proxy forwards them. |
| [backend/AttachmentResolutionMiddleware.cs](backend/AttachmentResolutionMiddleware.cs) | Marker → model-visible filename line + paths in `AdditionalProperties` (see [Flow](#flow)). |
| [backend/Program.cs](backend/Program.cs) | Registers the store, maps the endpoints, adds `.UseAttachmentResolution(fileStore)` to the `CreateAgent()` chain — below the two tool-approval links, above everything that reads the message text. |
| [frontend/src/app/chat.component.ts](frontend/src/app/chat.component.ts) | Picker, upload, pending chips, marker injection on send, chip rendering. |
| [tests/AttachmentResolutionMiddlewareTests.cs](tests/AttachmentResolutionMiddlewareTests.cs) | The middleware, including a data-leak guard: the model-visible text carries filenames only, never the storage path. |

## Gotchas / things we learned

- **A middleware that mutates messages in place must materialize first** (`messages.ToList()`) and forward that same list — the instances continuing down the pipeline have to be the ones that were edited, or a lazy re-enumeration rebuilds fresh `ChatMessage` objects and silently discards the edit, which is how the raw marker once ended up both persisted and sent to the model. The AG-UI server SDK already hands over a `List` (`RunAgentInputExtensions.ToChatRequestContext` calls `AsChatMessages(...).ToList()`), so materializing is insurance against the caller, not a fix for a live bug. (Adding or prepending a message, as `StateSnapshotMiddleware` does, is immune either way; in-place mutation is not.)
- Downloads are served as `Content-Disposition: attachment`, so nothing renders or executes inline.
- `GET /agents/files/{fileId}` only ever uses `fileId` as a dictionary key — the served path comes from the GUID-derived store value, never from the URL.

## Migration path (when the .NET AG-UI SDK supports `InputContent[]`)

Once `AsChatMessages` preserves structured content parts: replace the text marker with a real `DocumentInputPart` whose source is an `InputContentUrlSource` pointing at the existing `GET /agents/files/{fileId}` endpoint, and update `AttachmentResolutionMiddleware` to read that part instead of parsing the marker. The backend file store and download endpoint stay exactly as-is. Re-check [#126](https://github.com/ag-ui-protocol/ag-ui/issues/126) for when this lands.
