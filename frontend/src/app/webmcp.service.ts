import { Injectable, signal } from '@angular/core';

// Ensure the Web Model Context polyfill executes.
// If this is imported only as types elsewhere, TS/Angular can erase it and
// document.modelContext will remain undefined.
import '@mcp-b/global';

import { TabClientTransport } from '@mcp-b/transports';
import { Client } from '@modelcontextprotocol/client';
import type { CallToolResult } from '@modelcontextprotocol/client';
import { EXTENSION_ID, RESOURCE_MIME_TYPE, RESOURCE_URI_META_KEY, registerAppResource, registerAppTool } from '@modelcontextprotocol/ext-apps/server';
import type { McpUiResourceCsp, McpUiResourcePermissions } from '@modelcontextprotocol/ext-apps';
import { fromJsonSchema } from '@modelcontextprotocol/server';

// The McpServer behind @mcp-b's BrowserMcpServer (document.modelContext.mcpServer). App tools are
// registered on it directly because BrowserMcpServer.registerTool drops `_meta` (@mcp-b 6.0.3), and
// `_meta.ui.resourceUri` is what links a tool to its MCP app. Typed by what the ext-apps helpers need
// rather than as McpServer, which @mcp-b may resolve to a different copy of the package.
type AppServer = Parameters<typeof registerAppTool>[0] & Parameters<typeof registerAppResource>[0];

export interface WebmcpTool {
  name: string;
  description: string;
  inputSchema: any;
  _meta?: Record<string, unknown>;
}

/** A WebMCP tool response, both as the text the model gets and as the CallToolResult an MCP app gets. */
export interface WebmcpToolResponse {
  text: string;
  result: CallToolResult;
}

@Injectable({ providedIn: 'root' })
export class WebmcpService {
  private client?: Client;
  private readonly toolsSignal = signal<WebmcpTool[]>([]);
  readonly tools = this.toolsSignal.asReadonly();

  private registeredTools: string[] = [];
  // WebMCP v3 unregisters tools by aborting the signal passed to registerTool.
  private toolRegistrationController = new AbortController();
  // App tools and their UI resources live on the underlying McpServer, so they unregister by handle.
  private appRegistrations: { remove(): void }[] = [];

  registerTool(registerableTool: { name: string; description: string, inputSchema?: any, execute: (args: Record<string, any>) => Promise<string> }) {
    const webmcp = document.modelContext;
    if (webmcp) {
      webmcp.registerTool({
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
      }, { signal: this.toolRegistrationController.signal });
      this.registeredTools.push(registerableTool.name);
      console.log("WebMCP tool registered", registerableTool.name);
    } else {
      console.log("WebMCP context not found; tool not registered.", document.modelContext);
    }
  }

  /**
   * Registers a frontend tool that renders an MCP app: like registerTool, plus `_meta.ui.resourceUri`
   * pointing at a resource registered with registerAppResource. The chat discovers the link from
   * that `_meta` in listTools, exactly like a server-side MCP app tool.
   */
  registerAppTool(registerableTool: { name: string; description: string, inputSchema?: any, resourceUri: string, execute: (args: Record<string, any>) => Promise<string> }) {
    const server = this.appServer();
    if (!server) {
      console.log("WebMCP server not found; app tool not registered.", document.modelContext);
      return;
    }
    const handle = registerAppTool(server, registerableTool.name, {
      description: registerableTool.description,
      inputSchema: fromJsonSchema(registerableTool.inputSchema ?? { type: "object", properties: {} }),
      _meta: { ui: { resourceUri: registerableTool.resourceUri } },
    }, async (args: unknown) => ({
      content: [{ type: "text", text: await registerableTool.execute((args ?? {}) as Record<string, any>) }],
    }));
    this.appRegistrations.push(handle);
    this.registeredTools.push(registerableTool.name);
    console.log("WebMCP app tool registered", registerableTool.name);
  }

