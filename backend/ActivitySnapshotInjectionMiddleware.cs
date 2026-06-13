using System.Diagnostics.CodeAnalysis;

namespace AgenticTodos.Backend;

/// <summary>
/// Concrete <see cref="SseEventInjectionMiddleware"/> for the <c>/agui</c> SSE stream. Routes each
/// event through the activity-snapshot injectors (MCP apps first, then EU AI Act risk) so the
/// frontend receives the custom activity snapshots the AG-UI protocol doesn't model natively.
/// Injectors are tried in order; the first one to either suppress (<c>null</c>) or claim an event
/// (non-empty replacement) wins, and an injector that returns an empty sequence ("not mine —
/// forward unchanged") lets the next injector try.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core via UseMiddleware<T>")]
internal sealed class ActivitySnapshotInjectionMiddleware(RequestDelegate next)
    : SseEventInjectionMiddleware(next)
{
    protected override IEnumerable<string>? Inject(string eventJson) => TryInject(eventJson);

    /// <summary>
    /// Pure composition policy behind <see cref="Inject"/>, exposed for unit testing the routing
    /// without spinning up the request pipeline.
    /// </summary>
    internal static IEnumerable<string>? TryInject(string eventJson)
    {
        // MCP apps first.
        var mcp = McpAppsActivityInjector.TryInjectActivitySnapshot(eventJson);
        if (mcp is null) return null;                              // suppressed by the MCP-apps injector
        var mcpList = mcp as IReadOnlyList<string> ?? mcp.ToList();
        if (mcpList.Count > 0) return mcpList;                     // claimed by the MCP-apps injector

        // EU AI Act risk classification.
        return EUAIActRiskActivityInjector.TryInjectActivitySnapshot(eventJson);
    }
}
