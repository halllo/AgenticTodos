import { inject, Injectable, signal } from '@angular/core';
import type { Context } from '@ag-ui/client';
import { McpClientService } from './mcp-client.service';
import { WebmcpService } from './webmcp.service';

/** An MCP app in the side panel; everything `<app-mcp-app>` needs to start it. */
export interface PanelApp {
  /**
   * For an app moved out of the chat, the chat activity's messageId (the tool call id), linking the entry
   * back to its chat card; for an app opened from the launcher, an id of its own.
   */
  id: string;
  resourceUri: string;
  title?: string;
  /** Absent for an app opened from the launcher until a tool call delivers input and result to it. */
  toolInput?: Record<string, unknown>;
  toolResult?: unknown;
  source: 'server' | 'frontend';
  /** What the app last reported about itself (ui/update-model-context), passed to the model with each run. */
  modelContext?: string;
}

/**
 * The MCP apps in the side panel, where they stay visible while the chat moves on. An app gets here two
 * ways: moved out of the chat (while it is here, its chat card shows a placeholder instead), or opened
 * directly from the launcher, without a tool call. Either way the chat can drive it afterwards: a later
 * call to a tool of the same app is delivered to the open app rather than rendered again in the chat.
 */
@Injectable({ providedIn: 'root' })
export class AppPanelService {
  private readonly mcpClient = inject(McpClientService);
  private readonly webmcp = inject(WebmcpService);

  readonly apps = signal<PanelApp[]>([]);

  /** Set by the chat: delivers an app's ui/message as a user turn, wherever the app is rendered. */
  messageHandler?: (text: string) => Promise<boolean>;

  readonly sendMessage = (text: string): Promise<boolean> =>
    this.messageHandler?.(text) ?? Promise.resolve(false);

  open(app: PanelApp): void {
    this.apps.update(apps => (apps.some(a => a.id === app.id) ? apps : [...apps, app]));
  }

  /** Opens an app resource straight from the server, without a tool call. */
  launch(resourceUri: string, title?: string): void {
    this.open({ id: `launch-${crypto.randomUUID()}`, resourceUri, title, source: 'server' });
  }

  close(id: string): void {
    this.apps.update(apps => apps.filter(a => a.id !== id));
  }

  isOpen(id: string | undefined): boolean {
    return !!id && this.apps().some(a => a.id === id);
  }

  /**
   * Hands a tool call's input and result to the open app that renders this resource (the most recently
   * opened one, if several do). Returns false when no such app is open, so the chat renders it itself.
   */
  deliver(resourceUri: string, source: PanelApp['source'], toolInput: Record<string, unknown> | undefined, toolResult: unknown): boolean {
    const target = this.apps().filter(a => a.resourceUri === resourceUri && a.source === source).at(-1);
    if (!target) {
      return false;
    }
    this.apps.update(apps => apps.map(a => (a === target ? { ...a, toolInput: toolInput ?? {}, toolResult } : a)));
    return true;
  }

  setModelContext(id: string, modelContext: string): void {
    this.apps.update(apps => apps.map(a => (a.id === id ? { ...a, modelContext } : a)));
  }

  /**
   * The AG-UI context entry telling the model which apps are open in the panel, which tools drive them, and
   * what each app last reported about itself. Empty while the panel is.
   */
  async modelContext(): Promise<Context[]> {
    const apps = this.apps();
    if (!apps.length) {
      return [];
    }
    const serverTools = [...(await this.mcpClient.getServerInfo()).tools.values()];
    const frontendTools = this.webmcp.tools();
    const entries = apps.map(app => {
      const tools = (app.source === 'server' ? serverTools : frontendTools)
        .filter(t => (t._meta?.['ui'] as { resourceUri?: string } | undefined)?.resourceUri === app.resourceUri)
        .map(t => t.name);
      return [
        `- ${app.title ?? app.resourceUri} (${app.resourceUri}); tools: ${tools.join(', ') || 'none'}`,
        app.modelContext ? `  The app reports:\n${indent(app.modelContext, '    ')}` : undefined,
      ].filter(Boolean).join('\n');
    });
    return [{
      description: 'MCP apps open in the side panel next to the chat',
      value:
        'The user sees these apps next to the chat and may refer to them. Calling one of an app\'s tools delivers ' +
        'the result to the open app, updating it in place.\n' + entries.join('\n'),
    }];
  }
}

function indent(text: string, prefix: string): string {
  return text.split('\n').map(line => prefix + line).join('\n');
}
