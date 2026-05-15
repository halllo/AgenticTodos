import {
  AfterViewInit,
  Component,
  ElementRef,
  input,
  OnDestroy,
  ViewChild,
  inject,
  signal,
} from '@angular/core';
import {
  AppBridge,
  PostMessageTransport,
  buildAllowAttribute,
  RESOURCE_MIME_TYPE,
} from '@modelcontextprotocol/ext-apps/app-bridge';
import type { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import { McpClientService } from './mcp-client.service';
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
  toolInput = input<Record<string, unknown>>({});
  toolResult = input<unknown>(null);

  @ViewChild('iframeEl') iframeRef!: ElementRef<HTMLIFrameElement>;

  displayMode = signal<'inline' | 'fullscreen'>('inline');

  private mcpClientService = inject(McpClientService);
  private appBridge: AppBridge | null = null;
  private sandboxAbort = new AbortController();
  private iframeResizeObserver: ResizeObserver | null = null;

  async ngAfterViewInit(): Promise<void> {
    try {
      await this.initApp();
    } catch (err) {
      console.error('[McpAppComponent] failed to initialize MCP app', err);
    }
  }

  private async initApp(): Promise<void> {
    const iframe = this.iframeRef.nativeElement;

    const serverInfo = await this.mcpClientService.getServerInfo();

    // Read HTML + optional CSP/permissions metadata from the MCP resource
    const resource = await serverInfo.client.readResource({ uri: this.resourceUri() });
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
      serverInfo.client,
      { name: 'AgenticTodos', version: '1.0.0' },
      {
        openLinks: {},
        serverTools: serverInfo.client.getServerCapabilities()?.tools,
        serverResources: serverInfo.client.getServerCapabilities()?.resources,
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
      return {};
    };

    appBridge.onloggingmessage = (params) => {
      console.log('[McpAppComponent] Log from MCP App:', params);
    };

    appBridge.onupdatemodelcontext = async (params) => {
      console.log('[McpAppComponent] Model context update from MCP App:', params);
      return {};
    };

    const initialized = hookInitialized(appBridge);
    await appBridge.connect(
      new PostMessageTransport(iframe.contentWindow!, iframe.contentWindow!),
    );
    await appBridge.sendSandboxResourceReady({ html, csp, permissions });
    await initialized;

    appBridge.sendToolInput({ arguments: this.toolInput() });

    const result = this.toolResult();
    if (result != null) {
      appBridge.sendToolResult(result as CallToolResult);
    } else {
      appBridge.sendToolCancelled({ reason: 'No result available' });
    }
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
