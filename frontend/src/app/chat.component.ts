import { ChangeDetectionStrategy, Component, ElementRef, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

interface Message {
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
export class ChatComponent {
    protected readonly AGENT_URL = '/agui';
    protected readonly conversationId = signal<string | null>(null);
    protected readonly messages = signal<Message[]>([]);
    protected readonly status = signal('Ready to chat');
    protected readonly isLoading = signal(false);
    protected inputMessage = '';

    readonly backgroundColorChange = output<string>();

    private readonly messagesContainer = viewChild<ElementRef>('messagesContainer');
    private pendingToolCalls = new Map<string, any>();
    private conversationMessages: any[] = [];
    private readonly tools = [
        {
            "name": "change_background_color",
            "description": "Change the left panel background color. Can accept solid colors (e.g., '#1e3a8a', 'red') or gradients (e.g., 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)').",
            "parameters": {
                "type": "object",
                "properties": {
                    "color": {
                        "type": "string",
                        "description": "The background color or gradient to apply to the left panel. Can be a hex color, named color, or CSS gradient."
                    }
                },
                "required": ["color"]
            }
        }
    ];
    private hasToolCallsInProgress = false;

    protected onSubmit(event: Event): void {
        event.preventDefault();

        const message = this.inputMessage.trim();
        if (!message || this.isLoading()) {
            return;
        }

        this.messages.update(msgs => [...msgs, { role: 'user', content: message }]);
        this.inputMessage = '';
        this.sendMessage(message);
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

    private async sendMessage(message: string): Promise<void> {
        this.isLoading.set(true);
        this.status.set('Agent thinking...');

        this.conversationMessages.push({ role: "user", content: message });

        await this.sendToAgent();

        this.isLoading.set(false);
        this.status.set('Ready to chat');
        this.scrollToBottom();
    }

    private async sendToAgent(): Promise<void> {
        const payload: any = {
            messages: this.conversationMessages,
            tools: this.tools
        };

        if (this.conversationId()) {
            payload.conversationId = this.conversationId();
        }

        try {
            const response = await fetch(`${this.AGENT_URL}`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'text/event-stream'
                },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            await this.processStream(response);
        } catch (error) {
            console.error('Error sending message:', error);
            this.messages.update(msgs => [...msgs, {
                role: 'assistant',
                content: 'Sorry, an error occurred. Please try again.'
            }]);
            this.status.set('Error occurred');
        }
    }

    private async processStream(response: Response): Promise<void> {
        const reader = response.body!.getReader();
        const decoder = new TextDecoder();
        let currentContent = '';
        let messageStarted = false;

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            const chunk = decoder.decode(value);
            const lines = chunk.split('\n');

            for (const line of lines) {
                if (line.startsWith('data: ')) {
                    const data = line.substring(6).trim();

                    if (data === '[DONE]') {
                        continue;
                    }

                    try {
                        const event = JSON.parse(data);

                        if (event.type === 'RUN_STARTED') {
                            if (event.threadId) {
                                this.conversationId.set(event.threadId);
                            }
                            this.status.set('Agent thinking...');
                        } else if (event.type === 'TEXT_MESSAGE_START') {
                            if (!messageStarted) {
                                messageStarted = true;
                                currentContent = '';
                                this.messages.update(msgs => [...msgs, { role: 'assistant', content: '' }]);
                            }
                        } else if (event.type === 'TEXT_MESSAGE_CONTENT') {
                            currentContent += event.delta || '';
                            this.messages.update(msgs => {
                                const updated = [...msgs];
                                if (updated.length > 0 && updated[updated.length - 1].role === 'assistant') {
                                    updated[updated.length - 1] = { role: 'assistant', content: currentContent };
                                }
                                return updated;
                            });
                            this.scrollToBottom();
                        } else if (event.type === 'TEXT_MESSAGE_END') {
                            messageStarted = false;
                            // Add the completed assistant message to conversation history
                            this.conversationMessages.push({
                                role: 'assistant',
                                content: currentContent
                            });
                            this.status.set('Ready to chat');
                        } else if (event.type === 'RUN_FINISHED') {
                            this.status.set('Ready to chat');
                        } else if (event.type === 'RUN_ERROR') {
                            throw new Error(event.message || 'Unknown error occurred');
                        } else if (event.type === 'TOOL_CALL_START') {
                            console.log('Tool call started:', event);
                            this.pendingToolCalls.set(event.toolCallId, {
                                id: event.toolCallId,
                                name: event.toolCallName,
                                args: ''
                            });
                            this.status.set(`Executing ${event.toolCallName}...`);
                        } else if (event.type === 'TOOL_CALL_ARGS') {
                            const toolCall = this.pendingToolCalls.get(event.toolCallId);
                            if (toolCall) {
                                toolCall.args += event.delta || '';
                            }
                        } else if (event.type === 'TOOL_CALL_END') {
                            const toolCall = this.pendingToolCalls.get(event.toolCallId);
                            if (toolCall) {
                                console.log('Tool call ended:', toolCall);
                                const toolMessages = await this.executeToolCall(toolCall.id, toolCall.name, toolCall.args);
                                if (toolMessages.length > 0) {
                                    this.conversationMessages.push(...toolMessages);
                                    this.hasToolCallsInProgress = true;
                                }
                                this.pendingToolCalls.delete(event.toolCallId);
                            }
                        }
                    } catch (e) {
                        console.error('Error parsing SSE data:', e, 'Data:', data);
                    }
                }
            }
        }

        // After stream ends, continue if we just processed tool calls
        if (this.hasToolCallsInProgress) {
            this.hasToolCallsInProgress = false;
            await this.sendToAgent();
        }
    }

    private async executeToolCall(toolCallId: string, toolName: string, argsJson: string): Promise<any[]> {
        let result: string;
        let error: string | null = null;

        try {
            const args = argsJson ? JSON.parse(argsJson) : {};

            switch (toolName) {
                case 'change_background_color':
                    result = this.changeBackgroundColor(args.color || '#1e3a8a');
                    break;
                default:
                    console.warn(`Unknown tool ${toolName}. Must be a backend tool. Ignoring.`);
                    return [];
            }
        } catch (err) {
            error = String(err);
            result = `Error: ${err}`;
            console.error('Error executing tool call:', err);
        }

        const assistantMessage = {
            name: null,
            toolCalls: [{
                id: toolCallId,
                type: "function",
                function: { name: toolName, arguments: argsJson || "{}" }
            }],
            id: toolCallId,
            role: "assistant",
            content: ""
        };

        const toolResponse = {
            toolCallId: toolCallId,
            error: error,
            id: toolCallId,
            role: "tool",
            content: JSON.stringify(result)
        };

        return [assistantMessage, toolResponse];
    }

    private changeBackgroundColor(color: string): string {
        this.backgroundColorChange.emit(color);
        console.log('Left panel background color changed to:', color);
        return "Success: Function completed.";
    }
}
