using Microsoft.Extensions.Configuration;

namespace AgenticTodos.Tests;

/// <summary>
/// The guard that decides whether a live-provider test runs. It is the only thing standing between
/// <c>dotnet test</c> and a paid API call, and — the other direction — the only thing that turns absent
/// credentials into a readable skip instead of an opaque failure inside a provider constructor. Both
/// directions are asserted here against a synthetic configuration, so nothing depends on what happens to
/// be in this machine's user-secrets.
/// </summary>
public class IntegrationFactAttributesTests
{
    [Fact]
    public void NotOptedIn_SkipsWhateverTheCredentialsSay()
    {
        // Credentials present must not be enough: the flag is the cost control.
        var reason = LiveLlmFactAttribute.SkipReason(
            optedIn: false, Config((LiveLlmKeys.OpenAIApiKey, "sk-real")), [LiveLlmKeys.OpenAIApiKey]);

        Assert.Contains(LiveLlmFactAttribute.EnabledVariable, reason);
    }

    [Fact]
    public void OptedInWithItsOwnKeys_Runs()
    {
        Assert.Null(LiveLlmFactAttribute.SkipReason(
            optedIn: true, Config((LiveLlmKeys.OpenAIApiKey, "sk-real")), [LiveLlmKeys.OpenAIApiKey]));
    }

    [Fact]
    public void AnotherProvidersMissingKeys_DoNotSkipThisTest()
    {
        // The bug this replaced: one bundle of all four keys meant an absent AWS credential skipped the
        // OpenAI tests, which read none of it — and told the reader the reason was AWS.
        var openai = Config((LiveLlmKeys.OpenAIApiKey, "sk-real"), (LiveLlmKeys.BedrockRegion, "eu-central-1"));

        Assert.Null(LiveLlmFactAttribute.SkipReason(optedIn: true, openai, [LiveLlmKeys.OpenAIApiKey]));

        var bedrock = LiveLlmFactAttribute.SkipReason(optedIn: true, openai,
            [LiveLlmKeys.BedrockAccessKeyId, LiveLlmKeys.BedrockSecretAccessKey, LiveLlmKeys.BedrockRegion]);

        // And the reason names only what is actually absent — the region came from appsettings.json, and
        // reporting it as missing would send the reader looking in the wrong place.
        Assert.NotNull(bedrock);
        Assert.Contains(LiveLlmKeys.BedrockAccessKeyId, bedrock);
        Assert.Contains(LiveLlmKeys.BedrockSecretAccessKey, bedrock);
        Assert.DoesNotContain(LiveLlmKeys.BedrockRegion, bedrock);
        Assert.DoesNotContain(LiveLlmKeys.OpenAIApiKey, bedrock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void APresentButBlankKey_CountsAsMissing(string? value)
    {
        // A key set to the empty string is how a half-finished `dotnet user-secrets set` looks, and it
        // reaches the provider as a null credential.
        var reason = LiveLlmFactAttribute.SkipReason(
            optedIn: true, Config((LiveLlmKeys.OpenAIApiKey, value)), [LiveLlmKeys.OpenAIApiKey]);

        Assert.Contains(LiveLlmKeys.OpenAIApiKey, reason);
    }

    [Fact]
    public void TheGuardAndTheTests_ReadOneConfiguration()
    {
        // Not a tautology worth much on its own — but LiveLlmConfiguration.Instance being a single object
        // is the whole mechanism by which the guard cannot consult sources the tests do not. If this ever
        // becomes a factory method returning a fresh builder per call, the divergence is back.
        Assert.Same(LiveLlmConfiguration.Instance, LiveLlmConfiguration.Instance);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
