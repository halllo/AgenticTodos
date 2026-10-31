import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { RESOURCE_MIME_TYPE } from '@modelcontextprotocol/ext-apps/app-bridge';
import type { Resource } from '@modelcontextprotocol/client';
import { McpClientService } from './mcp-client.service';
import { AppPanelService } from './app-panel.service';

/**
 * Opens an MCP app straight into the side panel, without a chat turn: lists the MCP server's app resources
 * (`text/html;profile=mcp-app`) and starts the picked one with no tool call. The chat can drive it afterwards.
 */
@Component({
  selector: 'app-launcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:click)': 'onDocumentClick($event)' },
  template: `
    <button type="button" class="launcher__button" (click)="toggle()" [attr.aria-expanded]="isOpen()" title="Open an MCP app in the side panel">
      Apps ▾
    </button>
    @if (isOpen()) {
      <div class="launcher__menu" role="menu">
        @for (app of apps(); track app.uri) {
          <button type="button" class="launcher__item" role="menuitem" (click)="launch(app)">
            <span class="launcher__itemTitle">{{ app.title ?? app.name }}</span>
            @if (app.description) {
              <span class="launcher__itemDescription">{{ app.description }}</span>
            }
          </button>
        } @empty {
          <div class="launcher__empty">{{ error() ?? 'Loading apps…' }}</div>
        }
      </div>
    }
  `,
  styles: `
    :host {
      position: relative;
    }

    .launcher__button {
      border: 1px solid rgba(255, 255, 255, 0.6);
      border-radius: var(--radius-sm);
      background: transparent;
      color: inherit;
      font: inherit;
      padding: 0.35rem 0.75rem;
      cursor: pointer;

      &:hover {
        background: rgba(255, 255, 255, 0.15);
      }
    }

    .launcher__menu {
      position: absolute;
      top: calc(100% + 0.25rem);
      left: 0;
      z-index: 10;
      width: 20rem;
      display: flex;
      flex-direction: column;
      padding: 0.25rem;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      box-shadow: 0 4px 16px rgba(0, 0, 0, 0.15);
      color: #333;
    }

    .launcher__item {
      display: flex;
      flex-direction: column;
      gap: 0.15rem;
      border: none;
      border-radius: var(--radius-sm);
      background: transparent;
      font: inherit;
      text-align: left;
      padding: 0.5rem 0.75rem;
      cursor: pointer;

      &:hover {
        background: #f3e5f5;
      }
    }

    .launcher__itemTitle {
      font-weight: 600;
    }

    .launcher__itemDescription,
    .launcher__empty {
      font-size: 0.85rem;
      color: #666;
    }

    .launcher__empty {
      padding: 0.5rem 0.75rem;
    }
  `,
})
export class AppLauncherComponent {
  private readonly mcpClient = inject(McpClientService);
  private readonly panel = inject(AppPanelService);
  private readonly host = inject(ElementRef<HTMLElement>);

  protected readonly isOpen = signal(false);
  protected readonly apps = signal<Resource[]>([]);
  protected readonly error = signal<string | undefined>(undefined);

  protected async toggle(): Promise<void> {
    this.isOpen.update(open => !open);
    if (!this.isOpen()) {
      return;
    }
    try {
      const { resources } = await this.mcpClient.getServerInfo();
      const apps = [...resources.values()].filter(r => r.mimeType === RESOURCE_MIME_TYPE);
      this.apps.set(apps);
      this.error.set(apps.length ? undefined : 'The MCP server offers no apps.');
    } catch (err) {
      console.error('[AppLauncherComponent] could not list MCP apps', err);
      this.error.set('Could not reach the MCP server.');
    }
  }

  protected launch(app: Resource): void {
    this.panel.launch(app.uri, app.title ?? app.name);
    this.isOpen.set(false);
  }

  protected onDocumentClick(event: MouseEvent): void {
    if (this.isOpen() && !this.host.nativeElement.contains(event.target as Node)) {
      this.isOpen.set(false);
    }
  }
}
