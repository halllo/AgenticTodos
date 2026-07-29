import { ChangeDetectionStrategy, Component, computed, effect, ElementRef, inject, linkedSignal, signal, untracked, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { buildResumeArray, HttpAgent, Interrupt, Message, ResumeEntry, RunAgentParameters } from "@ag-ui/client"
import { JsonPipe } from '@angular/common';
import { form, FormField, required } from '@angular/forms/signals';
import { WebmcpService } from './webmcp.service';
import { McpAppComponent } from './mcp-app.component';

interface NewMessageViewModel {
  content: string;
}

interface Attachment {
  fileId: string;
  fileName: string;
}

interface RiskClassification {
  risk: string;
  category: string;
  reason: string;
}

type ApprovalDecision = 'approved' | 'always' | 'rejected';

// The tool call an interrupt asks the user to approve, as the backend puts it on
// `interrupt.metadata` and expects it echoed back in the resume payload.
interface ApprovalToolCall {
  callId: string;
  name: string;
  arguments?: Record<string, unknown>;
}

// Mirrors the library's own (unexported) ResumeResponse: what buildResumeArray expects per open
// interrupt. `resolved` carries the answer, `cancelled` declines the interrupt without one.
type ResumeResponse = { status: 'resolved'; payload?: unknown } | { status: 'cancelled' };

/** Narrows untyped wire data to a JSON object — neither a primitive nor an array. */
function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// Work a finished run left for the client to resolve before the conversation can continue.
// `tool` entries are WebMCP frontend tool calls, resolved by executing them; `approval` entries are
// AG-UI interrupts (see human-in-the-loop.md), resolved by a user decision; `cancelledInterrupt`
// entries are interrupts this client cannot answer and therefore declines. Tool results travel back
// as tool messages, decisions and declines as `resume` entries — the run resumes once nothing is
// unresolved.
type PendingClientCall =
  // `args` is filled in from onToolCallEndEvent, which hands over the parsed arguments, so it stays
  // unset while the deltas are still streaming (and if the run dies before TOOL_CALL_END).
  | { kind: 'tool'; id: string; name: string; args?: Record<string, any>; result?: string }
  | { kind: 'approval'; id: string; toolCall: ApprovalToolCall; decision?: ApprovalDecision }
  | { kind: 'cancelledInterrupt'; id: string };

interface ApprovalViewModel {
  toolName: string;
  args: Record<string, unknown>;
  decision?: ApprovalDecision;
}

interface MessageViewModel {
  role: 'user' | 'assistant' | 'tool' | 'activity' | 'risk' | 'reasoning' | 'approval';
  content: string;
  toolName?: string;
  toolCallId?: string;
  isGenerating?: boolean;
  error?: boolean;
  resourceUri?: string;
  messageId?: string;
  /** Identifies an approval card with the AG-UI interrupt it answers. */
  interruptId?: string;
  toolInput?: Record<string, unknown>;
  toolResult?: unknown;
  attachments?: Attachment[];
  risk?: RiskClassification;
  approval?: ApprovalViewModel;
  collapsed?: boolean;
  userToggled?: boolean;
}

@Component({
  selector: 'app-chat',
  imports: [FormField, JsonPipe, McpAppComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chat">
      <header class="chat__header">
        <div class="chat__header-left">
          <h1>Assistant</h1>
          <select
            class="chat__agent-selector"
            [value]="selectedAgent()"
            (change)="onAgentChange($event)"
            [disabled]="isLoading()"
          >
            @for (agent of agents.value(); track agent) {
              <option [value]="agent">{{ agent }}</option>
            }
          </select>
        </div>
        <div class="chat__status" [class.chat__status--active]="!isLoading()">
          {{ status() }}
        </div>
      </header>

      <div class="chat__messages" #messagesContainer>
        @if (messages().length === 0) {
          <div class="chat__empty">
            <p>Start a conversation with your AI assistant</p>
          </div>
        }
        
        @for (message of messages(); track $index) {
          <div
            class="chat__message"
            [class.chat__message--user]="message.role === 'user'"
            [class.chat__message--assistant]="message.role === 'assistant'"
            [class.chat__message--tool]="message.role === 'tool'"
            [class.chat__message--activity]="message.role === 'activity'"
            [class.chat__message--reasoning]="message.role === 'reasoning'"
            [class.chat__message--risk]="message.role === 'risk'"
            [class.chat__message--risk-critical]="message.role === 'risk' && message.risk?.risk === 'Unacceptable'"
            [class.chat__message--approval]="message.role === 'approval'"
            [class.chat__message--error]="message.error"
          >
            <div class="chat__avatar">
              @if (message.role === 'user') {
                👤
              } @else if (message.role === 'assistant') {
                🤖
              } @else if (message.role === 'tool') {
                🛠️
              } @else if (message.role === 'activity') {
                🔌
              } @else if (message.role === 'reasoning') {
                🧠
              } @else if (message.role === 'risk') {
                ⚠️
              } @else if (message.role === 'approval') {
                ✋
              }
            </div>
            <div class="chat__content" [class.chat__content--generating]="message.isGenerating">
              @if (message.role === 'tool' && message.toolName) {
                <span class="chat__toolIndicator">{{ message.toolName }}</span>
                <br>
              }
              @if (message.role === 'activity') {
                <span class="chat__toolIndicator">MCP App · {{ message.resourceUri }}</span>
                <app-mcp-app
                  [resourceUri]="message.resourceUri!"
                  [toolInput]="message.toolInput ?? {}"
                  [toolResult]="message.toolResult"
                />
              } @else if (message.role === 'reasoning') {
                <button
                  type="button"
                  class="chat__reasoningToggle"
                  [attr.aria-expanded]="!message.collapsed"
                  [attr.aria-controls]="'reasoning-' + message.messageId"
                  (click)="toggleReasoning(message.messageId)"
                >
                  <span class="chat__reasoningChevron" [class.chat__reasoningChevron--open]="!message.collapsed" aria-hidden="true">▸</span>
                  <span class="chat__reasoningLabel" [class.chat__reasoningLabel--live]="message.isGenerating">
                    {{ message.isGenerating ? 'Thinking…' : 'Thought process' }}
                  </span>
                </button>
                @if (!message.collapsed) {
                  <div class="chat__reasoningBody" [id]="'reasoning-' + message.messageId">{{ message.content }}</div>
                }
              } @else if (message.role === 'risk') {
                <span class="chat__riskBadge">EU AI Act · {{ message.risk?.risk }} risk</span>
                @if (message.risk?.category) {
                  <div class="chat__riskCategory">{{ message.risk.category }}</div>
                }
                @if (message.risk?.reason) {
                  <div class="chat__riskReason">{{ message.risk.reason }}</div>
                }
              } @else if (message.role === 'approval') {
                <span class="chat__approvalBadge">Approval required</span>
                @if (message.approval; as approval) {
                  <div class="chat__approvalTool">{{ approval.toolName }}</div>
                  <pre class="chat__approvalArgs">{{ approval.args | json }}</pre>
                  @if (approval.decision; as decision) {
                    <span class="chat__approvalDecision" [class.chat__approvalDecision--rejected]="decision === 'rejected'">
                      {{ decision === 'approved' ? 'Approved ✓' : decision === 'always' ? 'Always allowed ∞' : 'Rejected ✕' }}
                    </span>
                  } @else {
                    <div class="chat__approvalActions">
                      <button type="button" class="chat__approvalBtn chat__approvalBtn--approve"
                        (click)="onApprovalDecision(message.interruptId!, 'approved')">✓ Approve</button>
                      <button type="button" class="chat__approvalBtn chat__approvalBtn--always"
                        title="Approve and don't ask again for this tool in this conversation"
                        (click)="onApprovalDecision(message.interruptId!, 'always')">∞ Always allow</button>
                      <button type="button" class="chat__approvalBtn chat__approvalBtn--reject"
                        (click)="onApprovalDecision(message.interruptId!, 'rejected')">✕ Reject</button>
                    </div>
                  }
                }
              } @else {
                {{ message.content }}
                @if (message.attachments?.length) {
                  <div class="chat__attachments">
                    @for (att of message.attachments; track att.fileId) {
                      <a class="chat__attachmentChip" [href]="'/agents/files/' + att.fileId" target="_blank" rel="noopener" download>
                        📄 {{ att.fileName }}
                      </a>
                    }
                  </div>
                }
              }
            </div>
          </div>
        }

        @if (showTypingIndicator()) {
          <div class="chat__message chat__message--assistant">
            <div class="chat__avatar">🤖</div>
            <div class="chat__content">
              <div class="chat__typing">
                <span></span>
                <span></span>
                <span></span>
              </div>
            </div>
          </div>
        }
      </div>

      @if (conversationState()) {
        <div class="chat__stateRow">
          <pre class="chat__state">{{ conversationState() | json }}</pre>
          <div class="chat__stateAdd">
            <input #selectedResourceInput type="text" class="chat__stateInput" placeholder="Add selected resource…" />
            <button type="button" class="chat__stateBtn" (click)="addSelectedResource(selectedResourceInput.value); selectedResourceInput.value = ''">+</button>
          </div>
        </div>
      }
      @if (pendingAttachments().length) {
        <div class="chat__pending">
          @for (att of pendingAttachments(); track att.fileId) {
            <span class="chat__pendingChip">
              📄 {{ att.fileName }}
              <button type="button" class="chat__pendingRemove" (click)="removePending(att.fileId)" aria-label="Remove attachment">×</button>
            </span>
          }
        </div>
      }
      <form class="chat__inputRow" (submit)="onSubmit($event)">
        <input #fileInput type="file" multiple hidden (change)="onFilesSelected($event)"/>
        <button type="button" class="chat__attach" [disabled]="isUploading()" (click)="fileInput.click()" aria-label="Attach files" title="Attach files">📎</button>
        <input type="text" [formField]="newMessageForm.content" [attr.placeholder]="awaitingApproval() ? 'Respond to the approval request above…' : 'Type your message...'" class="chat__input"/>
        @if (isLoading()) {
          <button type="button" class="chat__send" (click)="cancelRun()">✋ Stop</button>
        } @else {
          <button type="submit" class="chat__send" [disabled]="awaitingApproval()">Send</button>
        }
      </form>
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      width: 100%;
    }

    .chat {
      display: flex;
      flex-direction: column;
      height: 100%;
      width: 100%;
      min-height: 0;
      min-width: 0;
      background: var(--surface);
    }

    .chat__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 1rem 1.5rem;
      background: var(--brand-gradient);
      color: white;
      box-shadow: 0 2px 4px rgba(0,0,0,0.1);

      .chat__header-left {
        display: flex;
        align-items: center;
        gap: 1rem;
      }

      h1 {
        margin: 0;
        font-size: 1.5rem;
        font-weight: 600;
      }

      .chat__agent-selector {
        padding: 0.5rem 1rem;
        border-radius: 8px;
        border: 2px solid rgba(255, 255, 255, 0.3);
        background: rgba(255, 255, 255, 0.1);
        color: white;
        font-size: 0.875rem;
        font-weight: 500;
        cursor: pointer;
        outline: none;
        transition: all 0.2s;

        &:hover:not(:disabled) {
          background: rgba(255, 255, 255, 0.2);
          border-color: rgba(255, 255, 255, 0.5);
        }

        &:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        option {
          background: var(--surface);
          color: var(--text);
        }
      }

      .chat__status {
        font-size: 0.875rem;
        padding: 0.5rem 1rem;
        border-radius: 20px;
        background: rgba(255, 255, 255, 0.2);

        &.chat__status--active {
          background: rgba(76, 175, 80, 0.3);
        }
      }
    }

    .chat__messages {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: 1.5rem;
      background: var(--surface-muted);

      .chat__empty {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100%;
        color: var(--text-muted);
        font-size: 1.125rem;
      }
    }

    .chat__message {
      display: flex;
      gap: 0.75rem;
      margin-bottom: 1.5rem;
      animation: slideIn 0.3s ease-out;

      &.chat__message--user {
        flex-direction: row-reverse;

        .chat__content {
          background: var(--brand-gradient);
          color: white;
          border-radius: 18px 4px 18px 18px;
        }
      }

      &.chat__message--assistant {
        .chat__content {
          background: var(--surface);
          color: var(--text);
          border-radius: 4px 18px 18px 18px;
          box-shadow: 0 1px 2px rgba(0,0,0,0.1);

          &.chat__content--generating {
            opacity: 0.7;
          }
        }
      }

      &.chat__message--error {
        .chat__content {
          background: #ffebee !important;
          color: #c62828 !important;
        }
      }

      .chat__avatar {
        width: 40px;
        height: 40px;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 1.5rem;
        flex-shrink: 0;
        background: var(--surface);
        box-shadow: 0 2px 4px rgba(0,0,0,0.1);
      }

      .chat__content {
        max-width: 80%;
        padding: 0.875rem 1.125rem;
        line-height: 1.5;
        word-wrap: break-word;
      }
    }

    .chat__typing {
      display: flex;
      gap: 4px;
      padding: 4px 0;

      span {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: var(--text-muted);
        animation: bounce 1.4s infinite ease-in-out both;

        &:nth-child(1) {
          animation-delay: -0.32s;
        }

        &:nth-child(2) {
          animation-delay: -0.16s;
        }
      }
    }

    @keyframes bounce {
      0%, 80%, 100% {
        transform: scale(0);
      }
      40% {
        transform: scale(1);
      }
    }

    @keyframes slideIn {
      from {
        opacity: 0;
        transform: translateY(10px);
      }
      to {
        opacity: 1;
        transform: translateY(0);
      }
    }

    .chat__stateRow {
      display: flex;
      align-items: flex-start;
      gap: 0.5rem;
      border-top: 1px solid var(--border);
    }

    .chat__stateAdd {
      display: flex;
      gap: 0.25rem;
      padding: 4px 4px 4px 0;
      margin: 4px;
      flex-shrink: 0;
    }

    .chat__stateInput {
      font-size: 0.75rem;
      padding: 2px 4px;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: var(--surface);
      width: 8rem;
    }

    .chat__stateBtn {
      border: 1px solid var(--border);
      border-radius: 4px;
      cursor: pointer;
      background: var(--surface);
    }

    .chat__state {
      font-size: 0.5rem;
      flex: 1;
      margin: 0;
      padding: 4px 8px;
    }

    .chat__inputRow {
      display: flex;
      gap: 0.75rem;
      padding: 1rem 1.5rem;
      background: var(--surface);
      border-top: 1px solid var(--border);

      .chat__input {
        flex: 1;
        padding: 0.875rem 1.125rem;
        border: 2px solid var(--border);
        border-radius: var(--radius-lg);
        font-size: 1rem;
        outline: none;
        transition: border-color 0.2s;

        &:focus {
          border-color: var(--brand-primary);
        }

        &:disabled {
          background: #f5f5f5;
          cursor: not-allowed;
        }
      }

      .chat__send {
        padding: 0.875rem 2rem;
        background: var(--brand-gradient);
        color: white;
        border: none;
        border-radius: var(--radius-lg);
        font-size: 1rem;
        font-weight: 600;
        cursor: pointer;
        transition: transform 0.2s, opacity 0.2s;

        &:hover:not(:disabled) {
          transform: translateY(-1px);
        }

        &:active:not(:disabled) {
          transform: translateY(0);
        }

        &:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }
      }
    }

    .chat__attach {
      flex-shrink: 0;
      width: 44px;
      border: 2px solid var(--border);
      border-radius: var(--radius-lg);
      background: var(--surface);
      font-size: 1.25rem;
      cursor: pointer;
      transition: border-color 0.2s, opacity 0.2s;

      &:hover:not(:disabled) {
        border-color: var(--brand-primary);
      }

      &:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
    }

    .chat__pending {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      padding: 0.5rem 1.5rem 0;
      background: var(--surface);
    }

    .chat__pendingChip {
      display: inline-flex;
      align-items: center;
      gap: 0.35rem;
      padding: 0.25rem 0.5rem 0.25rem 0.625rem;
      background: var(--surface-muted);
      border: 1px solid var(--border);
      border-radius: 16px;
      font-size: 0.8rem;
      color: var(--text);
    }

    .chat__pendingRemove {
      border: none;
      background: transparent;
      cursor: pointer;
      font-size: 1rem;
      line-height: 1;
      color: var(--text-muted);
      padding: 0;

      &:hover {
        color: var(--text);
      }
    }

    .chat__attachments {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      margin-top: 0.5rem;
    }

    .chat__attachmentChip {
      display: inline-flex;
      align-items: center;
      gap: 0.35rem;
      padding: 0.25rem 0.625rem;
      background: rgba(255, 255, 255, 0.2);
      border-radius: 14px;
      font-size: 0.8rem;
      color: inherit;
      text-decoration: none;

      &:hover {
        text-decoration: underline;
      }
    }

    .chat__message.chat__message--activity {
      .chat__content {
        width: 100%; //give all mcp apps max width (apps cannot really control their width anymore)
        background: #f3e5f5;
        color: #6a1b9a;
        border-radius: 4px 8px 18px 18px;
        font-size: 0.875rem;
      }
      .chat__avatar {
        background: #f3e5f5;
        color: #6a1b9a;
      }
    }

    .chat__message.chat__message--tool {
      .chat__content {
        background: #e0f7fa;
        color: #00796b;
        border-radius: 4px 8px 18px 18px;
        box-shadow: 0 1px 2px rgba(0,0,0,0.08);
      }
      .chat__avatar {
        background: #e0f7fa;
        color: #00796b;
      }
    }

    .chat__toolIndicator {
      font-size: 0.85em;
      margin-right: 0.5em;
      color: #00796b;
    }

    .chat__message.chat__message--reasoning {
      .chat__content {
        width: 100%;
        background: repeating-linear-gradient(
          -45deg,
          #f5f3ff,
          #f5f3ff 10px,
          #f8f6ff 10px,
          #f8f6ff 20px
        );
        color: #5b21b6;
        border: 1px dashed #c4b5fd;
        border-radius: 4px 8px 18px 18px;
        font-size: 0.875rem;
      }
      .chat__avatar {
        background: #f5f3ff;
        color: #5b21b6;
      }
    }

    .chat__reasoningToggle {
      display: inline-flex;
      align-items: center;
      gap: 0.4rem;
      border: none;
      background: transparent;
      padding: 0;
      cursor: pointer;
      color: inherit;
      font-size: 0.8rem;
      font-weight: 600;
      letter-spacing: 0.01em;
    }

    .chat__reasoningChevron {
      display: inline-block;
      transition: transform 0.2s ease;
      font-size: 0.7rem;

      &.chat__reasoningChevron--open {
        transform: rotate(90deg);
      }
    }

    .chat__reasoningLabel--live {
      animation: reasoningPulse 1.4s ease-in-out infinite;
    }

    @keyframes reasoningPulse {
      0% { opacity: 0.5; }
      50% { opacity: 1; }
      100% { opacity: 0.5; }
    }

    .chat__reasoningBody {
      margin-top: 0.5rem;
      padding-top: 0.5rem;
      border-top: 1px dashed #c4b5fd;
      white-space: pre-wrap;
      word-break: break-word;
      font-style: italic;
      line-height: 1.5;
      opacity: 0.92;
    }

    .chat__message.chat__message--risk {
      .chat__content {
        width: 100%;
        background: #fff3e0;
        color: #e65100;
        border: 1px solid #ffb74d;
        border-radius: 4px 8px 18px 18px;
        font-size: 0.875rem;
      }
      .chat__avatar {
        background: #fff3e0;
        color: #e65100;
      }
    }

    .chat__message.chat__message--risk-critical {
      .chat__content {
        background: #ffebee;
        color: #b71c1c;
        border-color: #ef9a9a;
      }
      .chat__avatar {
        background: #ffebee;
        color: #b71c1c;
      }
    }

    .chat__riskBadge {
      display: inline-block;
      font-weight: 700;
      font-size: 0.8rem;
      letter-spacing: 0.02em;
      text-transform: uppercase;
    }

    .chat__riskCategory {
      margin-top: 0.35rem;
      font-weight: 600;
    }

    .chat__riskReason {
      margin-top: 0.25rem;
      opacity: 0.9;
    }

    .chat__message.chat__message--approval {
      .chat__content {
        width: 100%;
        background: #e8f0fe;
        color: #1a3d7c;
        border: 1px solid #90b4f0;
        border-radius: 4px 8px 18px 18px;
        font-size: 0.875rem;
      }
      .chat__avatar {
        background: #e8f0fe;
        color: #1a3d7c;
      }
    }

    .chat__approvalBadge {
      display: inline-block;
      font-weight: 700;
      font-size: 0.8rem;
      letter-spacing: 0.02em;
      text-transform: uppercase;
    }

    .chat__approvalTool {
      margin-top: 0.35rem;
      font-weight: 600;
      font-family: monospace;
    }

    .chat__approvalArgs {
      margin: 0.35rem 0 0;
      padding: 0.5rem;
      background: rgba(255, 255, 255, 0.6);
      border-radius: 8px;
      font-size: 0.8rem;
      overflow-x: auto;
    }

    .chat__approvalActions {
      display: flex;
      gap: 0.5rem;
      margin-top: 0.6rem;
    }

    .chat__approvalBtn {
      padding: 0.4rem 0.9rem;
      border-radius: 8px;
      border: 1px solid transparent;
      font-size: 0.85rem;
      font-weight: 600;
      cursor: pointer;
      transition: filter 0.15s;

      &:hover {
        filter: brightness(0.95);
      }

      &.chat__approvalBtn--approve {
        background: #d7f3dc;
        color: #1b5e20;
        border-color: #81c784;
      }

      &.chat__approvalBtn--always {
        background: #e8f0fe;
        color: #1a3d7c;
        border-color: #90b4f0;
      }

      &.chat__approvalBtn--reject {
        background: #ffebee;
        color: #b71c1c;
        border-color: #ef9a9a;
      }
    }

    .chat__approvalDecision {
      display: inline-block;
      margin-top: 0.6rem;
      font-weight: 700;
      color: #1b5e20;

      &.chat__approvalDecision--rejected {
        color: #b71c1c;
      }
    }
  `,
})
export class ChatComponent {
  private readonly webmcp = inject(WebmcpService);
  private readonly messagesContainer = viewChild<ElementRef>('messagesContainer');

  protected readonly newMessageViewModel = signal<NewMessageViewModel>({ content: '' });
  protected readonly newMessageForm = form(this.newMessageViewModel, schemaPath => {
    required(schemaPath.content);
  });
  protected readonly messages = signal<MessageViewModel[]>([]);
  protected readonly status = signal('Ready to chat');
  protected readonly isLoading = signal(false);
  protected readonly pendingAttachments = signal<Attachment[]>([]);
  protected readonly isUploading = signal(false);
  protected readonly conversationState = signal<unknown>({ conversation: { selectedResources: [], counter: 0 } });

  // The standalone "typing" dots signal that the model is working. While a reasoning or assistant
  // bubble is actively streaming, that bubble is itself the activity indicator, so suppress the
  // redundant dots (otherwise a "Thinking…" bubble, a streaming answer and the dots show at once).
  protected readonly showTypingIndicator = computed(() => {
    if (!this.isLoading()) return false;
    const last = this.messages().at(-1);
    return !(last?.isGenerating === true && (last.role === 'assistant' || last.role === 'reasoning'));
  });

  protected readonly agents = httpResource<string[]>(() => '/agents');
  protected readonly selectedAgent = linkedSignal<string | undefined>(() => this.agents.value()?.[0]);

  // Auto-scroll effect: scroll to bottom when messages change, but only if user is already near bottom
  private autoScrollEffect = effect(() => {
    this.messages(); // Track messages changes
    this.scrollToBottomIfNearBottom();
  });

  protected onAgentChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const newAgent = select.value;
    this.selectedAgent.set(newAgent);
  }

  private onSelectedAgentChanged = effect(() => {
    const agentAlias = this.selectedAgent();
    if (agentAlias) {
      console.log('Selected agent changed:', agentAlias);
      this.messages.set([]);
      this.initializeAgent(agentAlias);
    }
  });

  private pendingClientCalls: PendingClientCall[] = [];
  protected readonly awaitingApproval = signal(false);
  /**
   * Set by `onRunFinishedEvent` when the finished run left work that should continue the
   * conversation. The continuation must not start from inside the subscriber: the SDK assigns
   * `agent.pendingInterrupts` only after every subscriber has returned, so a run started there
   * would still see the previous run's interrupts as open and be rejected by `runAgent`'s
   * "pending interrupt(s) not addressed by resume" check. `runAgent` acts on this instead.
   */
  private resumeRequested = false;
  /** Set by onRunErrorEvent so runAgent's catch does not report the same failure twice. */
  private runErrorReported = false;
  /**
   * Set by both terminal run handlers. A stream can end without either of them (see runAgent), and
   * then nothing has taken the UI out of its "running" state — this is how runAgent notices.
   */
  private runSettled = false;
  /** Set by cancelRun so a run that ends without a terminal event can still be reported as such. */
  private abortRequested = false;

  private agent?: HttpAgent;
  private initializeAgent(agentAlias: string): void {
    // Switching agents abandons whatever the old agent left open, so drop it here: stale frontend
    // tool calls must not execute against the new agent's runs, and an unanswered approval can no
    // longer be answered at all. The agent below is constructed without a `threadId`, and
    // AbstractAgent's constructor then invents one (`this.threadId = threadId ?? uuidv4()`), so the
    // new agent talks to a brand-new backend session (AGUIEndpoint resolves the session from the
    // thread id) — nothing from the old conversation is reachable from it. The abandoned card is not
    // lost data either: should that thread ever run again, ToolApprovalHistoryNormalizer's third
    // repair answers the orphaned request with a synthetic rejection.
    this.pendingClientCalls = [];
    this.resumeRequested = false;
    this.awaitingApproval.set(false);
    const agent = new HttpAgent({
      url: `/agents/routed/${agentAlias}/agui`,
      initialState: untracked(this.conversationState)
    });
    agent.subscribe({
      onTextMessageStartEvent: ({ event }) => {
        console.log('Text message started:', event);
        this.status.set('Assistant is typing...');
        this.upsertAssistantMessage(event.messageId, msg => ({ ...msg, isGenerating: true }));
      },
      onTextMessageContentEvent: ({ textMessageBuffer, event }) => {
        // textMessageBuffer holds content BEFORE this delta; append event.delta
        // to keep the streamed message from lagging one chunk behind.
        const content = textMessageBuffer + event.delta;
        this.upsertAssistantMessage(event.messageId, msg => ({ ...msg, content }));
      },
      onTextMessageEndEvent: async ({ textMessageBuffer, event }) => {
        console.log('Text message ended:', textMessageBuffer);
        this.upsertAssistantMessage(event.messageId, msg => ({
          ...msg,
          content: textMessageBuffer,
          isGenerating: false,
        }));
        this.status.set('Ready to chat');
      },
      onReasoningStartEvent: ({ event }) => {
        console.log('Reasoning started:', event);
        this.status.set('Assistant is thinking...');
      },
      onReasoningMessageContentEvent: ({ reasoningMessageBuffer, event }) => {
        // reasoningMessageBuffer holds content BEFORE this delta; append event.delta.
        const content = reasoningMessageBuffer + event.delta;
        this.upsertReasoningMessage(event.messageId, msg => ({ ...msg, content, isGenerating: true }));
      },
      onReasoningMessageEndEvent: ({ reasoningMessageBuffer, event }) => {
        // Nothing visible was streamed (e.g. encrypted/redacted thinking) — no bubble to finalize.
        if (!reasoningMessageBuffer.trim()) return;
        // Collapse the finished thought so the answer stays front and center — unless the user
        // has already toggled it themselves, in which case respect their choice.
        this.upsertReasoningMessage(event.messageId, msg => ({
          ...msg,
          content: reasoningMessageBuffer,
          isGenerating: false,
          collapsed: msg.userToggled ? msg.collapsed : true,
        }));
      },
      onReasoningEndEvent: ({ event }) => {
        console.log('Reasoning ended:', event);
      },
      onToolCallStartEvent: ({ event }) => {
        // Add a tool message to the chat for any tool call (local or backend)
        this.messages.update(msgs => [
          ...msgs,
          {
            role: 'tool',
            content: '',
            toolName: event.toolCallName,
            toolCallId: event.toolCallId
          }
        ]);
        // If it's a frontend tool, collect for execution once the run finishes
        if (this.webmcp.tools().some(t => t.name === event.toolCallName)) {
          this.pendingClientCalls.push({ kind: 'tool', id: event.toolCallId, name: event.toolCallName });
          this.status.set(`Executing ${event.toolCallName}...`);
        }
      },
      onToolCallEndEvent: async ({ toolCallName, toolCallArgs, event }) => {
        console.log('Tool call', toolCallName, toolCallArgs, event);
        this.messages.update(msgs => {
          return msgs.map(msg =>
            msg.role === 'tool' && msg.toolCallId === event.toolCallId
              ? { ...msg, toolName: `${msg.toolName}(${toolCallArgs ? JSON.stringify(toolCallArgs) : ''})` }
              : msg
          );
        });
        // No need to accumulate the argument deltas ourselves: the SDK concatenates them onto the
        // tool call it tracks and hands the parsed object over here (JSON.parse in a try/catch, so a
        // truncated stream yields `{}` rather than throwing). Hold it for the invocation below —
        // the tool must not run before the run has finished.
        const call = this.pendingClientCalls.find(tc => tc.id === event.toolCallId);
        if (call?.kind === 'tool') {
          call.args = toolCallArgs;
        }
      },
      onToolCallResultEvent: async ({ event }) => {
        console.log('Tool call result', event);
        // Server-side tools report their result as an event; frontend tools get theirs written into
        // the bubble by the invokeTool loop below. Fill in both so the two look the same in the
        // transcript instead of a server tool showing only `name(args)` with an empty body.
        this.messages.update(msgs => msgs.map(msg =>
          msg.role === 'tool' && msg.toolCallId === event.toolCallId
            ? { ...msg, content: event.content }
            : msg
        ));
      },
      onRunStartedEvent: ({ event }) => {
        console.log('Run started', event);
      },
      onRunErrorEvent: ({ event }) => {
        this.isLoading.set(false);
        this.runErrorReported = true;
        this.runSettled = true;
        // A failed/cancelled run invalidates the client calls it surfaced: results for a failed run's
        // tool calls must not be sent to a later one, and an approval card that is on screen when the
        // run dies can no longer be answered — the resume it belongs to would never reach the model.
        // Its interrupt has to be dropped from the SDK's list as well, or every later run is rejected
        // for leaving it unanswered. The request is not stranded server-side: on this thread's next
        // run ToolApprovalHistoryNormalizer's third repair answers the now-orphaned approval request
        // with a synthetic rejection, so the conversation continues (with the gated call refused).
        this.pendingClientCalls = [];
        agent.pendingInterrupts = [];
        this.awaitingApproval.set(false);
        // The client marks an aborted run with a typed code rather than leaving it to be guessed from
        // the untyped rawEvent: transformHttpEventStream turns an AbortError on the event stream into
        // `{ type: RUN_ERROR, message, code: "abort", rawEvent: err }`, and RUN_ERROR declares `code`.
        if (event.code === 'abort') {
          console.log('Run cancelled', event);
          this.status.set('Cancelled');
          this.messages.update(msgs => {
            const last = msgs.at(-1);
            return last?.role === 'assistant'
              ? [...msgs.slice(0, -1), { ...last, content: last.content + '✋', isGenerating: false }]
              : [...msgs, { role: 'assistant', content: '✋', isGenerating: false }];
          });
        } else {
          console.error('Run error', event);
          this.status.set('Error occurred');
          this.messages.update(msgs => {
            const last = msgs.at(-1);
            return last?.role === 'assistant'
              ? [...msgs.slice(0, -1), { ...last, content: event.message, isGenerating: false, error: true }]
              : [...msgs, { role: 'assistant', content: event.message, isGenerating: false, error: true }];
          });
        }
      },
      onActivitySnapshotEvent: ({ event }) => {
        const content = event.content as Record<string, unknown>;

        // EU AI Act risk classification — only emitted by the backend for High risk or above.
        if (event.activityType === 'eu-ai-act-risk') {
          this.upsertActivityMessage(event.messageId, {
            role: 'risk',
            content: '',
            messageId: event.messageId,
            risk: {
              risk: typeof content?.['risk'] === 'string' ? content['risk'] : 'Unknown',
              category: typeof content?.['category'] === 'string' ? content['category'] : '',
              reason: typeof content?.['reason'] === 'string' ? content['reason'] : '',
            },
          });
          return;
        }

        // MCP Apps — interactive UI resource rendered inline.
        if (event.activityType === 'mcp-apps') {
          const resourceUri = typeof content?.['resourceUri'] === 'string' ? content['resourceUri'] : undefined;
          const toolInput = content?.['toolInput'] as Record<string, unknown> | undefined;
          const toolResult = content?.['result'];
          this.upsertActivityMessage(event.messageId, {
            role: 'activity',
            content: '',
            resourceUri,
            messageId: event.messageId,
            toolInput,
            toolResult,
          });
          return;
        }

        console.warn('Unhandled activity snapshot type:', event.activityType);
      },
      onStateSnapshotEvent: ({ event }) => {
        // No need to return a mutation: the SDK already applies the snapshot to its own state.
        this.conversationState.set(event.snapshot);
      },
      onRunFinishedEvent: async (params) => {
        const { event } = params;
        console.log('Run finished', event.result, event);
        // Deliberately NOT unlocking the composer here. RUN_FINISHED is not the end of the work: the
        // invoke loop below still awaits every WebMCP tool, and a continuation run follows. Clearing
        // isLoading at this point let the user start a second run while this one was suspended on a
        // tool, and the second run's `runAgent` resets `runSettled`/`resumeRequested` — after which
        // THIS run took the "no terminal event" recovery branch and discarded the tool result it had
        // just computed, so the tool ran (side effects and all) and the model never heard the outcome.
        // runAgent's finally clears isLoading, and on the continuation path maybeResumeRun reaches
        // runAgent synchronously, so nothing is left stuck and there is no window in between.
        this.runSettled = true;
        agent.setMessages([]); // server supports session management, no need to resend history

        // An interrupt outcome is the protocol's human-in-the-loop pause: the backend stopped before
        // a gated tool call and waits for a decision, which travels back as a `resume` entry.
        if (params.outcome === 'interrupt') {
          for (const interrupt of params.interrupts) {
            this.addApprovalRequest(interrupt);
          }
        }

        // Execute pending frontend tool calls right away, holding each result on its entry;
        // approval entries resolve later in onApprovalDecision. maybeResumeRun re-runs once
        // no call is left unresolved.
        for (const call of this.pendingClientCalls) {
          if (call.kind !== 'tool') {
            continue;
          }
          try {
            // `args` is unset only if TOOL_CALL_END never arrived for this call; invoking with no
            // arguments still lets the tool reply with its own validation error, which is more useful
            // to the model than a silently skipped call.
            const invokeToolResponse = await this.webmcp.invokeTool(call.name, call.args);
            call.result = typeof invokeToolResponse === 'string'
              ? invokeToolResponse
              : JSON.stringify(invokeToolResponse);
          } catch (error) {
            call.result = 'Error: Tool execution failed.';
          }

          // Update the tool message with the result
          this.messages.update(msgs => {
            return msgs.map(msg =>
              msg.toolCallId === call.id
                ? { ...msg, content: call.result! }
                : msg
            );
          });
        }

        // Hand the continuation to runAgent — see resumeRequested for why not from here.
        this.resumeRequested = true;
      }
    });

    this.agent = agent;
  }

  /**
   * Renders an approval card for an open interrupt and queues it for the resume that answers it.
   * The pending tool call rides on the interrupt's metadata, so the card needs no extra round-trip.
   * An interrupt that fails either check below is declined rather than ignored — see declineInterrupt.
   */
  private addApprovalRequest(interrupt: Interrupt): void {
    if (this.pendingClientCalls.some(call => call.id === interrupt.id)) {
      return;
    }

    // Only `confirmation` interrupts are approvals. The backend picks that reason deliberately over
    // `tool_call` (ToolApprovalInterruptMiddleware) because a call awaiting approval has not been
    // streamed, so a client cannot correlate it with anything it has seen. Rendering some future
    // interrupt kind as an approval card would ask the user to approve a call that isn't there.
    if (interrupt.reason !== 'confirmation') {
      this.declineInterrupt(interrupt, `An unsupported interrupt (${interrupt.reason}) arrived.`);
      return;
    }

    // `metadata` is untyped on the wire (`Record<string, any>`), so validate it as strictly as the
    // activity payloads are validated in onActivitySnapshotEvent — `name` is what the card claims is
    // being approved and `arguments` is fed to the `| json` pipe.
    const toolCall = this.parseApprovalToolCall(interrupt.metadata?.['toolCall']);
    if (!toolCall) {
      // The backend always puts the pending call on the metadata; without it there is nothing to
      // approve and nothing meaningful to echo back, so surface it instead of inventing a call.
      this.declineInterrupt(interrupt, 'An approval request arrived without the tool call it refers to.');
      return;
    }

    this.pendingClientCalls.push({ kind: 'approval', id: interrupt.id, toolCall });
    this.messages.update(msgs => [...msgs, {
      role: 'approval',
      content: '',
      interruptId: interrupt.id,
      approval: { toolName: toolCall.name, args: toolCall.arguments ?? {} },
    }]);
    this.status.set('Waiting for your approval…');
  }

  /**
   * Validates the pending call the backend puts on `interrupt.metadata`. `callId` and `name` must
   * really be strings and `arguments`, if present, a plain object: the `toolCall` is echoed back
   * field-for-field in the resume payload and the AG-UI server SDK rebuilds the approval — and with
   * it the call that gets executed — from that echo (see ToolApprovalHistoryNormalizer's second repair,
   * where the re-supplied request supersedes the stored one). Coercing a malformed `arguments` to
   * `{}` here would therefore not be a display fallback but a silent rewrite of the approved call, so
   * anything unexpected fails the whole approval instead.
   */
  private parseApprovalToolCall(value: unknown): ApprovalToolCall | undefined {
    if (!isPlainObject(value)) {
      return undefined;
    }
    const { callId, name, arguments: args } = value;
    if (typeof callId !== 'string' || typeof name !== 'string') {
      return undefined;
    }
    // `arguments` is optional on the wire — a parameterless call serializes it as absent or null.
    if (args !== undefined && args !== null && !isPlainObject(args)) {
      return undefined;
    }
    // Rebuilt rather than passed through, so the echo carries only these three fields. That is the
    // whole of what the server can read back anyway: the SDK deserializes the payload's `toolCall`
    // into AGUIToolCallInfo, which models `callId`, `name` and `arguments` and nothing else.
    return { callId, name, ...(isPlainObject(args) ? { arguments: args } : {}) };
  }

  /**
   * Answers an interrupt this client cannot act on with a `cancelled` resume entry, and says so in
   * the transcript. Dropping it instead would poison the thread: defaultApplyEvents assigns
   * `agent.pendingInterrupts` from the run-finished event *after* the subscribers return, so an
   * interrupt nothing queued still counts as open, maybeResumeRun sees no pending call and never
   * resumes, and the user's next message is rejected by `runAgent` with "Thread has N pending
   * interrupt(s) not addressed by resume" — a burnt turn showing only the generic error.
   */
  private declineInterrupt(interrupt: Interrupt, message: string): void {
    console.error('Declining an interrupt this client cannot answer:', message, interrupt);
    this.pendingClientCalls.push({ kind: 'cancelledInterrupt', id: interrupt.id });
    this.messages.update(msgs => [...msgs, { role: 'assistant', content: message, error: true }]);
  }

  /**
   * Records the user's decision on an approval card, then resumes the run via
   * maybeResumeRun once every pending client call is resolved.
   */
  protected async onApprovalDecision(interruptId: string, decision: ApprovalDecision): Promise<void> {
    const call = this.pendingClientCalls.find(tc => tc.id === interruptId);
    if (call?.kind !== 'approval' || call.decision) {
      return;
    }
    call.decision = decision;
    this.messages.update(msgs => msgs.map(msg =>
      msg.role === 'approval' && msg.interruptId === interruptId && msg.approval
        ? { ...msg, approval: { ...msg.approval, decision } }
        : msg
    ));
    await this.maybeResumeRun();
  }

  /**
   * Resumes the paused run once every pending client call is resolved — a result for frontend
   * tools, a decision for approvals, nothing for a declined interrupt. Tool results go back as tool
   * messages, decisions and declines as `resume` entries. Until then (i.e. while approvals await the
   * user, since tool calls resolve within the run-finished handler) the composer stays locked.
   *
   * Failures are handled here rather than at the call sites because the template calls
   * onApprovalDecision without awaiting it: a throw escaping this method on that path is an
   * unhandled rejection nobody reports, and it would leave `awaitingApproval` set — a composer
   * locked behind an approval card that has already been answered.
   */
  private async maybeResumeRun(): Promise<void> {
    try {
      if (this.pendingClientCalls.length === 0) {
        this.awaitingApproval.set(false);
        this.status.set('Ready to chat');
        return;
      }
      // Nothing may resume while any call is open: an approval without a decision, or a frontend tool
      // whose result has not landed. The tool half guards a re-entrancy window that exists in
      // principle — addApprovalRequest renders its card, buttons live, *before* the run-finished
      // handler awaits the invokeTool loop, so a click could land mid-loop and resume with
      // `content: undefined` for a call still running. Today it cannot: the backend never surfaces an
      // approval and a WebMCP tool call in the same run, because FICC escalation makes them siblings
      // and ToolApprovalAgent then defers one of the two to the next run (see human-in-the-loop.md
      // and ToolApprovalSiblingEscalationTests). That is a backend property, so keep the invariant
      // enforced here too.
      const unresolved = this.pendingClientCalls.some(call =>
        (call.kind === 'approval' && !call.decision) || (call.kind === 'tool' && call.result === undefined));
      if (unresolved) {
        this.awaitingApproval.set(true);
        this.status.set('Awaiting your approval');
        return;
      }

      const toolMessages: Message[] = this.pendingClientCalls
        .filter(call => call.kind === 'tool')
        .map(call => ({ id: call.id, role: 'tool', content: call.result!, toolCallId: call.id }));

      // A decision resolves its interrupt; one this client could not make sense of is cancelled
      // rather than left hanging, since buildResumeArray insists on an answer for every open one.
      const responses: Record<string, ResumeResponse> = {};
      for (const call of this.pendingClientCalls) {
        if (call.kind === 'approval') {
          responses[call.id] = { status: 'resolved', payload: this.buildApprovalPayload(call) };
        } else if (call.kind === 'cancelledInterrupt') {
          responses[call.id] = { status: 'cancelled' };
        }
      }
      // The interrupts come from the SDK's own list of what is still open rather than a copy kept
      // here, so buildResumeArray's "a response for every open interrupt and nothing else" check is
      // a real check — it is the same invariant runAgent enforces before it starts the next run.
      const openInterrupts = this.agent?.pendingInterrupts ?? [];
      const resume = Object.keys(responses).length
        ? buildResumeArray(openInterrupts, responses)
        : undefined;

      this.pendingClientCalls = [];
      this.awaitingApproval.set(false);
      this.agent?.setMessages([]); // server supports session management, no need to resend history
      if (toolMessages.length) {
        this.agent?.addMessages(toolMessages);
      }
      await this.runAgent(resume);
    } catch (error) {
      // buildResumeArray throws if the responses and the SDK's open interrupts ever disagree.
      // Surface it instead of leaving the composer locked behind awaitingApproval.
      console.error('Error resuming run:', error);
      this.messages.update(msgs => [...msgs, {
        role: 'assistant',
        content: 'Sorry, the conversation could not be resumed. Please try again.',
        error: true,
      }]);
      this.status.set('Error occurred');
      this.resetPendingWork();
    }
  }

  /**
   * Builds the resume payload for an approval interrupt: the decision plus the tool call echoed
   * back field-for-field (as parseApprovalToolCall rebuilt it), which is what lets the backend
   * rebuild the approval without keeping correlation state between the two runs. `alwaysApprove`
   * asks it to remember a standing rule.
   */
  private buildApprovalPayload(call: Extract<PendingClientCall, { kind: 'approval' }>): unknown {
    return {
      toolCall: call.toolCall,
      approved: call.decision !== 'rejected',
      alwaysApprove: call.decision === 'always' ? 'tool' : null,
    };
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();

    const newMessage = this.newMessageViewModel().content.trim();
    const attachments = this.pendingAttachments();
    // While an approval is pending the run is paused mid-tool-call; a fresh user message
    // would leave the approval unanswered, so submission is blocked until the user decides.
    if ((!newMessage && attachments.length === 0) || this.isLoading() || this.awaitingApproval()) {
      return;
    }

    // The local view model keeps clean text + structured attachments (no marker).
    // Only the wire payload carries the hidden marker that the backend middleware resolves:
    // it strips the marker, appends a model-visible "[Attached files: ...]" line, and stores
    // the file paths in history (out of the model's view).
    const marker = attachments.length
      ? `\n[[agui-attachments:${attachments.map(a => a.fileId).join(',')}]]`
      : '';

    this.newMessageViewModel.update(vm => ({ ...vm, content: '' }));
    this.pendingAttachments.set([]);
    this.messages.update(msgs => [...msgs, { role: 'user', content: newMessage, attachments }]);
    this.agent?.addMessages([{ id: "", role: 'user', content: newMessage + marker }]);
    this.scrollToBottom();

    await this.runAgent();
  }

  protected async onFilesSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = input.files;
    if (!files?.length) {
      return;
    }

    this.isUploading.set(true);
    this.status.set('Uploading files...');
    try {
      const formData = new FormData();
      for (const file of Array.from(files)) {
        formData.append('files', file, file.name);
      }
      // Do not set Content-Type manually — the browser sets the multipart boundary.
      const response = await fetch('/agents/files', { method: 'POST', body: formData });
      if (!response.ok) {
        throw new Error(`Upload failed: ${response.status}`);
      }
      const uploaded = await response.json() as Attachment[];
      this.pendingAttachments.update(a => [...a, ...uploaded]);
      this.status.set('Ready to chat');
    } catch (error) {
      console.error('Error uploading files:', error);
      this.status.set('Upload failed');
    } finally {
      this.isUploading.set(false);
      input.value = ''; // allow re-selecting the same file
    }
  }

  protected removePending(fileId: string): void {
    this.pendingAttachments.update(a => a.filter(att => att.fileId !== fileId));
  }

  private async runAgent(resume?: ResumeEntry[]): Promise<void> {
    this.isLoading.set(true);
    this.status.set('Agent thinking...');
    this.resumeRequested = false;
    this.runErrorReported = false;
    this.runSettled = false;
    this.abortRequested = false;

    try {
      const parameters: RunAgentParameters = {
        tools: this.webmcp.tools().map(t => ({
          name: t.name,
          description: t.description,
          parameters: t.inputSchema,
        })),
        ...(resume?.length ? { resume } : {}),
      };
      await this.agent?.runAgent(parameters);
    } catch (error) {
      console.error('Error running agent:', error);
      // onRunErrorEvent already rendered the server's own message, so only report when it did not —
      // otherwise a cancelled or server-errored run shows both its own message and this generic one.
      if (!this.runErrorReported) {
        this.messages.update(msgs => [...msgs, {
          role: 'assistant',
          content: 'Sorry, an error occurred. Please try again.',
          error: true,
        }]);
        this.status.set('Error occurred');
      }
      // Same reset as onRunErrorEvent, which does not fire for a failure raised by runAgent itself
      // (e.g. a rejected run because an interrupt went unanswered). Without clearing the interrupts
      // every later run would be rejected for the same reason.
      this.resetPendingWork();
      return;
    } finally {
      // The stream can also end without RUN_FINISHED or RUN_ERROR — a failure after the response
      // started can only abort the body, and the client resolves rather than rejects on that path.
      // Without this the composer would stay stuck showing "Agent thinking…".
      this.isLoading.set(false);
    }

    // Neither terminal event arrived, so nothing has undone what starting the run set up. This is
    // what an abort looks like when it lands before the response headers do: the HTTP observable
    // itself fails, and only a failure of the already-parsing event stream is turned into a
    // `code: "abort"` RUN_ERROR — this earlier one reaches runAgent's own error handler, which
    // deliberately swallows abort errors and resolves instead of rejecting, so the catch above does
    // not see it either. A truncated event stream ends up here too: parseSSEStream's
    // `complete: () => subject.complete()` ends the observable with no terminal event, and there is
    // no synthetic one to stand in for it. Left alone, the status would sit on "Canceling…" forever
    // and a half-streamed bubble would keep pulsing as if it were still being written.
    if (!this.runSettled) {
      this.status.set(this.abortRequested ? 'Cancelled' : 'Ready to chat');
      this.messages.update(msgs => msgs.map(msg => msg.isGenerating ? { ...msg, isGenerating: false } : msg));
      // A run that ends this way invalidates its pending work exactly like the two settled failure
      // paths do (onRunErrorEvent inline, the catch above via this same method), so drop it here as
      // well — otherwise it leaks into the next run. A tool entry whose TOOL_CALL_START streamed
      // before the stream was cut still has `result === undefined`, so the NEXT run's
      // onRunFinishedEvent invoke loop would execute that stale WebMCP tool and post its result as
      // `{ role: 'tool', toolCallId: <the dead run's id> }`. And on a resume run the interrupt this
      // run was answering is still in `agent.pendingInterrupts` — onInitialize only checks that every
      // open interrupt is addressed, the list is reassigned from RUN_FINISHED — so the user's next
      // message would be rejected with "Thread has N pending interrupt(s) not addressed by resume":
      // the burnt turn declineInterrupt exists to prevent.
      // Clearing `resumeRequested` along with the rest cannot swallow a continuation: only
      // onRunFinishedEvent sets it, and that handler sets `runSettled` too, so it is already false
      // on this path and the check below is unaffected.
      this.resetPendingWork();
    }

    // The run has fully settled here, so agent.pendingInterrupts is up to date and a follow-up
    // run is safe to start. maybeResumeRun reports its own failures — see its doc comment.
    if (this.resumeRequested) {
      this.resumeRequested = false;
      await this.maybeResumeRun();
    }
  }

  /** Drops everything a failed run left behind, so the next run is not rejected for its sake. */
  private resetPendingWork(): void {
    this.pendingClientCalls = [];
    this.resumeRequested = false;
    this.awaitingApproval.set(false);
    if (this.agent) {
      this.agent.pendingInterrupts = [];
    }
  }

  protected addSelectedResource(value: string): void {
    const trimmed = value.trim();
    if (!trimmed) return;
    this.conversationState.update((s: any) => ({
      ...s,
      conversation: {
        ...s.conversation,
        selectedResources: [...(s.conversation?.selectedResources ?? []), trimmed]
      }
    }));
    this.agent?.setState(this.conversationState());
  }

  protected cancelRun(): void {
    if (!this.isLoading() || !this.agent) {
      return;
    }

    try {
      this.status.set('Canceling...');
      this.abortRequested = true;
      this.agent.abortRun();
    } catch (error) {
      console.error('Error aborting agent run:', error);
    }
  }

  /**
   * Routes a TEXT_MESSAGE_* event to its own bubble, keyed by messageId exactly like reasoning is.
   * Positional matching ("the last assistant bubble still generating") would be wrong in principle:
   * `verifyEvents` tracks active text messages in a map keyed by id and only rejects a *duplicate*
   * id, so two text messages may legitimately be open at once, and interleaved deltas would then all
   * pile into whichever bubble happened to come last. Creating on demand is belt-and-braces (the same
   * verifier rejects a TEXT_MESSAGE_CONTENT whose START never arrived, so the bubble does exist) and
   * keeps this symmetrical with upsertReasoningMessage, where no start handler opens the bubble.
   */
  private upsertAssistantMessage(messageId: string, updateFn: (msg: MessageViewModel) => MessageViewModel): void {
    this.messages.update(msgs => {
      const idx = msgs.findIndex(m => m.role === 'assistant' && m.messageId === messageId);
      if (idx < 0) {
        return [...msgs, updateFn({ role: 'assistant', content: '', isGenerating: true, messageId })];
      }
      return [...msgs.slice(0, idx), updateFn(msgs[idx]), ...msgs.slice(idx + 1)];
    });
  }

  private upsertReasoningMessage(messageId: string, updateFn: (msg: MessageViewModel) => MessageViewModel): void {
    this.messages.update(msgs => {
      const idx = msgs.findIndex(m => m.role === 'reasoning' && m.messageId === messageId);
      if (idx < 0) {
        return [...msgs, updateFn({ role: 'reasoning', content: '', isGenerating: true, collapsed: false, messageId })];
      }
      return [...msgs.slice(0, idx), updateFn(msgs[idx]), ...msgs.slice(idx + 1)];
    });
  }

  protected toggleReasoning(messageId: string | undefined): void {
    if (messageId === undefined) return;
    // Mark userToggled so a completing reasoning block won't auto-collapse out from under the reader.
    this.messages.update(msgs => msgs.map(m =>
      m.role === 'reasoning' && m.messageId === messageId
        ? { ...m, collapsed: !m.collapsed, userToggled: true }
        : m
    ));
  }

  // Activity snapshots (MCP apps, EU AI Act risk) replace in place when re-sent with the same
  // messageId, otherwise they are appended.
  private upsertActivityMessage(messageId: string | undefined, vm: MessageViewModel): void {
    const existing = messageId === undefined ? -1 : this.messages().findIndex(
      m => (m.role === 'activity' || m.role === 'risk') && m.messageId === messageId
    );
    if (existing >= 0) {
      this.messages.update(msgs => msgs.map((m, i) => i === existing ? vm : m));
    } else {
      this.messages.update(msgs => [...msgs, vm]);
    }
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      const container = this.messagesContainer()?.nativeElement;
      if (container) {
        container.scrollTop = container.scrollHeight;
      }
    }, 100);
  }

  private isNearBottom(): boolean {
    const container = this.messagesContainer()?.nativeElement;
    if (!container) return true;

    const threshold = 10; // pixels from bottom
    const position = container.scrollTop + container.clientHeight;
    const height = container.scrollHeight;

    return position >= height - threshold;
  }

  private scrollToBottomIfNearBottom(): void {
    if (this.isNearBottom()) {
      this.scrollToBottom();
    }
  }
}
