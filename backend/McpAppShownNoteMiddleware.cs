using System.Text.Json;
using AGUI.Server;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgenticTodos.Backend;

internal static class McpAppShownNoteMiddleware
{
    extension(ChatClientBuilder builder)
    {
        /// <summary>
        /// Keeps the model from repeating what an MCP app already shows. A tool whose MCP metadata carries a
        /// <c>ui.resourceUri</c> has its result rendered as an app (<see cref="DetectMcpAppsActivityMiddleware"/>), but the
        /// model does not know that and restates the result in its answer.
        /// <para>
        /// Only the client knows whether it renders apps, so it declares it per run (AG-UI <c>forwardedProps.mcpApps: true</c>).
        /// For such a run, this appends <see cref="Injector.AppShownNote"/> to each app tool's result on its way to the model; the
        /// stored history keeps the plain result, so a text-only client continuing the thread gets full answers.
        /// </para>
        /// </summary>
        public ChatClientBuilder UseMcpAppShownNote() => builder.Use(inner => new Injector(inner));
    }

    internal sealed class Injector(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        internal const string AppShownNote =
            "[The user sees this tool result as an interactive app in the chat. Do not repeat or summarize what it shows; "
            + "answer in at most one short sentence, adding only what the app does not show.]";

        /// <summary>The AG-UI <c>forwardedProps</c> key a client sets to <c>true</c> when it renders MCP apps.</summary>
        internal const string ForwardedPropsKey = "mcpApps";

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(ForModel(messages, options), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(ForModel(messages, options), options, cancellationToken);

        internal static IEnumerable<ChatMessage> ForModel(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (!ClientRendersApps(options)) return messages;

            var appTools = options!.Tools?
                .Select(t => t.GetService<McpClientTool>())
                .Where(t => t?.ProtocolTool.Meta?["ui"]?["resourceUri"] is not null)
                .Select(t => t!.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (appTools is not { Count: > 0 }) return messages;

            var list = messages.ToList();
            var appCallIds = list
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Where(c => appTools.Contains(c.Name))
                .Select(c => c.CallId)
                .ToHashSet(StringComparer.Ordinal);

            return list.Select(message =>
            {
                if (!message.Contents.Any(c => c is FunctionResultContent r && appCallIds.Contains(r.CallId) && !IsError(r.Result))) return message;
                var copy = message.Clone();
                copy.Contents = [.. message.Contents.Select(c =>
                    c is FunctionResultContent r && appCallIds.Contains(r.CallId) && !IsError(r.Result)
                        ? new FunctionResultContent(r.CallId, WithNote(r.Result))
                        : c)];
                return copy;
            });
        }

        /// <summary>Whether the AG-UI request behind this call declares that its client renders MCP apps.</summary>
        internal static bool ClientRendersApps(ChatOptions? options) =>
            options is not null && options.TryGetRunAgentInput(out var input)
            && input.ForwardedProperties is JsonElement { ValueKind: JsonValueKind.Object } props
            && props.TryGetProperty(ForwardedPropsKey, out var flag) && flag.ValueKind == JsonValueKind.True;

        /// <summary>
        /// McpClientTool returns a failed call as the <c>CallToolResult</c> JSON with <c>isError: true</c>. It gets no
        /// note, so the model is free to explain the error or retry.
        /// </summary>
        private static bool IsError(object? result) =>
            result is JsonElement { ValueKind: JsonValueKind.Object } el
            && el.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;

        internal static object WithNote(object? result) => result switch
        {
            // McpClientTool's usual result: the content blocks as AIContents.
            IEnumerable<AIContent> contents => (List<AIContent>)[.. contents, new TextContent(AppShownNote)],
            AIContent content => (List<AIContent>)[content, new TextContent(AppShownNote)],
            string text => $"{text}\n\n{AppShownNote}",
            // Anything else, e.g. a result reloaded from stored history as JSON.
            _ => $"{JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)}\n\n{AppShownNote}",
        };
    }
}
