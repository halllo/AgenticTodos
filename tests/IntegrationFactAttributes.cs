using Microsoft.Extensions.Configuration;

namespace AgenticTodos.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that drive a <b>running</b> backend over HTTP and cost real
/// LLM calls. They are skipped unless <c>AG_UI_ENDPOINT</c> names the AG-UI endpoint to exercise, so a
/// plain <c>dotnet test</c> stays hermetic and free.
/// <para>
/// Example: <c>AG_UI_ENDPOINT=http://localhost:5288/agents/routed/openai/agui dotnet test</c>
/// </para>
/// </summary>
public sealed class AgUiEndpointFactAttribute : FactAttribute
{
    internal const string EndpointVariable = "AG_UI_ENDPOINT";

    public AgUiEndpointFactAttribute()
    {
        if (!IsConfigured)
        {
            Skip = $"Set {EndpointVariable} to a running AG-UI endpoint to run this integration test.";
        }
    }

    internal static bool IsConfigured =>
        Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 };

    internal static string Endpoint =>
        Environment.GetEnvironmentVariable(EndpointVariable)
        ?? throw new InvalidOperationException($"{EndpointVariable} is not set.");
}

/// <summary>
/// The configuration keys the live-provider tests read, named once. Both the <see cref="LiveLlmFactAttribute"/>
/// guard and the test body that consumes a key refer to the same constant, so the guard cannot check a
/// key the test does not use — a mismatch would either skip a runnable test forever or admit one that
/// then fails on the credential the guard was meant to have caught.
/// </summary>
internal static class LiveLlmKeys
{
    internal const string OpenAIApiKey = "OPENAI_API_KEY";
    internal const string BedrockAccessKeyId = "AWSBedrockAccessKeyId";
    internal const string BedrockSecretAccessKey = "AWSBedrockSecretAccessKey";
    internal const string BedrockRegion = "AWSBedrockRegion";
}

/// <summary>
/// The one configuration the live-provider tests build — and therefore the one the
/// <see cref="LiveLlmFactAttribute"/> guard has to read, because a guard that consults different sources
/// admits tests that then fail on a credential it believed present. Every live test resolves its client
/// from <see cref="Instance"/> rather than composing a <see cref="ConfigurationBuilder"/> of its own, so
/// the two cannot drift.
/// </summary>
/// <remarks>
/// <c>appsettings.json</c> is optional rather than required: with the file absent the guard reports the
/// keys it could not find and the test skips, which is a better failure than a
/// <see cref="FileNotFoundException"/> out of the builder. Environment variables are included because
/// that is how CI supplies credentials — and, being part of this one factory, they are now visible to
/// the tests as well as to the guard.
/// </remarks>
internal static class LiveLlmConfiguration
{
    /// <summary>Shared with the backend project, so `dotnet user-secrets` reaches both.</summary>
    private const string UserSecretsId = "99db47a8-e571-40ad-829f-0733c2f6e62b";

    internal static IConfiguration Instance => Lazy.Value;

    private static readonly Lazy<IConfiguration> Lazy = new(() => new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddUserSecrets(UserSecretsId)
        .AddEnvironmentVariables()
        .Build());
}

/// <summary>
/// A <see cref="FactAttribute"/> for tests that call a real model provider (Amazon Bedrock, OpenAI).
/// They need valid credentials and they cost money, so they are opt-in: set <c>RUN_LIVE_LLM_TESTS=1</c>
/// to run them.
/// <para>
/// These tests document provider behaviour this repo had to work around (see the extended-thinking and
/// <c>AdditionalModelRequestFields</c> notes in README.md), which is why they stay in the suite rather
/// than being deleted — but they must not make <c>dotnet test</c> depend on live credentials.
/// </para>
/// <para>
/// Each test names the keys <b>it</b> needs — <c>[LiveLlmFact(LiveLlmKeys.OpenAIApiKey)]</c>, say. Not a
/// shared bundle of every provider's keys: that made an absent AWS credential skip the OpenAI tests,
/// which read none of it, and reported the wrong reason for doing so.
/// </para>
/// </summary>
public sealed class LiveLlmFactAttribute : FactAttribute
{
    internal const string EnabledVariable = "RUN_LIVE_LLM_TESTS";

    /// <param name="requiredKey">
    /// A configuration key this test cannot run without. Positional rather than a bare
    /// <c>params</c> array so that <c>[LiveLlmFact]</c> with no keys at all does not compile — a guard
    /// checking nothing is the failure mode this replaced.
    /// </param>
    /// <param name="alsoRequired">Any further keys the same test needs.</param>
    public LiveLlmFactAttribute(string requiredKey, params string[] alsoRequired)
    {
        Skip = SkipReason(
            optedIn: Environment.GetEnvironmentVariable(EnabledVariable) is { Length: > 0 },
            configuration: LiveLlmConfiguration.Instance,
            requiredKeys: [requiredKey, .. alsoRequired]);
    }

    /// <summary>
    /// The skip decision as a pure function of the opt-in flag and the configuration, so it can be
    /// asserted without an ambient environment. Returns <see langword="null"/> when the test should run.
    /// </summary>
    /// <remarks>
    /// Checking the keys and not only the flag is deliberate: without them the tests fail deep inside a
    /// provider constructor with an opaque <see cref="ArgumentNullException"/> — or, for OpenAI, with the
    /// test's own <see cref="InvalidOperationException"/> — which reads like a broken test rather than
    /// missing credentials.
    /// </remarks>
    internal static string? SkipReason(bool optedIn, IConfiguration configuration, IReadOnlyList<string> requiredKeys)
    {
        if (!optedIn)
        {
            return $"Set {EnabledVariable}=1 (and valid provider credentials) to run this live-provider test.";
        }

        var missing = requiredKeys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        return missing.Length > 0
            ? $"{EnabledVariable} is set but this test's credentials are missing from " +
              $"appsettings.json/user-secrets/environment: {string.Join(", ", missing)}."
            : null;
    }
}
