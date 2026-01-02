import { Injectable, signal } from '@angular/core';

// Ensure the Web Model Context polyfill executes.
// If this is imported only as types elsewhere, TS/Angular can erase it and
// navigator.modelContext will remain undefined.
import '@mcp-b/global';
import type { RegistrationHandle } from '@mcp-b/global';

import { TabClientTransport } from '@mcp-b/transports';
import { Client } from '@modelcontextprotocol/sdk/client';

@Injectable({ providedIn: 'root' })
export class WebmcpService {
  private client?: Client;
  private readonly toolsSignal = signal<{ name: string, description: string, inputSchema: any }[]>([]);
  readonly tools = this.toolsSignal.asReadonly();

  private registeredTools: RegistrationHandle[] = [];

  registerTool(registerableTool: { name: string; description: string, inputSchema?: any, execute: (args: Record<string, any>) => Promise<string> }) {
    const webmcp = navigator.modelContext;
    if (webmcp) {
      const registeredTool = webmcp.registerTool({
        name: registerableTool.name,
        description: registerableTool.description,
        inputSchema: registerableTool.inputSchema ??
        {
          type: "object",
          properties: {},
        },
        execute: async (_args) => {
          const result = await registerableTool.execute(_args);
          return {
            content: [{ type: "text", text: result }],
          };
        }
      });
      this.registeredTools.push(registeredTool);
      console.log("WebMCP tool registered.");
    } else {
      console.log("WebMCP context not found; tool not registered.", navigator.modelContext);
    }
  }

  unregisterTools() {
    for (const handle of this.registeredTools) {
      try {
        handle.unregister();
      } catch {
        // ignore cleanup errors
      }
    }
    this.registeredTools = [];
  }

  async initializeClient(): Promise<void> {
    const skipInitializationInFavorOfOtherTabTransport = false;
    if (skipInitializationInFavorOfOtherTabTransport) {
      console.log('Skipping WebMCP client initialization to avoid TabClientTransport collision.');
      return;
    }

    console.log('Connecting to WebMCP server...');
    const transport = new TabClientTransport({ targetOrigin: window.origin });
    const client = new Client({ name: 'agentic-todos', version: '1.0.0' });
    await client.connect(transport); // if the MCP-B extension is active, this will take over the transport
    this.client = client;
    console.log('Connected to WebMCP server.');

    console.log('Fetching available tools...');
    try {
      const listToolsResponse = await client.listTools();
      const tools = listToolsResponse.tools.map(t => ({ name: t.name, description: t.description || '', inputSchema: t.inputSchema }));
      this.toolsSignal.set(tools);
      console.log('Available tools:', tools);
    } catch (error) {
      console.error('Error fetching tools. Likely a TabClientTransport collision.', error);
      console.log('Falling back to mcpBridge.tools...');
      const tools = [...window.__mcpBridge?.tools.values() ?? []];
      this.toolsSignal.set(tools);
      console.log('Available tools:', tools);
    }
  }

  async invokeTool(toolName: string, args?: Record<string, any>): Promise<any> {
    const invokableTool = this.toolsSignal().find(t => t.name === toolName);
    if (invokableTool) {
      if (!this.client) {
        throw new Error('WebMCP client is not initialized. Call initializeClient() first.');
      }

      const response: unknown = await this.client.callTool({ name: toolName, arguments: args ?? {} });
      // If the response is a single text content, return just the text.
      const unwrappedText = this.tryUnwrapSingleTextContent(response);
      console.log(`Tool ${toolName} invoked. Response:`, response);
      return unwrappedText ?? response;
    } else {
      console.error(`Tool ${toolName} not found.`);
      throw new Error(`Tool ${toolName} not found.`);
    }
  }

  private tryUnwrapSingleTextContent(response: unknown): string | undefined {
    if (!response || typeof response !== 'object') {
      return undefined;
    }

    const maybeContent = (response as { content?: unknown }).content;
    if (!Array.isArray(maybeContent) || maybeContent.length !== 1) {
      return undefined;
    }

    const item = maybeContent[0];
    if (!item || typeof item !== 'object') {
      return undefined;
    }

    const maybeType = (item as { type?: unknown }).type;
    const maybeText = (item as { text?: unknown }).text;

    return maybeType === 'text' && typeof maybeText === 'string'
      ? maybeText
      : undefined;
  }
}

