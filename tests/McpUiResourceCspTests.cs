using AgenticTodos.Backend;

namespace AgenticTodos.Tests;

/// <summary>
/// The <c>Content-Security-Policy</c> the MCP-Apps sandbox iframe is served with is built from request
/// input: <c>Program.cs</c> reads <c>ctx.Request.Query["csp"]</c> on <c>/sandbox.html</c>, deserializes
/// it and joins the domains straight into the header. Every domain string is therefore attacker-supplied,
/// and <c>SanitizeCspDomains</c> is the only thing between it and the response header.
/// <para>
/// Two failure modes are in scope, and the whole path is exercised as <c>json.ToMcpUiResourceCsp()
/// .BuildHeader()</c> — exactly what the middleware does. <b>Header injection:</b> a <c>;</c> ends a
/// directive and CR/LF ends the header, so either would let a caller rewrite the policy that is
/// sandboxing them (or append headers of their own). <b>Fail-closed:</b> input the app cannot understand
/// must produce the restrictive default, never an absent or permissive policy.
/// </para>
/// </summary>
public class McpUiResourceCspTests
{
    /// <summary>What every unusable input has to fall back to. Pinned verbatim: it is a security default,
    /// and a maintainer widening it should have to change a test that says so.</summary>
    private const string SafeDefault =
        "default-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob: data:; " +
        "style-src 'self' 'unsafe-inline' blob: data:; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data: blob:; " +
        "media-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "worker-src 'self' blob:; " +
        "frame-src 'none'; " +
        "object-src 'none'; " +
        "base-uri 'none'";

    // ---------------------------------------------------------------------------
    // Header injection
    // ---------------------------------------------------------------------------

    [Theory]
    // `;` would close the directive and let the rest be read as new directives.
    [InlineData("evil.example;script-src")]
    [InlineData(";")]
    // CR/LF would close the header itself — the classic response-splitting primitive.
    [InlineData("evil.example\r\nX-Injected: 1")]
    [InlineData("evil.example\r")]
    [InlineData("evil.example\n")]
    // A space would smuggle a second source in as one entry; quotes would forge a keyword source such
    // as 'unsafe-eval'. (Which also means a caller cannot legitimately pass 'self' or 'none' — the
    // sanitizer takes hostnames only, and the keyword sources are the app's to decide.)
    [InlineData("evil.example other.example")]
    [InlineData("'unsafe-eval'")]
    [InlineData("\"evil.example\"")]
    // A JSON `null` array element — `{"resourceDomains":[null,"https://cdn.example.com"]}` deserializes
    // to a string[] with a null in it, and the sanitizer's null check is the only thing standing between
    // that and an ArgumentNullException from Enumerable.Any inside BuildHeader. There is no try/catch
    // around the /sandbox.html app.Use that calls it, so the throw means a 500 with no CSP header at all
    // on the page that hosts third-party MCP UI. Reachable data, not hypothetical: the ?csp= payload is
    // JSON.stringify(uiMeta?.csp) straight off the MCP resource's _meta.ui.csp.
    [InlineData(null)]
    // An empty entry, which the guard also drops. Note this case alone would pass even without the
    // IsNullOrEmpty guard when the entry is last in the array — an empty string joins in as nothing and
    // the directive's .TrimEnd() removes the stray trailing space. It only pins the guard because the
    // entry is placed *before* the legitimate domain below too, where it leaves a double space instead.
    [InlineData("")]
    public void AHostileDomain_IsDroppedFromEveryDirective(string? domain)
    {
        // The hostile entry is placed on both sides of the legitimate one: an entry that is dropped
        // cannot leave a trace in either position, whereas one that is merely trimmed at the end of the
        // joined list survives in front of it.
        var json = $$"""
            {
              "resourceDomains": [{{Quote(domain)}}, "https://cdn.example.com", {{Quote(domain)}}],
              "connectDomains": [{{Quote(domain)}}, "https://api.example.com", {{Quote(domain)}}]
            }
            """;

        var header = json.ToMcpUiResourceCsp().BuildHeader();

        // Nothing of the hostile entry reaches the header — not the payload after the separator, and
        // not the part before it either, since the whole entry is dropped rather than trimmed.
        Assert.DoesNotContain("evil.example", header);
        Assert.DoesNotContain("X-Injected", header);
        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);

        // No run of two spaces anywhere: that is the fingerprint of an entry that made it into a source
        // list as the empty string instead of being dropped.
        Assert.DoesNotContain("  ", header);

        // Exactly one `;`-separated directive per directive the builder emits: 11, unchanged. Counting
        // them is what catches a smuggled directive that happens not to use the words above.
        Assert.Equal(11, header.Split("; ").Length);

