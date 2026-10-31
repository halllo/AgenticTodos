import { Injectable, signal } from '@angular/core';

/** An MCP app moved out of the chat into the side panel; everything `<app-mcp-app>` needs to start it again. */
export interface PanelApp {
  /** The chat activity's messageId (the tool call id), linking the panel entry back to its chat card. */
  id: string;
  resourceUri: string;
  toolInput: Record<string, unknown>;
  toolResult: unknown;
  source: 'server' | 'frontend';
}

/**
 * The MCP apps the user moved into the side panel, where they stay visible while the chat scrolls on.
 * An app lives in one place at a time: while it is here, its chat card shows a placeholder instead.
 */
@Injectable({ providedIn: 'root' })
export class AppPanelService {
  readonly apps = signal<PanelApp[]>([]);

  /** Set by the chat: delivers an app's ui/message as a user turn, wherever the app is rendered. */
  messageHandler?: (text: string) => Promise<boolean>;

  readonly sendMessage = (text: string): Promise<boolean> =>
    this.messageHandler?.(text) ?? Promise.resolve(false);

  open(app: PanelApp): void {
    this.apps.update(apps => (apps.some(a => a.id === app.id) ? apps : [...apps, app]));
  }

  close(id: string): void {
    this.apps.update(apps => apps.filter(a => a.id !== id));
  }

  isOpen(id: string | undefined): boolean {
    return !!id && this.apps().some(a => a.id === id);
  }
}
