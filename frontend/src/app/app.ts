import { ChangeDetectionStrategy, Component, inject, OnDestroy, OnInit, signal, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ChatComponent } from './chat.component';
import { TodosComponent } from './todos.component';
import { ResizerComponent } from './resizer.component';
import { WebmcpService } from './webmcp.service';

const TODOS_APP_URI = 'ui://todos/todos-app.html';

/**
 * The single HTML document an MCP app resource must be: a frontend app's bundle (built into public/ by
 * `npm run build:apps`) inlined into a page, since the sandbox writes the HTML into a frame that
 * cannot resolve the host's relative URLs.
 */
async function inlineAppHtml(title: string, scriptUrl: string): Promise<string> {
  const response = await fetch(scriptUrl);
  if (!response.ok) {
    throw new Error(`Failed to load MCP app bundle ${scriptUrl}: ${response.status}`);
  }
  // `<\/script` keeps a literal `</script` inside the bundle from closing the inline script early.
  const script = (await response.text()).replaceAll('</script', '<\\/script');
  return `<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8" /><title>${title}</title></head>`
    + `<body><script type="module">${script}</script></body></html>`;
}

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ChatComponent, TodosComponent, ResizerComponent],
  template: `
    <div class="app">
      <div class="app__leftPanel" #leftPanel [style.background]="leftPanelBackground()">
        <app-todos #todos />
      </div>
      <app-resizer [leftPanel]="leftPanel" [rightPanel]="rightPanel" />
      <div class="app__rightPanel" #rightPanel>
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
      flex: 2;
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

    this.webmcp.registerAppResource({
      uri: TODOS_APP_URI,
      name: "todos_app",
      html: () => inlineAppHtml('Todos', '/mcp-apps/todos-app.js'),
    });
    this.webmcp.registerAppTool({
      name: "show_todos",
      description: "Show the todo list from the left panel as an interactive app in the chat, where the user can also add todos. Returns the todos as JSON.",
      resourceUri: TODOS_APP_URI,
      execute: async () => {
        const todos = this.todos();
        if (!todos) {
          return 'Error: Todo list not available.';
        }
        return JSON.stringify(todos.list());
      }
    });

    await this.webmcp.initializeClient();
  }

  ngOnDestroy() {
    this.webmcp.unregisterTools();
  }
}
