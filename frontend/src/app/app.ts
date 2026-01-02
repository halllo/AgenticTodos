import { ChangeDetectionStrategy, Component, inject, OnDestroy, OnInit, signal, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ChatComponent } from './chat.component';
import { TodosComponent } from './todos.component';
import { WebmcpService } from './webmcp.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ChatComponent, TodosComponent],
  template: `
    <div class="app">
      <div class="app__leftPanel" [style.background]="leftPanelBackground()">
        <app-todos #todos />
      </div>
      <div class="app__rightPanel">
        <app-chat/>
      </div>
    </div>
    <router-outlet />
  `,
  styles: `
    .app {
      display: flex;
      height: 100vh;
      min-height: 0;
      min-width: 0;
    }

    .app__leftPanel {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: stretch;
      justify-content: stretch;
      transition: background 0.3s ease;
      overflow: hidden;
      min-height: 0;
      min-width: 0;
    }

    .app__rightPanel {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: stretch;
      justify-content: stretch;
      overflow: hidden;
      min-height: 0;
      min-width: 0;
    }

    .app__leftPanel app-todos {
      flex: 1;
      min-height: 0;
      min-width: 0;
      display: flex;
    }

    .app__rightPanel app-chat {
      flex: 1;
      min-height: 0;
      min-width: 0;
      display: flex;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements OnInit, OnDestroy {
  protected readonly leftPanelBackground = signal('var(--brand-gradient)');
  private readonly webmcp = inject(WebmcpService);
  private readonly todos = viewChild<TodosComponent>('todos');

  async ngOnInit() {
    this.webmcp.registerTool({
      name: "change_background_color",
      description: "Change the left panel background color. Can accept solid colors (e.g., '#1e3a8a', 'red') or gradients (e.g., 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)').",
      inputSchema: {
        type: "object",
        properties: {
          color: {
            type: "string",
            description: "The background color or gradient to apply to the left panel. Can be a hex color, named color, or CSS gradient."
          }
        },
        required: ["color"]
      },
      execute: async (args) => {
        const color = String(args?.['color'] ?? '').trim();
        if (!color) {
          return 'Error: Missing color.';
        }
        this.leftPanelBackground.set(color);
        return 'Success: Background changed.';
      }
    });
    this.webmcp.registerTool({
      name: "add_todo",
      description: "Add a todo to the todo list in the left panel.",
      inputSchema: {
        type: "object",
        properties: {
          title: {
            type: "string",
            description: "The todo title text."
          }
        },
        required: ["title"]
      },
      execute: async (args) => {
        const title = String(args?.['title'] ?? '').trim();
        if (!title) {
          return 'Error: Missing todo title.';
        }
        const todos = this.todos();
        if (!todos) {
          return 'Error: Todo list not available.';
        }
        todos.addTodoWithTitle(title);
        return 'Success: Todo added.';
      }
    });

    await this.webmcp.initializeClient();
  }

  ngOnDestroy() {
    this.webmcp.unregisterTools();
  }
}
