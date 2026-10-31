import {
  AfterViewInit,
  Component,
  ElementRef,
  input,
  OnDestroy,
  ViewChild,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import {
  AppBridge,
  PostMessageTransport,
  buildAllowAttribute,
  RESOURCE_MIME_TYPE,
} from '@modelcontextprotocol/ext-apps/app-bridge';
import type { CallToolResult, Client } from '@modelcontextprotocol/client';
import { McpClientService } from './mcp-client.service';
import { WebmcpService } from './webmcp.service';
import { HOST_STYLE_VARIABLES } from './host-styles';

// The outer sandbox iframe must be served from a different origin than the host.
// The ASP.NET backend (port 5288) provides this cross-origin separation.
const SANDBOX_URL = 'http://localhost:5288/sandbox.html';

// How long to wait for the sandbox-proxy-ready message before giving up.
const SANDBOX_READY_TIMEOUT_MS = 10_000;

@Component({
  selector: 'app-mcp-app',
  standalone: true,
  template: `
    <div [class]="displayMode() === 'fullscreen' ? 'mcp-app fullscreen' : 'mcp-app'">
      <iframe #iframeEl style="width:100%;max-width:100%;border:none;"></iframe>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .mcp-app { display: contents; }
    .mcp-app.fullscreen {
      position: fixed; inset: 0; z-index: 1000;
      display: flex; flex-direction: column;
      background: var(--color-background, #fff);
      padding: 0;
    }
    .mcp-app.fullscreen iframe { flex: 1; height: 100%; width: 100%; }
    .mcp-app__exit {
      position: absolute; top: 0.5rem; right: 0.75rem;
      background: transparent; border: none; cursor: pointer;
      font-size: 1.25rem; z-index: 1001;
    }
  `],
})
export class McpAppComponent implements AfterViewInit, OnDestroy {
  resourceUri = input.required<string>();
  // Both absent for an app opened from the launcher: it starts without a tool call. A later change of
  // either (a follow-up tool call delivered to an app in the side panel) is sent to the running app.
  toolInput = input<Record<string, unknown> | undefined>(undefined);
  toolResult = input<unknown>(null);
  // Which MCP server serves the app's resource and answers its tool calls: the backend's MCP server
  // through the relay, or the tab's WebMCP server for frontend tools.
  source = input<'server' | 'frontend'>('server');
  // Delivers an app's ui/message to the chat as a user turn; resolves false when the chat cannot take it now.
  sendMessage = input<(text: string) => Promise<boolean>>();
  // Receives what the app reports about itself (ui/update-model-context), as text for the model.
  updateModelContext = input<(text: string) => void>();

  @ViewChild('iframeEl') iframeRef!: ElementRef<HTMLIFrameElement>;

  displayMode = signal<'inline' | 'fullscreen'>('inline');

  private mcpClientService = inject(McpClientService);
  private webmcp = inject(WebmcpService);
  private appBridge: AppBridge | null = null;
  private sandboxAbort = new AbortController();
  private iframeResizeObserver: ResizeObserver | null = null;
  /** Set once the app is initialized and has been sent its initial tool data. */
  private ready = false;

  constructor() {
    effect(() => {
      const toolInput = this.toolInput();
      const toolResult = this.toolResult();
      if (this.ready) {
        untracked(() => this.sendToolData(toolInput, toolResult));
      }
    });
  }

  async ngAfterViewInit(): Promise<void> {
    try {
      await this.initApp();
    } catch (err) {
      console.error('[McpAppComponent] failed to initialize MCP app', err);
    }
  }

