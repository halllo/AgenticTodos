import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { McpAppComponent } from './mcp-app.component';
import { AppPanelService } from './app-panel.service';

/** The side panel holding the MCP apps the user moved out of the chat. */
@Component({
  selector: 'app-panel',
  imports: [McpAppComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="appPanel">
      <header class="appPanel__header">
        <h1 class="appPanel__title">Apps</h1>
      </header>
      <div class="appPanel__list">
        @for (app of panel.apps(); track app.id) {
          <section class="appPanel__item">
            <div class="appPanel__itemHeader">
              <span class="appPanel__itemTitle" [title]="app.resourceUri">{{ app.resourceUri }}</span>
              <button type="button" class="appPanel__button" (click)="panel.close(app.id)" title="Move this app back into the chat">
                Back to chat
              </button>
            </div>
            <app-mcp-app
              [resourceUri]="app.resourceUri"
              [toolInput]="app.toolInput"
              [toolResult]="app.toolResult"
              [source]="app.source"
              [sendMessage]="panel.sendMessage"
            />
          </section>
        }
      </div>
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex: 1;
      min-height: 0;
      min-width: 0;
    }

    .appPanel {
      flex: 1;
      display: flex;
      flex-direction: column;
      min-height: 0;
      min-width: 0;
      background: var(--surface-muted);
    }

    .appPanel__header {
      display: flex;
      align-items: center;
      padding: 1rem 1.5rem;
      background: var(--brand-gradient);
      color: #fff;
    }

    .appPanel__title {
      margin: 0;
      font-size: 1.5rem;
      font-weight: 600;
    }

    .appPanel__list {
      flex: 1;
      overflow-y: auto;
      padding: 1rem;
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }

    .appPanel__item {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      overflow: hidden;
      flex-shrink: 0;
    }

    .appPanel__itemHeader {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
      padding: 0.5rem 0.75rem;
      background: #f3e5f5;
      color: #6a1b9a;
      font-size: 0.85rem;
    }

    .appPanel__itemTitle {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .appPanel__button {
      flex-shrink: 0;
      border: 1px solid currentColor;
      border-radius: var(--radius-sm);
      background: transparent;
      color: inherit;
      font: inherit;
      padding: 0.15rem 0.6rem;
      cursor: pointer;

      &:hover {
        background: rgba(106, 27, 154, 0.08);
      }
    }
  `,
})
export class AppPanelComponent {
  protected readonly panel = inject(AppPanelService);
}