        // And the legitimate neighbour in the same array is untouched — the sanitizer drops entries, not
        // whole directives, so one hostile domain must not disarm the policy the caller wanted.
        Assert.Contains("script-src 'self' 'unsafe-inline' 'unsafe-eval' blob: data: https://cdn.example.com;", header);
        Assert.Contains("connect-src 'self' https://api.example.com;", header);
    }

    [Fact]
    public void AllDomainsHostile_LeavesTheDirectiveWithNoSourceAtAll()
    {
        // frame-src and base-uri are chosen by `Length > 0` *before* sanitizing, so an array of nothing
        // but hostile entries yields a directive with an empty source list rather than 'none'. That is
        // still fail-closed — an empty source list allows nothing — and it is what the code does, so it
        // is pinned rather than assumed to say 'none'.
        var header = """
            {"frameDomains":["evil.example;x"],"baseUriDomains":["evil.example;x"]}
            """.ToMcpUiResourceCsp().BuildHeader();

        Assert.DoesNotContain("evil.example", header);
        Assert.Contains("; frame-src ; ", header);
        Assert.EndsWith("; base-uri ", header);
    }

    // ---------------------------------------------------------------------------
    // Fail-closed
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"resourceDomains\":")]
    // Valid JSON of the wrong shape: Deserialize throws, and the catch turns it into null.
    [InlineData("[]")]
    [InlineData("42")]
    // Valid JSON that deserializes *to* null — no exception, same outcome.
    [InlineData("null")]
    public void UnusableInput_BecomesNull_AndYieldsTheSafeDefault(string json)
    {
        // The parse failure must not escape either: it happens inside the response pipeline for
        // /sandbox.html, where an exception would mean no CSP header on a page that hosts third-party
        // MCP UI, or no page at all.
        Assert.Null(json.ToMcpUiResourceCsp());
        Assert.Equal(SafeDefault, json.ToMcpUiResourceCsp().BuildHeader());
    }

    [Fact]
    public void NoCspQueryParameterAtAll_YieldsTheSafeDefault()
    {
        // Program.cs writes `Query["csp"].FirstOrDefault()?.ToMcpUiResourceCsp()`, so a request without
        // the parameter reaches BuildHeader as a null McpUiResourceCsp — the extension has to answer it.
        Assert.Equal(SafeDefault, ((McpUiResourceCsp?)null).BuildHeader());
    }

    [Fact]
    public void AnEmptyPolicyObject_YieldsTheSafeDefault()
    {
        // Parses fine, every array null. The 'none' arms are what makes the absence restrictive.
        Assert.Equal(SafeDefault, "{}".ToMcpUiResourceCsp().BuildHeader());
    }

    // ---------------------------------------------------------------------------
    // The legitimate case still works — a guard that dropped everything would pass the tests above
    // ---------------------------------------------------------------------------

    [Fact]
    public void LegitimateDomains_ReachTheDirectivesTheyBelongTo()
    {
        var header = """
            {
              "resourceDomains": ["https://cdn.example.com", "https://assets.example.com"],
              "connectDomains": ["https://api.example.com"],
              "frameDomains": ["https://embed.example.com"],
              "baseUriDomains": ["https://base.example.com"]
            }
            """.ToMcpUiResourceCsp().BuildHeader();

        // resourceDomains feeds the six fetch directives that load the MCP app's own assets…
        foreach (var directive in (string[])["script-src", "style-src", "img-src", "font-src", "media-src", "worker-src"])
        {
            var value = Assert.Single(header.Split("; "), d => d.StartsWith(directive + " "));
            Assert.Contains("https://cdn.example.com https://assets.example.com", value);
        }

        // …and the other three arrays feed exactly one directive each, kept apart on purpose: a domain
        // the app may talk to is not a domain it may be framed by or rebase its URLs onto.
        Assert.Contains("connect-src 'self' https://api.example.com;", header);
        Assert.Contains("frame-src https://embed.example.com;", header);
        Assert.EndsWith("base-uri https://base.example.com", header);
        Assert.DoesNotContain("https://api.example.com;", header.Split("; ").First(d => d.StartsWith("script-src")));
    }

    [Fact]
    public void PropertyNames_AreMatchedCaseInsensitively()
    {
        // The reader is configured PropertyNameCaseInsensitive, so the frontend's casing cannot silently
        // produce an all-null policy that looks like a deliberate lockdown.
        var csp = """{"RESOURCEDOMAINS":["https://cdn.example.com"]}""".ToMcpUiResourceCsp();

        Assert.Equal(["https://cdn.example.com"], csp!.ResourceDomains!);
    }

    /// <summary>JSON-encodes one domain so a CR or a quote survives into the test's input verbatim — and
    /// so a null becomes a literal JSON <c>null</c> array element rather than an empty string.</summary>
    private static string Quote(string? value) => System.Text.Json.JsonSerializer.Serialize(value);
}
