import { Injectable } from '@angular/core';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';
import type { Resource, Tool } from '@modelcontextprotocol/sdk/types.js';

export interface McpServerInfo {
  client: Client;
  tools: Map<string, Tool>;
  resources: Map<string, Resource>;
  appHtmlCache: Map<string, string>;
}

@Injectable({ providedIn: 'root' })
export class McpClientService {
  private infoPromise: Promise<McpServerInfo> | null = null;

  getServerInfo(): Promise<McpServerInfo> {
    return (this.infoPromise ??= this.connect());
  }

  private async connect(): Promise<McpServerInfo> {
    const url = new URL('/agents/mcp-relay', window.location.href);
    try {
      const client = new Client({ name: 'AgenticTodos', version: '1.0.0' });
      await client.connect(new StreamableHTTPClientTransport(url));
      return await this.buildInfo(client);
    } catch {
      const client = new Client({ name: 'AgenticTodos', version: '1.0.0' });
      await client.connect(new SSEClientTransport(url));
      return await this.buildInfo(client);
    }
  }

  private async buildInfo(client: Client): Promise<McpServerInfo> {
    const [{ tools }, { resources }] = await Promise.all([
      client.listTools(),
      client.listResources(),
    ]);
    return {
      client,
      tools: new Map(tools.map((t) => [t.name, t])),
      resources: new Map(resources.map((r) => [r.uri, r])),
      appHtmlCache: new Map(),
    };
  }
}