  private async initApp(): Promise<void> {
    const iframe = this.iframeRef.nativeElement;

    const client = await this.resolveClient();

    // Read HTML + optional CSP/permissions metadata from the MCP resource
    const resource = await client.readResource({ uri: this.resourceUri() });
    const content = resource.contents[0];

    // Verify this is actually an MCP app resource before treating as HTML
    if (content.mimeType !== RESOURCE_MIME_TYPE) {
      throw new Error(`Unexpected MIME type "${content.mimeType}" for resource ${this.resourceUri()}`);
    }

    const html =
      'blob' in content
        ? atob((content as unknown as { blob: string }).blob)
        : (content as unknown as { text: string }).text;
    const uiMeta = (content as any)._meta?.ui ?? (content as any).meta?.ui;
    const csp = uiMeta?.csp;
    const permissions = uiMeta?.permissions;

    // Load the outer sandbox iframe — blocks until sandbox-proxy-ready
    const loaded = await loadSandboxProxy(iframe, csp, permissions, this.sandboxAbort.signal);
    if (!loaded) return;

    const appBridge = new AppBridge(
      client,
      { name: 'AgenticTodos', version: '1.0.0' },
      {
        openLinks: {},
        serverTools: client.getServerCapabilities()?.tools,
        serverResources: client.getServerCapabilities()?.resources,
        updateModelContext: { text: {} },
      },
      {
        hostContext: {
          theme: 'light',
          platform: 'web',
          styles: { variables: HOST_STYLE_VARIABLES },
          containerDimensions: { maxHeight: 6000 },
          displayMode: 'inline',
          availableDisplayModes: ['inline', 'fullscreen'],
        },
      },
    );
    this.appBridge = appBridge;

    // Notify the app when the iframe container width changes (e.g. layout shifts, resize)
    this.iframeResizeObserver = new ResizeObserver(([entry]) => {
      const width = Math.round(entry.contentRect.width);
      if (width > 0) {
        appBridge.sendHostContextChange({ containerDimensions: { width, maxHeight: 6000 } });
      }
    });
    this.iframeResizeObserver.observe(iframe);

    appBridge.onrequestdisplaymode = async ({ mode }) => {
      const newMode = mode === 'fullscreen' ? 'fullscreen' : 'inline';
      this.displayMode.set(newMode);
      appBridge.sendHostContextChange({ displayMode: newMode });
      return { mode: newMode };
    };

    appBridge.onsizechange = async ({ width, height }) => {
      console.debug('[McpAppComponent] Size change requested by MCP App:', { width, height });
      if (this.displayMode() === 'fullscreen') return;
      if (height !== undefined) iframe.style.height = `${height}px`;
      
      //if (width !== undefined) iframe.style.width = `${width}px`; 
      // since we give all apps width:100% (.chat__message.chat__message--activity > .chat__content), they cannot really control their width anymore (and shouldn't try to), so we ignore width change requests for now
    };

    appBridge.onopenlink = async ({ url }) => {
      window.open(url, '_blank', 'noopener,noreferrer');
      return {};
    };

    appBridge.onmessage = async (params) => {
      console.log('[McpAppComponent] Message from MCP App:', params);
      const text = params.content
        .flatMap(c => (c.type === 'text' ? [c.text] : []))
        .join('\n');
      const send = this.sendMessage();
      const delivered = !!text && !!send && (await send(text));
      return delivered ? {} : { isError: true };
    };

    appBridge.onloggingmessage = (params) => {
      console.log('[McpAppComponent] Log from MCP App:', params);
    };

    appBridge.onupdatemodelcontext = async (params) => {
      console.log('[McpAppComponent] Model context update from MCP App:', params);
      const text = [
        ...(params.content ?? []).flatMap(c => (c.type === 'text' ? [c.text] : [])),
        ...(params.structuredContent ? [JSON.stringify(params.structuredContent)] : []),
      ].join('\n');
      this.updateModelContext()?.(text);
      return {};
    };

    const initialized = hookInitialized(appBridge);
    await appBridge.connect(
      new PostMessageTransport(iframe.contentWindow!, iframe.contentWindow!),
    );
    await appBridge.sendSandboxResourceReady({ html, csp, permissions });
    await initialized;

    this.sendToolData(this.toolInput(), this.toolResult());
    this.ready = true;
  }

  private sendToolData(toolInput: Record<string, unknown> | undefined, toolResult: unknown): void {
    if (!this.appBridge || (toolInput === undefined && toolResult == null)) {
      return; // opened from the launcher: no tool call to report
    }
    this.appBridge.sendToolInput({ arguments: toolInput ?? {} });
    if (toolResult != null) {
      this.appBridge.sendToolResult(toolResult as CallToolResult);
    } else {
      this.appBridge.sendToolCancelled({ reason: 'No result available' });
    }
  }

  private async resolveClient(): Promise<Client> {
    if (this.source() === 'server') {
      return (await this.mcpClientService.getServerInfo()).client;
    }
    const client = this.webmcp.getClient();
    if (!client) {
      throw new Error('WebMCP client is not initialized.');
    }
    return client;
  }

  exitFullscreen(): void {
    this.displayMode.set('inline');
    this.appBridge?.sendHostContextChange({ displayMode: 'inline' });
  }

  ngOnDestroy(): void {
    this.sandboxAbort.abort();
    this.iframeResizeObserver?.disconnect();
    this.appBridge?.teardownResource({}).catch(() => {});
  }
}

function loadSandboxProxy(
  iframe: HTMLIFrameElement,
  csp?: unknown,
  permissions?: unknown,
  signal?: AbortSignal,
): Promise<boolean> {
  if (iframe.src) return Promise.resolve(false);
  iframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms');
  const allow = buildAllowAttribute(permissions as any);
  if (allow) iframe.setAttribute('allow', allow);

  const url = new URL(SANDBOX_URL);
  if (csp) url.searchParams.set('csp', JSON.stringify(csp));

  return new Promise((resolve, reject) => {
    const cleanup = () => {
      window.removeEventListener('message', listener);
      clearTimeout(timer);
    };

    const listener = ({ source, data }: MessageEvent) => {
      if (
        source === iframe.contentWindow &&
        data?.method === 'ui/notifications/sandbox-proxy-ready'
      ) {
        cleanup();
        resolve(true);
      }
    };

    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`Sandbox at ${SANDBOX_URL} did not respond within ${SANDBOX_READY_TIMEOUT_MS}ms`));
    }, SANDBOX_READY_TIMEOUT_MS);

    signal?.addEventListener('abort', () => {
      cleanup();
      resolve(false);
    });

    window.addEventListener('message', listener);
    iframe.src = url.href;
  });
}

function hookInitialized(appBridge: AppBridge): Promise<void> {
  return new Promise((resolve) => {
    const prev = appBridge.oninitialized;
    appBridge.oninitialized = (...args) => {
      resolve();
      appBridge.oninitialized = prev;
      prev?.(...args);
    };
  });
}
