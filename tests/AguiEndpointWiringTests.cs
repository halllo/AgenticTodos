using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgenticTodos.Tests;

/// <summary>
/// <see cref="AguiClientContentMappingTests"/> covers the content mappings as pure functions. These
/// tests cover the wiring that decides whether those mappings ever run at all — the stream options the
/// endpoint carries as metadata, the JSON registration the serialized events depend on, and the error
/// middleware's scope. Every one of them fails at request time rather than at build time, and none is
/// touched by another test.
/// </summary>
public class AguiEndpointWiringTests
{
    [Fact]
    public void StreamOptions_ReachTheEndpointAsMetadata()
    {
        // MapAGUIServer reads the configuration as
        //   context.GetEndpoint()?.Metadata.GetMetadata<AGUIStreamOptions>()
        //     ?? RequestServices.GetService<IOptions<AGUIStreamOptions>>()?.Value
        // and this app registers no IOptions<AGUIStreamOptions>, so the WithMetadata call is the only
        // path. Dropping it would silently remove STATE_SNAPSHOT and both ACTIVITY_SNAPSHOT kinds while
        // leaving every other test green.
        var app = BuildApp();

        app.MapAGUIViaHttpRoutingAgent();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints));
        Assert.NotNull(endpoint.Metadata.GetMetadata<AGUIStreamOptions>());
    }

    [Fact]
    public async Task TheMappedRoute_IsInsideTheRunErrorMiddlewaresScope()
    {
        // AguiRunErrorMiddleware is scoped by path prefix, so an eager failure on the route the endpoint
        // is *actually* mapped on has to be wrapped. Asserting the mapped pattern against
        // AGUIEndpoint.RoutedPathPrefix would only compare a constant with the interpolation it is built
        // from; driving a request down the pipeline instead catches the real drift — a route moved out
        // from under the middleware turns every eager failure back into the invisible HTTP 500 the
        // middleware exists to prevent.
        var app = BuildApp();

        app.MapAGUIViaHttpRoutingAgent();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints));
        var mapped = Assert.IsType<RouteEndpoint>(endpoint).RoutePattern.RawText!;
        var path = mapped.Replace("{alias}", "openai");

        var (events, _) = await AguiRunErrorPipeline.RunAsync(
            _ => throw new AguiClientException("Unknown agent alias 'nope'."), path);

        Assert.Equal(["RUN_STARTED", "RUN_ERROR"], events.Select(e => e.GetProperty("type").GetString()));

        // And the scope really is a scope, not a catch-all: the same failure one segment to the side is
        // left to become an HTTP 500. (Without this, a prefix of "/" would pass the assertion above.)
        await Assert.ThrowsAsync<AguiClientException>(
            () => AguiRunErrorPipeline.RunAsync(_ => throw new AguiClientException("boom"), "/not" + path));
    }

    /// <summary>
    /// The other half of the two-step contract in <c>AguiClientContent.cs</c>: the SDK serializes every
    /// response update into the event's <c>rawEvent</c> field, so a content type that is mapped but not
    /// registered for AIContent polymorphism throws <c>NotSupportedException</c> on first use.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClientContentTypes))]
    public void EveryMappedContentType_SurvivesRawEventSerialization(AIContent content)
    {
        var json = JsonSerializer.Serialize(content, AguiJson.Options);

        // Nested too — rawEvent serializes the whole update, not the content on its own.
        var nested = JsonSerializer.Serialize(new ChatResponseUpdate { Contents = [content] }, AguiJson.Options);

        Assert.Contains("agenticTodos.", json);
        Assert.Contains("agenticTodos.", nested);
        Assert.NotNull(JsonSerializer.Deserialize<AIContent>(json, AguiJson.Options));
    }

    [Fact]
    public void AnUnregisteredContentType_Fails_SoTheTestAboveIsMeaningful()
    {
        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize<AIContent>(new UnregisteredContent(), AguiJson.Options));
    }

    // ---------------------------------------------------------------------------
    // The registration that makes ConfigureAguiJson apply to the running app.
    //
    // The test above proves the *content* of ConfigureAguiJson, through the options this test project
    // composes itself (AguiJson.Options). That says nothing about whether the app ever applies it: the
    // AG-UI endpoint (de)serializes with Microsoft.AspNetCore.Http.Json.JsonOptions, and with the
    // registration gone every STATE_SNAPSHOT and ACTIVITY_SNAPSHOT throws NotSupportedException on
    // rawEvent serialization at request time — with the whole suite still green.
    //
    // What is asserted here is the seam, AddAGUIJson, from a container built the way the app builds one.
    // NOT asserted: that Program.cs calls it. Program.cs is a top-level statement file whose composition
    // root is only reachable by starting the real server (which needs live provider credentials), so the
    // call site itself is unpinned — a maintainer deleting `builder.Services.AddAGUIJson();` still gets
    // a green suite. Making the seam impossible to *misuse* is as far as a test can go here.
    // ---------------------------------------------------------------------------

    [Fact]
    public void AddAGUIJson_AppliesTheContentTypeRegistrations_ToTheEndpointsJsonOptions()
    {
        var options = EndpointJsonOptions(new ServiceCollection().AddAGUIJson());

        // Exactly what the SSE result does with a STATE_SNAPSHOT's rawEvent.
        var json = JsonSerializer.Serialize<AIContent>(
            new ConversationStateContent(JsonSerializer.SerializeToElement(new { counter = 3 })), options);

        Assert.Contains("agenticTodos.conversationState", json);
        Assert.IsType<ConversationStateContent>(JsonSerializer.Deserialize<AIContent>(json, options));
    }

    [Fact]
    public void WithoutAddAGUIJson_TheSameSerializationThrows_SoTheTestAboveIsMeaningful()
    {
        // AddOptions() only so IOptions<JsonOptions> resolves at all — AddAGUIJson pulls the options
        // infrastructure in itself, a plain ServiceCollection has none. This is the app minus one line.
        var options = EndpointJsonOptions(new ServiceCollection().AddOptions());

        var ex = Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<AIContent>(
            new ConversationStateContent(JsonSerializer.SerializeToElement(new { counter = 3 })), options));

        // The exact failure a client would see mid-stream, once the response is already committed.
        Assert.Contains("polymorphic type", ex.Message);
    }

    [Fact]
    public void AddAGUIServerAlone_DoesNotRegisterTheAppsContentTypes()
    {
        // The realistic drift: AddAGUIServer() also configures JsonOptions (it chains in the Agent
        // Framework and AG-UI type info resolvers), which makes it look as though the app's own content
        // types are covered. They are not — the two registrations compose rather than overlap.
        //
        // AddOptions() is needed on this side and not on AddAGUIJson's because AddAGUIJson goes through
        // services.Configure<T>(), which pulls the options infrastructure in; AddAGUIServer registers
        // its IConfigureOptions<JsonOptions> without doing so, and relies on the host having added it.
        var options = EndpointJsonOptions(new ServiceCollection().AddOptions().AddAGUIServer());

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<AIContent>(
            new ConversationStateContent(JsonSerializer.SerializeToElement(new { counter = 3 })), options));
    }

    /// <summary>
    /// Resolves the JSON options the minimal-API SSE result flows through — <c>Http.Json.JsonOptions</c>,
    /// not the MVC namesake — from a container, the way a request does.
    /// </summary>
    private static JsonSerializerOptions EndpointJsonOptions(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;

    public static TheoryData<AIContent> ClientContentTypes()
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return
        [
            new ConversationStateContent(empty),
            new McpAppActivityContent("m1", "ui://todos", empty, empty),
            new EUAIActRiskActivityContent("m1", "High", "cat", "reason"),
        ];
    }

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAGUIServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IAgentProvider, AgentProvider>();
        // MapAGUIServer resolves the endpoint's store as GetKeyedService<AgentSessionStore>("routed"),
        // which this supplies; nothing else in the graph is read at map time.
        builder.Services.AddAGUISessionStore();
        return builder.Build();
    }

    private sealed class UnregisteredContent : AIContent;
}
