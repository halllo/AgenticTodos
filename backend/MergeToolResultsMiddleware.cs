using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Merges missing tool results into the message history before sending to Amazon Bedrock.
/// This fixes the issue where parallel tool calls (backend + frontend) result in missing backend tool results.
/// </summary>
public class MergeToolResultsMiddleware(IChatClient inner) : IChatClient
{
    private static readonly Type? FunctionResultContentType = typeof(ChatMessage).Assembly
        .GetTypes()
        .FirstOrDefault(t => t.Name == "FunctionResultContent");
    
    private static readonly System.Reflection.ConstructorInfo? FunctionResultContentConstructor = 
        FunctionResultContentType?.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(object);
            });

    public void Dispose() => inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var fixedMessages = MergeToolResults(messages.ToList());
        return await inner.GetResponseAsync(fixedMessages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => 
        inner.GetService(serviceType, serviceKey);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fixedMessages = MergeToolResults(messages.ToList());
        await foreach (var update in inner.GetStreamingResponseAsync(fixedMessages, options, cancellationToken))
        {
            yield return update;
        }
    }
    
    private List<ChatMessage> MergeToolResults(List<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages);
        
        for (int i = 0; i < result.Count; i++)
        {
            var msg = result[i];
            if (msg.Role != ChatRole.Assistant || msg.Contents == null)
                continue;
            
            var toolCallIds = ExtractToolCallIds(msg.Contents).ToList();
            if (toolCallIds.Count == 0)
                continue;
            
            // Find existing tool results after this assistant message
            // Backend tool results are executed server-side and should be in the message history,
            // but frontend tool results come from the client and may arrive separately
            var (toolResults, existingToolMessage, existingToolMessageIndex) = FindToolResults(result, i);
            var missingToolCallIds = toolCallIds.Except(toolResults).ToList();
            
            if (missingToolCallIds.Count == 0 || FunctionResultContentConstructor == null)
                continue;
            
            // Create placeholder results for missing backend tool calls
            var newFunctionResults = missingToolCallIds
                .Select(id => CreateFunctionResultContent(id, DateTimeOffset.UtcNow.ToString("O")))
                .Where(x => x != null)
                .Cast<AIContent>()
                .ToList();
            
            if (newFunctionResults.Count == 0)
                continue;
            
            // Merge into existing tool message if present, otherwise create new one
            if (existingToolMessage != null && existingToolMessageIndex >= 0)
            {
                var mergedContents = (existingToolMessage.Contents ?? []).Concat(newFunctionResults).ToList();
                result[existingToolMessageIndex] = new ChatMessage(ChatRole.Tool, mergedContents);
            }
            else
            {
                result.Insert(i + 1, new ChatMessage(ChatRole.Tool, newFunctionResults));
                i++;
            }
        }
        
        return result;
    }
    
    private static string? GetCallId(AIContent content)
    {
        var type = content.GetType();
        return (type.GetProperty("CallId") ?? type.GetProperty("callId"))?.GetValue(content)?.ToString();
    }
    
    private static IEnumerable<string> ExtractToolCallIds(IEnumerable<AIContent> contents) =>
        contents
            .Where(c => c.GetType().Name.Contains("FunctionCall"))
            .Select(GetCallId)
            .Where(id => id != null)!;
    
    private static (HashSet<string> toolResults, ChatMessage? existingToolMessage, int existingToolMessageIndex) FindToolResults(List<ChatMessage> messages, int startIndex)
    {
        var toolResults = new HashSet<string>();
        ChatMessage? existingToolMessage = null;
        int existingToolMessageIndex = -1;
        
        for (int j = startIndex + 1; j < messages.Count; j++)
        {
            var nextMsg = messages[j];
            if (nextMsg.Role == ChatRole.Tool && nextMsg.Contents != null)
            {
                existingToolMessage ??= nextMsg;
                existingToolMessageIndex = j;
                
                foreach (var content in nextMsg.Contents)
                {
                    if (content.GetType().Name.Contains("FunctionResult"))
                    {
                        var callId = GetCallId(content);
                        if (callId != null)
                            toolResults.Add(callId);
                    }
                }
            }
            else if (nextMsg.Role == ChatRole.Assistant || nextMsg.Role == ChatRole.User)
            {
                break;
            }
        }
        
        return (toolResults, existingToolMessage, existingToolMessageIndex);
    }
    
    private static AIContent? CreateFunctionResultContent(string callId, object result)
    {
        try
        {
            return (AIContent)FunctionResultContentConstructor!.Invoke([callId, result]);
        }
        catch
        {
            return null;
        }
    }
}

