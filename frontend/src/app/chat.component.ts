import { ChangeDetectionStrategy, Component, ElementRef, OnInit, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentSubscriber, HttpAgent, Message as Message } from "@ag-ui/client"

interface UIMessage {
  role: 'user' | 'assistant';
  content: string;
}

@Component({
  selector: 'app-chat',
  imports: [FormsModule],
  template: `
    <div class="chat-container">
      <header class="chat-header">
        <h1>Assistant</h1>
        <div class="status" [class.active]="!isLoading()">
          {{ status() }}
        </div>
      </header>

      <div class="messages-container" #messagesContainer>
        @if (messages().length === 0) {
          <div class="empty-state">
            <p>Start a conversation with your AI assistant</p>
          </div>
        }
        
        @for (message of messages(); track $index) {
          <div class="message" [class.user]="message.role === 'user'" [class.assistant]="message.role === 'assistant'">
            <div class="message-avatar">
              {{ message.role === 'user' ? '👤' : '🤖' }}
            </div>
            <div class="message-content">
              {{ message.content }}
            </div>
          </div>
        }

        @if (isLoading()) {
          <div class="message assistant">
            <div class="message-avatar">🤖</div>
            <div class="message-content">
              <div class="typing-indicator">
                <span></span>
                <span></span>
                <span></span>
              </div>
            </div>
          </div>
        }
      </div>

      <form class="input-container" (submit)="onSubmit($event)">
        <input 
          type="text" 
          [(ngModel)]="inputMessage"
          name="message"
          placeholder="Type your message..."
          [disabled]="isLoading()"
          class="message-input"
        />
        <button 
          type="submit" 
          [disabled]="isLoading() || !inputMessage.trim()"
          class="send-button"
        >
          Send
        </button>
      </form>
    </div>
  `,
  styles: `
    :host {
      flex: 1;
    }

    .chat-container {
      display: flex;
      flex-direction: column;
      height: 100%;
      background: #fff;
    }

    .chat-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 1rem 1.5rem;
      background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
      color: white;
      box-shadow: 0 2px 4px rgba(0,0,0,0.1);

      h1 {
        margin: 0;
        font-size: 1.5rem;
        font-weight: 600;
      }

      .status {
        font-size: 0.875rem;
        padding: 0.5rem 1rem;
        border-radius: 20px;
        background: rgba(255, 255, 255, 0.2);
        
        &.active {
          background: rgba(76, 175, 80, 0.3);
        }
      }
    }

    .messages-container {
      flex: 1;
      overflow-y: auto;
      padding: 1.5rem;
      background: #f5f5f5;

      .empty-state {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100%;
        color: #999;
        font-size: 1.125rem;
      }
    }

    .message {
      display: flex;
      gap: 0.75rem;
      margin-bottom: 1.5rem;
      animation: slideIn 0.3s ease-out;

      &.user {
        flex-direction: row-reverse;

        .message-content {
          background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
          color: white;
          border-radius: 18px 18px 4px 18px;
        }
      }

      &.assistant {
        .message-content {
          background: white;
          color: #333;
          border-radius: 18px 18px 18px 4px;
          box-shadow: 0 1px 2px rgba(0,0,0,0.1);
        }
      }

      .message-avatar {
        width: 40px;
        height: 40px;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 1.5rem;
        flex-shrink: 0;
        background: white;
        box-shadow: 0 2px 4px rgba(0,0,0,0.1);
      }

      .message-content {
        max-width: 70%;
        padding: 0.875rem 1.125rem;
        line-height: 1.5;
        word-wrap: break-word;
      }
    }

    .typing-indicator {
      display: flex;
      gap: 4px;
      padding: 4px 0;

      span {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: #999;
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

    .input-container {
      display: flex;
      gap: 0.75rem;
      padding: 1rem 1.5rem;
      background: white;
      border-top: 1px solid #e0e0e0;

      .message-input {
        flex: 1;
        padding: 0.875rem 1.125rem;
        border: 2px solid #e0e0e0;
        border-radius: 24px;
        font-size: 1rem;
        outline: none;
        transition: border-color 0.2s;

        &:focus {
          border-color: #667eea;
        }

        &:disabled {
          background: #f5f5f5;
          cursor: not-allowed;
        }
      }

      .send-button {
        padding: 0.875rem 2rem;
        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        color: white;
        border: none;
        border-radius: 24px;
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
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatComponent implements OnInit {
  protected readonly AGENT_URL = '/agui';
  protected readonly messages = signal<UIMessage[]>([]);
  protected readonly status = signal('Ready to chat');
  protected readonly isLoading = signal(false);
  protected inputMessage = '';

  readonly backgroundColorChange = output<string>();

  private readonly messagesContainer = viewChild<ElementRef>('messagesContainer');
  private agent!: HttpAgent;
  private pendingToolCall: { id: string, name: string, args: string } | null = null;
  private toolResultMessages: Message[] = [];
  private readonly tools = [
    {
      name: "change_background_color",
      description: "Change the left panel background color. Can accept solid colors (e.g., '#1e3a8a', 'red') or gradients (e.g., 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)').",
      parameters: {
        type: "object",
        properties: {
          color: {
            type: "string",
            description: "The background color or gradient to apply to the left panel. Can be a hex color, named color, or CSS gradient."
          }
        },
        required: ["color"]
      }
    }
  ];

  ngOnInit() {
    this.initializeAgent();
  }

  private initializeAgent(): void {
    if (this.agent) {
      throw new Error('Agent is already initialized.');
    }

    const agent = new HttpAgent({ url: this.AGENT_URL });
    agent.subscribe({
      onTextMessageContentEvent: ({ textMessageBuffer }) => {
        this.updateAssistantMessage(textMessageBuffer);
      },
      onTextMessageEndEvent: async ({ textMessageBuffer }) => {
        console.log('Text message ended:', textMessageBuffer);
        this.updateAssistantMessage(textMessageBuffer);
        this.status.set('Ready to chat');
      },
      onToolCallStartEvent: ({ event }) => {
        if (event.toolCallName === "change_background_color") {
          this.pendingToolCall = { id: event.toolCallId, name: event.toolCallName, args: '' };
          this.status.set(`Executing ${event.toolCallName}...`);
        }
      },
      onToolCallArgsEvent: ({ event }) => {
        if (this.pendingToolCall?.id === event.toolCallId) {
          this.pendingToolCall.args += event.delta || '';
        }
      },
      onToolCallEndEvent: async ({ toolCallName, toolCallArgs, event }) => {
        console.log('Tool call', toolCallName, toolCallArgs, event);
        if (this.pendingToolCall?.id === event.toolCallId) {
          console.log('Local tool call', toolCallName);
          const args = this.pendingToolCall.args ? JSON.parse(this.pendingToolCall.args) : {};
          
          // Execute the tool locally (TODO, figure our the correct tool)
          const result = this.changeBackgroundColor(args.color || '#1e3a8a');

          // Store tool result message to be added after current run
          this.toolResultMessages.push({
            toolCallId: this.pendingToolCall.id,
            id: this.pendingToolCall.id,
            role: "tool",
            content: JSON.stringify(result)
          });
          this.pendingToolCall = null;
        }
      },
      onRunStartedEvent: ({ event }) => {
        console.log('Run started', event);
      },
      onRunFinishedEvent: async ({ result, event }) => {
        console.log('Run finished', result, event);
        this.isLoading.set(false);
        
        // If we have tool results, add them and run again
        if (this.toolResultMessages.length > 0) {
          const toolMessages = [...this.toolResultMessages];
          this.toolResultMessages = [];
          this.agent.addMessages(toolMessages);
          await this.runAgent();
        } else {
          this.status.set('Ready to chat');
        }
      }
    });

    this.agent = agent;
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();

    const message = this.inputMessage.trim();
    if (!message || this.isLoading()) {
      return;
    }

    this.messages.update(msgs => [...msgs, { role: 'user', content: message }]);
    this.inputMessage = '';
    this.scrollToBottom();

    // Add user message to agent and run
    this.agent.addMessages([{ id: "", role: 'user', content: message }]);
    await this.runAgent();
  }

  private async runAgent(): Promise<void> {
    this.isLoading.set(true);
    this.status.set('Agent thinking...');

    try {
      const parameters = { tools: this.tools };
      await this.agent.runAgent(parameters);
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

  private updateAssistantMessage(content: string): void {
    this.messages.update(msgs => {
      const updated = [...msgs];
      const lastMsg = updated[updated.length - 1];
      if (lastMsg?.role === 'assistant') {
        updated[updated.length - 1] = { role: 'assistant', content };
      } else {
        updated.push({ role: 'assistant', content });
      }
      return updated;
    });
    this.scrollToBottom();
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      const container = this.messagesContainer()?.nativeElement;
      if (container) {
        container.scrollTop = container.scrollHeight;
      }
    }, 100);
  }

  private changeBackgroundColor(color: string): string {
    this.backgroundColorChange.emit(color);
    console.log('Left panel background color changed to:', color);
    return "Success: Function completed.";
  }
}
