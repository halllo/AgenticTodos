using AgenticTodos.Backend;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgenticTodos.Tests;

public class MergeToolResultsMiddlewareTest
{
    [Fact]
    public async Task AddsMissingBackendToolResult()
    {
        // Assistant has 2 tool calls, but only frontend tool result is present
        var backendId = "tooluse_backend";
        var frontendId = "tooluse_frontend";
        
        var assistant = CreateAssistantMessageWithToolCalls([backendId, frontendId]);
        var toolMessage = CreateToolMessageWithResult(frontendId, "result");
        var captured = new List<IEnumerable<ChatMessage>>();
        
        // Run message through middleware
        await new MergeToolResultsMiddleware(new MockChatClient(captured))
            .GetResponseAsync([assistant, toolMessage]);
        
        // Both tool results should now be present
        var toolResultIds = ExtractToolResultIds(captured[0].ToList()[1].Contents!).ToList();
        Assert.Contains(backendId, toolResultIds);
        Assert.Contains(frontendId, toolResultIds);
    }
    
    private static readonly Type? FunctionCallContentType = typeof(ChatMessage).Assembly
        .GetTypes()
        .FirstOrDefault(t => t.Name == "FunctionCallContent");
    
    private static readonly System.Reflection.ConstructorInfo? FunctionCallContentConstructor = 
        FunctionCallContentType?.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 2 && c.GetParameters()[0].ParameterType == typeof(string));
    
    private static ChatMessage CreateAssistantMessageWithToolCalls(IEnumerable<string> toolCallIds)
    {
        if (FunctionCallContentConstructor == null)
            throw new InvalidOperationException("FunctionCallContent constructor not found");
        
        var args = FunctionCallContentConstructor.GetParameters().Length == 2 
            ? (Func<string, object[]>)(id => [id, "test_function"])
            : id => [id, "test_function", null!];
        
        var contents = toolCallIds.Select(id => (AIContent)FunctionCallContentConstructor.Invoke(args(id))).ToList();
        return new ChatMessage(ChatRole.Assistant, contents);
    }
    
    private static readonly System.Reflection.ConstructorInfo? FunctionResultContentConstructor = 
        typeof(ChatMessage).Assembly
            .GetTypes()
            .FirstOrDefault(t => t.Name == "FunctionResultContent")
            ?.GetConstructors()
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(object);
            });
    
    private static ChatMessage CreateToolMessageWithResult(string toolCallId, string result)
    {
        if (FunctionResultContentConstructor == null)
            throw new InvalidOperationException("FunctionResultContent constructor not found");
        
        return new ChatMessage(ChatRole.Tool, [(AIContent)FunctionResultContentConstructor.Invoke([toolCallId, result])]);
    }
    
    private static IEnumerable<string> ExtractToolResultIds(IEnumerable<AIContent> contents) =>
        contents
            .Where(c => c.GetType().Name.Contains("FunctionResult"))
            .Select(c => (c.GetType().GetProperty("CallId") ?? c.GetType().GetProperty("callId"))?.GetValue(c)?.ToString())
            .Where(id => id != null)!;
    
    private class MockChatClient : IChatClient
    {
        private readonly List<IEnumerable<ChatMessage>> _capturedMessages;
        
        public MockChatClient(List<IEnumerable<ChatMessage>> capturedMessages)
        {
            _capturedMessages = capturedMessages;
        }
        
        public void Dispose() { }
        
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _capturedMessages.Add(messages);
            return Task.FromResult((ChatResponse)Activator.CreateInstance(typeof(ChatResponse))!);
        }
        
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _capturedMessages.Add(messages);
            return AsyncEnumerable.Repeat(new ChatResponseUpdate { ResponseId = "mock" }, 1);
        }
    }
}