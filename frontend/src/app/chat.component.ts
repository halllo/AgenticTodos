import { ChangeDetectionStrategy, Component, computed, effect, ElementRef, inject, linkedSignal, resource, signal, untracked, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { HttpAgent, Message, RunAgentParameters } from "@ag-ui/client"
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

interface ApprovalViewModel {
  id: string;
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
  activityType?: string;
  resourceUri?: string;
  messageId?: string;
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
                        (click)="onApprovalDecision(message.toolCallId!, 'approved')">✓ Approve</button>
                      <button type="button" class="chat__approvalBtn chat__approvalBtn--always"
                        title="Approve and don't ask again for this tool in this conversation"
                        (click)="onApprovalDecision(message.toolCallId!, 'always')">∞ Always allow</button>
                      <button type="button" class="chat__approvalBtn chat__approvalBtn--reject"
                        (click)="onApprovalDecision(message.toolCallId!, 'rejected')">✕ Reject</button>
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

    .chat__activityContent {
      font-size: 0.7rem;
      margin: 0.25rem 0 0;
      white-space: pre-wrap;
      word-break: break-all;
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

  private pendingFrontendToolCalls: Array<{ id: string, name: string, args: string }> = [];
  // Approval requests surfaced by the backend as synthetic `request_approval` tool calls
  // (see human-in-the-loop.md). Unlike frontend tools they are not auto-executed on run
  // finish — the run pauses until the user decides on every pending request.
  private pendingApprovalCalls: Array<{ id: string, args: string, decision?: ApprovalDecision }> = [];
  protected readonly awaitingApproval = signal(false);

  private agent?: HttpAgent;
  private initializeAgent(agentAlias: string): void {
    // Switching agents discards any in-flight approval state; the backend keeps pending
    // approvals in its per-conversation session queue and re-presents them when asked again.
    this.pendingApprovalCalls = [];
    this.awaitingApproval.set(false);
    const agent = new HttpAgent({
      url: `/agents/routed/${agentAlias}/agui`,
      initialState: untracked(this.conversationState)
    });
    agent.subscribe({
      onTextMessageStartEvent: ({ event }) => {
        console.log('Text message started:', event);
        this.status.set('Assistant is typing...');
        this.messages.update(msgs => ([...msgs, { role: 'assistant', content: '', isGenerating: true }]));
      },
      onTextMessageContentEvent: ({ textMessageBuffer, event }) => {
        // textMessageBuffer holds content BEFORE this delta; append event.delta
        // to keep the streamed message from lagging one chunk behind.
        const content = textMessageBuffer + event.delta;
        this.updateLastAssistantMessage(
          msg => ({ ...msg, content }),
          { role: 'assistant', content }
        );
      },
      onTextMessageEndEvent: async ({ textMessageBuffer }) => {
        console.log('Text message ended:', textMessageBuffer);
        this.updateLastAssistantMessage(
          msg => ({ ...msg, content: textMessageBuffer, isGenerating: false }),
          { role: 'assistant', content: textMessageBuffer, isGenerating: false }
        );
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
        // Approval request: render a card instead of a tool bubble; do not auto-execute.
        if (event.toolCallName === 'request_approval') {
          this.messages.update(msgs => [
            ...msgs,
            { role: 'approval', content: '', toolCallId: event.toolCallId }
          ]);
          this.pendingApprovalCalls.push({ id: event.toolCallId, args: '' });
          this.status.set('Waiting for your approval…');
          return;
        }
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
        // If it's a frontend tool, collect for batch execution
        if (this.webmcp.tools().some(t => t.name === event.toolCallName)) {
          this.pendingFrontendToolCalls.push({ id: event.toolCallId, name: event.toolCallName, args: '' });
          this.status.set(`Executing ${event.toolCallName}...`);
        }
      },
      onToolCallArgsEvent: ({ event }) => {
        // Find the matching pending frontend tool or approval call and append args
        const call = this.pendingFrontendToolCalls.find(tc => tc.id === event.toolCallId)
          ?? this.pendingApprovalCalls.find(tc => tc.id === event.toolCallId);
        if (call) {
          call.args += event.delta || '';
        }
      },
      onToolCallEndEvent: async ({ toolCallName, toolCallArgs, event }) => {
        console.log('Tool call', toolCallName, toolCallArgs, event);
        // Approval request complete: parse the payload and populate the card.
        const approvalCall = this.pendingApprovalCalls.find(tc => tc.id === event.toolCallId);
        if (approvalCall) {
          let parsed: any = {};
          try {
            parsed = approvalCall.args ? JSON.parse(approvalCall.args) : {};
          } catch {
            parsed = {};
          }
          this.messages.update(msgs => msgs.map(msg =>
            msg.role === 'approval' && msg.toolCallId === approvalCall.id
              ? {
                  ...msg,
                  approval: {
                    id: parsed.id ?? approvalCall.id,
                    toolName: parsed.tool_call?.name ?? 'unknown tool',
                    args: parsed.tool_call?.arguments ?? {},
                  }
                }
              : msg
          ));
          return;
        }
        this.messages.update(msgs => {
          return msgs.map(msg =>
            msg.role === 'tool' && msg.toolCallId === event.toolCallId
              ? { ...msg, toolName: `${msg.toolName}(${toolCallArgs ? JSON.stringify(toolCallArgs) : ''})` }
              : msg
          );
        });
        // Do not execute tool here; wait until run finishes
      },
      onToolCallResultEvent: async ({ event }) => {
        console.log('Tool call result', event);
      },
      onRunStartedEvent: ({ event }) => {
        console.log('Run started', event);
      },
      onRunErrorEvent: ({ event }) => {
        this.isLoading.set(false);
        // A failed/cancelled run invalidates any approval requests it surfaced — the backend
        // re-presents pending approvals from its session queue on the next run.
        this.pendingApprovalCalls = [];
        this.awaitingApproval.set(false);
        if (this.isAbortError(event.rawEvent)) {
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
            activityType: event.activityType,
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
            content: JSON.stringify(content, null, 2),
            activityType: event.activityType,
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
        this.conversationState.set(event.snapshot);
        return { state: event.snapshot };
      },
      onRunFinishedEvent: async ({ event }) => {
        console.log('Run finished', event.result, event);
        this.isLoading.set(false);

        // Batch execute all pending frontend tool calls
        if (this.pendingFrontendToolCalls.length > 0) {
          const toolMessages: Message[] = [];
          for (const call of this.pendingFrontendToolCalls) {
            let parsedArgs: Record<string, any> = {};
            try {
              const parsed: unknown = call.args ? JSON.parse(call.args) : {};
              parsedArgs = (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
                ? (parsed as Record<string, any>)
                : {};
            } catch {
              parsedArgs = {};
            }

            let result: string = '';
            try {
              const invokeToolResponse = await this.webmcp.invokeTool(call.name, parsedArgs);
              result = typeof invokeToolResponse === 'string'
                ? invokeToolResponse
                : JSON.stringify(invokeToolResponse);
            } catch (error) {
              result = 'Error: Tool execution failed.';
            }

            // Update the tool message with the result
            this.messages.update(msgs => {
              return msgs.map(msg =>
                msg.toolCallId === call.id
                  ? { ...msg, content: result }
                  : msg
              );
            });
            toolMessages.push({
              id: call.id,
              role: "tool",
              content: result,
              toolCallId: call.id,
            });
          }
          this.pendingFrontendToolCalls = [];
          this.agent?.setMessages([]); // server supports session management, no need to resend history
          this.agent?.addMessages(toolMessages);
          await this.runAgent();
        } else if (this.pendingApprovalCalls.length > 0) {
          // Approval pending: do NOT auto-run — the run resumes in onApprovalDecision once
          // the user has decided on every pending request.
          this.agent?.setMessages([]); // server supports session management, no need to resend history
          this.awaitingApproval.set(true);
          this.status.set('Awaiting your approval');
        } else {
          this.agent?.setMessages([]); // server supports session management, no need to resend history
          this.status.set('Ready to chat');
        }
      }
    });

    this.agent = agent;
  }

  /**
   * Records the user's decision on an approval card. Once every pending approval of the run
   * has a decision, sends one tool-result message per request (echoing the request payload,
   * plus `approved` and the optional `always_approve` rule scope) and resumes the run — the
   * same mechanism the WebMCP frontend-tool round-trip uses.
   */
  protected async onApprovalDecision(toolCallId: string, decision: ApprovalDecision): Promise<void> {
    const call = this.pendingApprovalCalls.find(tc => tc.id === toolCallId);
    if (!call || call.decision) {
      return;
    }
    call.decision = decision;
    this.messages.update(msgs => msgs.map(msg =>
      msg.role === 'approval' && msg.toolCallId === toolCallId && msg.approval
        ? { ...msg, approval: { ...msg.approval, decision } }
        : msg
    ));

    // The backend surfaces approvals one at a time, but handle multiple cards defensively:
    // the resumed run must answer every pending request.
    if (this.pendingApprovalCalls.some(tc => !tc.decision)) {
      return;
    }

    const toolMessages: Message[] = this.pendingApprovalCalls.map(tc => {
      let request: Record<string, unknown> = {};
      try {
        request = tc.args ? JSON.parse(tc.args) : {};
      } catch {
        request = {};
      }
      const response = {
        ...request, // echoes id + tool_call verbatim so the backend can reconstruct the approval
        approved: tc.decision !== 'rejected',
        reason: null,
        always_approve: tc.decision === 'always' ? 'tool' : null,
      };
      return {
        id: tc.id,
        role: 'tool',
        content: JSON.stringify(response),
        toolCallId: tc.id,
      };
    });
    this.pendingApprovalCalls = [];
    this.awaitingApproval.set(false);
    this.agent?.setMessages([]); // server supports session management, no need to resend history
    this.agent?.addMessages(toolMessages);
    await this.runAgent();
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

  private async runAgent(): Promise<void> {
    this.isLoading.set(true);
    this.status.set('Agent thinking...');

    try {
      const parameters: RunAgentParameters = {
        tools: this.webmcp.tools().map(t => ({
          name: t.name,
          description: t.description,
          parameters: t.inputSchema,
        }))
      };
      await this.agent?.runAgent(parameters);
    } catch (error) {
      console.error('Error running agent:', error);
      this.messages.update(msgs => [...msgs, {
        role: 'assistant',
        content: 'Sorry, an error occurred. Please try again.'
      }]);
      this.status.set('Error occurred');
      this.isLoading.set(false);
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
      this.agent.abortRun();
    } catch (error) {
      console.error('Error aborting agent run:', error);
    }
  }

  private isAbortError(error: unknown): boolean {
    if (error && typeof error === 'object') {
      const anyError = error as { name?: unknown; message?: unknown };
      const name = typeof anyError.name === 'string' ? anyError.name : '';
      const message = typeof anyError.message === 'string' ? anyError.message : '';
      return name === 'AbortError';
    }
    return false;
  }

  private updateLastAssistantMessage(updateFn: (msg: MessageViewModel) => MessageViewModel, fallback: MessageViewModel): void {
    this.messages.update(msgs => {
      const lastIdx = msgs
        .slice()
        .map((v, i) => ({ v, i }))
        .reverse()
        .filter(({ v }) => v.role === 'assistant' && v.isGenerating)
        .map(({ i }) => i)
        .at(0)
        ;
      return lastIdx === undefined
        ? [...msgs, fallback]
        : [
          ...msgs.slice(0, lastIdx),
          updateFn(msgs[lastIdx]),
          ...msgs.slice(lastIdx + 1)
        ];
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