  /** Registers the HTML an app tool's `_meta.ui.resourceUri` points at. */
  registerAppResource(resource: { uri: string; name: string; html: () => Promise<string>, csp?: McpUiResourceCsp, permissions?: McpUiResourcePermissions }) {
    const server = this.appServer();
    if (!server) {
      console.log("WebMCP server not found; app resource not registered.", document.modelContext);
      return;
    }
    const handle = registerAppResource(server, resource.name, resource.uri, { mimeType: RESOURCE_MIME_TYPE }, async () => ({
      contents: [{
        uri: resource.uri,
        mimeType: RESOURCE_MIME_TYPE,
        text: await resource.html(),
        ...(resource.csp || resource.permissions
          ? { _meta: { ui: { csp: resource.csp, permissions: resource.permissions } } }
          : {}),
      }],
    }));
    this.appRegistrations.push(handle);
  }

  private appServer(): AppServer | undefined {
    const mcpServer = (document.modelContext as { mcpServer?: AppServer } | undefined)?.mcpServer;
    return mcpServer && typeof mcpServer.registerTool === 'function' ? mcpServer : undefined;
  }

  unregisterTools() {
    // Aborting the shared signal unregisters every tool registered with it.
    this.toolRegistrationController.abort();
    this.toolRegistrationController = new AbortController();
    for (const registration of this.appRegistrations) {
      registration.remove();
    }
    this.appRegistrations = [];
    this.registeredTools = [];
  }

  /** The tab's WebMCP client, which McpAppComponent uses for frontend tools' apps. */
  getClient(): Client | undefined {
    return this.client;
  }

  /**
   * The MCP app a WebMCP tool renders, from its `_meta` as listTools returned it — so any tool on the
   * tab's WebMCP server that declares one gets its app, not just ones registered here.
   */
  uiResourceUriFor(toolName: string): string | undefined {
    const meta = this.toolsSignal().find(t => t.name === toolName)?._meta;
    const ui = meta?.['ui'] as { resourceUri?: unknown } | undefined;
    const resourceUri = ui?.resourceUri ?? meta?.[RESOURCE_URI_META_KEY];
    return typeof resourceUri === 'string' ? resourceUri : undefined;
  }

  async initializeClient(): Promise<void> {
    const skipInitializationInFavorOfOtherTabTransport = false;
    if (skipInitializationInFavorOfOtherTabTransport) {
      console.log('Skipping WebMCP client initialization to avoid TabClientTransport collision.');
      return;
    }

    console.log('Connecting to WebMCP server...');
    const transport = new TabClientTransport({ targetOrigin: window.origin });
    // Advertising the MCP Apps extension tells servers that check getUiCapability this host renders apps.
    const client = new Client(
      { name: 'agentic-todos', version: '1.0.0' },
      { capabilities: { extensions: { [EXTENSION_ID]: { mimeTypes: [RESOURCE_MIME_TYPE] } } } },
    );
    await client.connect(transport); // if the MCP-B extension is active, this will take over the transport
    this.client = client;
    console.log('Connected to WebMCP server.');

    console.log('Fetching available tools...');
    try {
      const listToolsResponse = await client.listTools();
      const tools = listToolsResponse.tools.map(t => ({ name: t.name, description: t.description || '', inputSchema: t.inputSchema, _meta: t._meta }));
      this.toolsSignal.set(tools);
      console.log('Available tools:', tools);
    } catch (error) {
      console.error('Error fetching tools. Likely a TabClientTransport collision.', error);
    }
  }

  async invokeTool(toolName: string, args?: Record<string, any>): Promise<WebmcpToolResponse> {
    const invokableTool = this.toolsSignal().find(t => t.name === toolName);
    if (invokableTool) {
      if (!this.client) {
        throw new Error('WebMCP client is not initialized. Call initializeClient() first.');
      }

      const response = await this.client.callTool({ name: toolName, arguments: args ?? {} }) as CallToolResult;
      console.log(`Tool ${toolName} invoked. Response:`, response);
      // If the response is a single text content, the model gets just the text.
      return { text: this.tryUnwrapSingleTextContent(response) ?? JSON.stringify(response), result: response };
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
