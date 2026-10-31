using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;

namespace AgenticTodos.Backend;

/// <summary>
/// Provides the model with what the client reports about the user's current view: the AG-UI request's
/// <c>context</c> entries, e.g. the MCP apps open in the side panel next to the chat and what each shows
/// (see mcp-apps.md). The AG-UI server SDK leaves <c>RunAgentInput.Context</c> on the request untouched.
/// <para>
/// The entries describe the current run only, so they go to the model as <see cref="AIContext.Instructions"/>,
/// which apply to this invocation and never enter the persisted history. Being instructions rather than a
/// message, they also reach the model on a continuation run that starts with tool results.
/// </para>
/// </summary>
internal sealed class ClientContextProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new AIContext { Instructions = Instructions(AIAgent.CurrentRunContext?.RunOptions) });

    /// <summary>The run's AG-UI context entries as instructions; null when there are none.</summary>
    internal static string? Instructions(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chatOptions }) return null;
        if (!chatOptions.TryGetRunAgentInput(out var input) || input.Context is not { Count: > 0 } context) return null;

        var text = new StringBuilder("Context from the user's client for this turn:");
        foreach (var entry in context)
        {
            text.Append("\n\n## ").Append(entry.Description).Append('\n').Append(entry.Value);
        }
        return text.ToString();
    }
}
