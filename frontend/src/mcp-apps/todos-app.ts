// MCP app for the `show_todos` frontend tool. Runs inside the sandbox iframe, never on the host
// origin; `npm run build:apps` bundles it into public/mcp-apps/todos-app.js, which the app resource
// registered in app.ts inlines into the HTML it serves. Its tool calls go to the tab's WebMCP server.

import { App, applyHostStyleVariables } from '@modelcontextprotocol/ext-apps';
import type { McpUiHostContext } from '@modelcontextprotocol/ext-apps';
import type { CallToolResult } from '@modelcontextprotocol/client';

type Todo = { id: string; title: string; completed: boolean };

document.body.innerHTML = `
  <style>
    body {
      margin: 0;
      font-family: var(--font-sans, system-ui, sans-serif);
      font-size: var(--font-text-md-size, 14px);
      color: var(--color-text-primary, #111);
      background: var(--color-background-primary, #fff);
    }
    .todos { padding: 0.75rem; display: flex; flex-direction: column; gap: 0.5rem; }
    .todos__list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .todos__item--done { color: var(--color-text-tertiary, #888); text-decoration: line-through; }
    .todos__empty, .todos__error { color: var(--color-text-secondary, #555); }
    .todos__error { color: var(--color-text-danger, #b00); }
    .todos__form { display: flex; gap: 0.5rem; }
    .todos__form input {
      flex: 1; padding: 0.35rem 0.5rem; font: inherit;
      border: var(--border-width-regular, 1px) solid var(--color-border-primary, #ccc);
      border-radius: var(--border-radius-sm, 4px);
    }
    .todos__form button { font: inherit; padding: 0.35rem 0.75rem; cursor: pointer; }
  </style>
  <div class="todos">
    <ul class="todos__list" id="list"></ul>
    <form class="todos__form" id="form">
      <input id="title" placeholder="Add a todo" autocomplete="off" />
      <button type="submit">Add</button>
    </form>
  </div>
`;

const listEl = document.getElementById('list')!;
const formEl = document.getElementById('form') as HTMLFormElement;
const titleEl = document.getElementById('title') as HTMLInputElement;

const app = new App({ name: 'Todos App', version: '1.0.0' }, {});

const applyHostContext = (context: McpUiHostContext | undefined) => {
  if (context?.styles?.variables) applyHostStyleVariables(context.styles.variables);
};
app.onhostcontextchanged = applyHostContext;

app.ontoolresult = render;

formEl.addEventListener('submit', async event => {
  event.preventDefault();
  const title = titleEl.value.trim();
  if (!title) return;
  titleEl.value = '';
  await app.callServerTool({ name: 'add_todo', arguments: { title } });
  render(await app.callServerTool({ name: 'show_todos', arguments: {} }));
});

app.connect().then(() => applyHostContext(app.getHostContext()));

function render(result: CallToolResult): void {
  const text = result.content?.find(c => c.type === 'text')?.text ?? '';
  let todos: Todo[];
  try {
    todos = result.isError ? [] : JSON.parse(text);
  } catch {
    todos = [];
  }

  if (result.isError || !Array.isArray(todos)) {
    listEl.replaceChildren(item('todos__error', text || 'Could not load todos.'));
  } else if (todos.length === 0) {
    listEl.replaceChildren(item('todos__empty', 'No todos yet.'));
  } else {
    listEl.replaceChildren(...todos.map(t => item(t.completed ? 'todos__item--done' : '', t.title)));
  }
}

function item(className: string, text: string): HTMLLIElement {
  const li = document.createElement('li');
  if (className) li.className = className;
  li.textContent = text;
  return li;
}
