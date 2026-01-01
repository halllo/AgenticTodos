import { ChangeDetectionStrategy, Component, ElementRef, OnInit, output, signal, viewChild } from '@angular/core';
import { HttpAgent, Message as Message } from "@ag-ui/client"
import { Field, form, required } from '@angular/forms/signals';

interface NewMessageViewModel {
  content: string;
}

interface MessageViewModel {
  role: 'user' | 'assistant' | 'tool';
  content: string;
  toolName?: string;
  toolCallId?: string;
  isGenerating?: boolean;
  error?: boolean;
}

@Component({
  selector: 'app-chat',
  imports: [Field],
  changeDetection: ChangeDetectionStrategy.OnPush,
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
          <div class="message" 
            [class.user]="message.role === 'user'" 
            [class.assistant]="message.role === 'assistant'" 
            [class.tool]="message.role === 'tool'"
            [class.error]="message.error"
          >
            <div class="message-avatar">
              @if (message.role === 'user') {
                👤
              } @else if (message.role === 'assistant') {
                🤖
              } @else if (message.role === 'tool') {
                🛠️
              }
            </div>
            <div class="message-content"
              [class.generating]="message.isGenerating" 
            >
              @if (message.role === 'tool' && message.toolName) {
                <span class="tool-indicator">{{ message.toolName }}</span>
                <br>
              }
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
        <input type="text" [field]="newMessageForm.content" placeholder="Type your message..." class="message-input"/>
        @if (isLoading()) {
          <button type="button" class="send-button" (click)="cancelRun()">✋ Stop</button>
        } @else {
          <button type="submit" class="send-button">Send</button>
        }
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
          border-radius: 18px 4px 18px 18px;
        }
      }

      &.assistant {
        .message-content {
          background: white;
          color: #333;
          border-radius: 4px 18px 18px 18px;
          box-shadow: 0 1px 2px rgba(0,0,0,0.1);

          &.generating {
            opacity: 0.7;
          }
        }
      }

      &.error {
        .message-content {
          background: #ffebee !important;
          color: #c62828 !important;
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

    .message.tool {
      .message-content {
        background: #e0f7fa;
        color: #00796b;
        border-radius: 4px 8px 18px 18px;
        box-shadow: 0 1px 2px rgba(0,0,0,0.08);
      }
      .message-avatar {
        background: #e0f7fa;
        color: #00796b;
      }
    }

    .tool-indicator {
      font-size: 0.85em;
      margin-right: 0.5em;
      color: #00796b;
    }
  `,
})
export class ChatComponent implements OnInit {
  protected readonly newMessageViewModel = signal<NewMessageViewModel>({ content: '' });
  protected readonly newMessageForm = form(this.newMessageViewModel, schemaPath => {
    required(schemaPath.content);
  });
  protected readonly messages = signal<MessageViewModel[]>([]);
  protected readonly status = signal('Ready to chat');
  protected readonly isLoading = signal(false);

  protected readonly backgroundColorChange = output<string>();

  private readonly messagesContainer = viewChild<ElementRef>('messagesContainer');
  private agent!: HttpAgent;
  private pendingFrontendToolCalls: Array<{ id: string, name: string, args: string }> = [];
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

    const agent = new HttpAgent({ url: '/amazonbedrock/agui' });
    agent.subscribe({
      onTextMessageStartEvent: ({ event }) => {
        console.log('Text message started:', event);
        this.status.set('Assistant is typing...');
        this.messages.update(msgs => ([...msgs, { role: 'assistant', content: '', isGenerating: true }]));
      },
      onTextMessageContentEvent: ({ textMessageBuffer }) => {
        this.updateLastAssistantMessage(
          msg => ({ ...msg, content: textMessageBuffer }),
          { role: 'assistant', content: textMessageBuffer }
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
        // If it's a frontend tool, collect for batch execution
        if (event.toolCallName === "change_background_color") {
          this.pendingFrontendToolCalls.push({ id: event.toolCallId, name: event.toolCallName, args: '' });
          this.status.set(`Executing ${event.toolCallName}...`);
        }
      },
      onToolCallArgsEvent: ({ event }) => {
        // Find the matching pending frontend tool call and append args
        const call = this.pendingFrontendToolCalls.find(tc => tc.id === event.toolCallId);
        if (call) {
          call.args += event.delta || '';
        }
      },
      onToolCallEndEvent: async ({ toolCallName, toolCallArgs, event }) => {
        console.log('Tool call', toolCallName, toolCallArgs, event);
        this.messages.update(msgs => {
          return msgs.map(msg =>
            msg.toolCallId === event.toolCallId
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
      onRunFinishedEvent: async ({ result, event }) => {
        console.log('Run finished', result, event);
        this.isLoading.set(false);

        // Batch execute all pending frontend tool calls
        if (this.pendingFrontendToolCalls.length > 0) {
          const toolMessages: Message[] = [];
          for (const call of this.pendingFrontendToolCalls) {
            let result: string = '';
            if (call.name === "change_background_color") {
              const args = call.args ? JSON.parse(call.args) : {};
              result = JSON.stringify(this.changeBackgroundColor(args.color || '#1e3a8a'));
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

    const newMessage = this.newMessageViewModel().content.trim();
    if (!newMessage || this.isLoading()) {
      return;
    }

    this.newMessageViewModel.update(vm => ({ ...vm, content: '' }));
    this.messages.update(msgs => [...msgs, { role: 'user', content: newMessage }]);
    this.agent.addMessages([{ id: "", role: 'user', content: newMessage }]);
    this.scrollToBottom();

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
