using Microsoft.Agents.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Looks up the agents the AG-UI endpoint can route to. Both members are asynchronous on purpose: an
/// implementation is expected to fetch agent definitions from a store (a database, a config service),
/// and the routing agent resolves one per request — so the seam has to allow awaiting.
/// </summary>
public interface IAgentProvider
{
    ValueTask<IReadOnlyList<string>> GetAliasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns <see langword="null"/> when no agent is registered under <paramref name="alias"/>.</summary>
    ValueTask<AIAgent?> GetAsync(string alias, CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-process implementation: agents are keyed singletons in DI, so both lookups complete
/// synchronously. The <see cref="ValueTask{TResult}"/> return types mean that costs no allocation.
/// </summary>
public class AgentProvider(IServiceProvider services) : IAgentProvider
{
    public ValueTask<IReadOnlyList<string>> GetAliasesAsync(CancellationToken cancellationToken = default) =>
        new(services.GetRequiredKeyedService<List<string>>("agentAliases"));

    public ValueTask<AIAgent?> GetAsync(string alias, CancellationToken cancellationToken = default) =>
        new(services.GetKeyedService<AIAgent>(alias));
}
