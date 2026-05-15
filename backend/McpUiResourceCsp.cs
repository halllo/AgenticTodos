public record McpUiResourceCsp(
    [property: System.Text.Json.Serialization.JsonPropertyName("resourceDomains")] string[]? ResourceDomains,
    [property: System.Text.Json.Serialization.JsonPropertyName("connectDomains")] string[]? ConnectDomains,
    [property: System.Text.Json.Serialization.JsonPropertyName("frameDomains")] string[]? FrameDomains,
    [property: System.Text.Json.Serialization.JsonPropertyName("baseUriDomains")] string[]? BaseUriDomains
);

public static class McpUiResourceCspExtensions
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static McpUiResourceCsp? ToMcpUiResourceCsp(this string json)
    {
        if (json is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<McpUiResourceCsp>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string BuildHeader(this McpUiResourceCsp? csp)
    {
        var rd = string.Join(" ", SanitizeCspDomains(csp?.ResourceDomains));
        var cd = string.Join(" ", SanitizeCspDomains(csp?.ConnectDomains));
        var frame = csp?.FrameDomains?.Length > 0
            ? $"frame-src {string.Join(" ", SanitizeCspDomains(csp.FrameDomains))}"
            : "frame-src 'none'";
        var baseUri = csp?.BaseUriDomains?.Length > 0
            ? $"base-uri {string.Join(" ", SanitizeCspDomains(csp.BaseUriDomains))}"
            : "base-uri 'none'";

        return string.Join("; ", new[]
        {
            "default-src 'self' 'unsafe-inline'",
            $"script-src 'self' 'unsafe-inline' 'unsafe-eval' blob: data: {rd}".TrimEnd(),
            $"style-src 'self' 'unsafe-inline' blob: data: {rd}".TrimEnd(),
            $"img-src 'self' data: blob: {rd}".TrimEnd(),
            $"font-src 'self' data: blob: {rd}".TrimEnd(),
            $"media-src 'self' data: blob: {rd}".TrimEnd(),
            $"connect-src 'self' {cd}".TrimEnd(),
            $"worker-src 'self' blob: {rd}".TrimEnd(),
            frame,
            "object-src 'none'",
            baseUri,
        });
    }

    static string[] SanitizeCspDomains(string[]? domains) =>
        domains?
            .Where(d => !string.IsNullOrEmpty(d)
                && !d.Any(c => c is ';' or '\r' or '\n' or '\'' or '"' or ' '))
            .ToArray() ?? [];
};
