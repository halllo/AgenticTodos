import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';

type Todo = {
  id: string;
  title: string;
  completed: boolean;
};

@Component({
  selector: 'app-todos',
  template: `
    <section class="todo">
      <header class="todo__header">
        <h1 class="todo__title">{{ remainingCount() > 0 ? remainingCount() : '' }} Todos</h1>

        <div class="todo__inputRow">
          <input
            class="todo__input"
            type="text"
            placeholder="Add a todo"
            [value]="newTodoTitle()"
            (input)="onTitleInput($event)"
            (keydown.enter)="addTodo()"
            aria-label="New todo"
          />
          <button class="todo__add" type="button" (click)="addTodo()" aria-label="Add todo">➕</button>
        </div>
      </header>

      <div class="todo__list" role="list">
        @if (todos().length === 0) {
          <div class="todo__empty">Nothing to do.</div>
        } @else {
          @for (todo of todos(); track todo.id) {
            <div class="todo__item" role="listitem">
              <label class="todo__label">
                <input
                  class="todo__checkbox"
                  type="checkbox"
                  [checked]="todo.completed"
                  (change)="toggleTodo(todo.id)"
                  aria-label="Complete todo"
                />
                <span class="todo__text" [class.todo__text--completed]="todo.completed">
                  {{ todo.title }}
                </span>
              </label>

              <button
                class="todo__remove"
                type="button"
                (click)="removeTodo(todo.id)"
                aria-label="Remove todo"
              >
                🗑️
              </button>
            </div>
          }
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      width: 100%;
    }

    .todo {
      height: 100%;
      width: 100%;
      display: flex;
      flex-direction: column;
      padding: 20px;
      box-sizing: border-box;
      gap: 12px;
      color: white;
    }

    .todo__header {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }

    .todo__title {
      font-size: 2rem;
      font-weight: 700;
      margin: 0;
      text-align: left;
    }

    .todo__inputRow {
      display: flex;
      gap: 10px;
    }

    .todo__input {
      flex: 1;
      padding: 10px 12px;
      border-radius: var(--radius-lg);
      border: 1px solid var(--glass-border);
      background: var(--glass-bg);
      color: white;
      font-size: 1rem;
      outline: none;
    }

    .todo__input::placeholder {
      color: rgba(255, 255, 255, 0.75);
    }

    .todo__add {
      padding: 10px 14px;
      border-radius: var(--radius-lg);
      border: 1px solid var(--glass-border);
      background: var(--glass-bg);
      color: white;
      font-size: 1rem;
      font-weight: 600;
      cursor: pointer;
    }

    .todo__meta {
      font-size: 0.95rem;
      opacity: 0.9;
    }

    .todo__list {
      flex: 1;
      min-height: 0;
      overflow: auto;
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding-right: 6px;
    }

    .todo__empty {
      opacity: 0.9;
      padding: 10px 0;
    }

    .todo__item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 10px 12px;
      border-radius: var(--radius-md);
      border: 1px solid var(--glass-border-weak);
      background: var(--glass-bg-weak);
    }

    .todo__label {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      flex: 1;
      cursor: pointer;
    }

    .todo__checkbox {
      flex: 0 0 auto;
    }

    .todo__text {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 1rem;
    }

    .todo__text--completed {
      text-decoration: line-through;
      opacity: 0.7;
    }

    .todo__remove {
      flex: 0 0 auto;
      padding: 8px 10px;
      border-radius: var(--radius-lg);
      border: 1px solid var(--glass-border-weak);
      background: var(--glass-bg-weak);
      color: white;
      font-size: 1rem;
      font-weight: 600;
      cursor: pointer;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TodosComponent {
  protected readonly newTodoTitle = signal('');
  protected readonly todos = signal<Todo[]>([]);
  protected readonly remainingCount = computed(() => this.todos().filter((t) => !t.completed).length);

  protected onTitleInput(event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.newTodoTitle.set(value);
  }

  public addTodoWithTitle(title: string): void {
    this.prependTodo(title);
  }

  protected addTodo(): void {
    this.prependTodo(this.newTodoTitle());
    this.newTodoTitle.set('');
  }

  private prependTodo(title: string): void {
    const trimmed = title.trim();
    if (!trimmed) return;

    const todo: Todo = {
      id: globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      title: trimmed,
      completed: false,
    };

    this.todos.update((todos) => [todo, ...todos]);
  }

  protected toggleTodo(id: string): void {
    this.todos.update((todos) =>
      todos.map((todo) => (todo.id === id ? { ...todo, completed: !todo.completed } : todo)),
    );
  }

  protected removeTodo(id: string): void {
    this.todos.update((todos) => todos.filter((todo) => todo.id !== id));
  }
}
