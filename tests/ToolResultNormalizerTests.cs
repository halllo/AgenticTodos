using System.Text.Json;
using AgenticTodos.Backend;

namespace AgenticTodos.Tests;

public class ToolResultNormalizerTests
{
    // ---------------------------------------------------------------------------
    // Already-normalized inputs — returned unchanged
    // ---------------------------------------------------------------------------

    [Fact]
    public void AlreadyNormalized_SingleTextItem_ReturnedUnchanged()
    {
        const string input = """{"content":[{"type":"text","text":"hello"}]}""";
        Assert.Equal(input, ToolResultNormalizer.Normalize(input));
    }

    [Fact]
    public void AlreadyNormalized_MultipleTypedItems_ReturnedUnchanged()
    {
        const string input = """{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}""";
        Assert.Equal(input, ToolResultNormalizer.Normalize(input));
    }

    [Fact]
    public void AlreadyNormalized_EmptyContentArray_ReturnedUnchanged()
    {
        const string input = """{"content":[]}""";
        Assert.Equal(input, ToolResultNormalizer.Normalize(input));
    }

    // ---------------------------------------------------------------------------
    // Missing "type" discriminator — injected as "text"
    // ---------------------------------------------------------------------------

    [Fact]
    public void MissingTypeDiscriminator_InjectsTextType()
    {
        const string input = """{"content":[{"text":"05.05.2026 10:17:16"}]}""";
        var result = ToolResultNormalizer.Normalize(input);
        AssertSingleTextItem(result, "05.05.2026 10:17:16");
    }

    [Fact]
    public void MixedTypedAndUntyped_OnlyUntypedItemsGetType()
    {
        const string input = """{"content":[{"type":"text","text":"typed"},{"text":"untyped"}]}""";
        var result = ToolResultNormalizer.Normalize(input);
        using var doc = JsonDocument.Parse(result);
        var items = doc.RootElement.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("typed", items[0].GetProperty("text").GetString());
        Assert.Equal("untyped", items[1].GetProperty("text").GetString());
        Assert.All(items, item => Assert.Equal("text", item.GetProperty("type").GetString()));
    }

    // ---------------------------------------------------------------------------
    // Microsoft.Extensions.AI TextContent object: {"text":"...", "annotations":null}
    // ---------------------------------------------------------------------------

    [Fact]
    public void TextContentObject_ExtractsTextProperty()
    {
        const string input = """{"text":"The current time is 10:17","annotations":null,"additionalProperties":null}""";
        AssertSingleTextItem(ToolResultNormalizer.Normalize(input), "The current time is 10:17");
    }

    [Fact]
    public void TextContentObject_EmptyText_ProducesEmptyTextItem()
    {
        const string input = """{"text":"","annotations":null}""";
        AssertSingleTextItem(ToolResultNormalizer.Normalize(input), "");
    }

    // ---------------------------------------------------------------------------
    // JSON string (result of JsonSerializer.Serialize(someString))
    // ---------------------------------------------------------------------------

    [Fact]
    public void JsonString_WrapsAsTextItem()
    {
        // JsonSerializer.Serialize("hello") → "\"hello\""
        const string input = "\"hello world\"";
        AssertSingleTextItem(ToolResultNormalizer.Normalize(input), "hello world");
    }

    [Fact]
    public void JsonString_WithSpecialChars_EscapedCorrectly()
    {
        var text = "line1\nline2\ttab\"quote";
        var jsonString = JsonSerializer.Serialize(text); // "\"line1\\nline2\\ttab\\\"quote\""
        var result = ToolResultNormalizer.Normalize(jsonString);
        using var doc = JsonDocument.Parse(result);
        var extracted = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal(text, extracted);
    }

    // ---------------------------------------------------------------------------
    // Non-JSON / plain strings — wrapped safely
    // ---------------------------------------------------------------------------

    [Fact]
    public void PlainString_NotJson_WrappedAsText()
    {
        const string input = "this is plain text";
        AssertSingleTextItem(ToolResultNormalizer.Normalize(input), "this is plain text");
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmptyContentArray()
    {
        Assert.Equal("""{"content":[]}""", ToolResultNormalizer.Normalize(""));
        Assert.Equal("""{"content":[]}""", ToolResultNormalizer.Normalize(null!));
    }

    // ---------------------------------------------------------------------------
    // Other JSON shapes — wrapped safely
    // ---------------------------------------------------------------------------

    [Fact]
    public void JsonNumber_WrappedAsText()
    {
        var result = ToolResultNormalizer.Normalize("42");
        AssertSingleTextItem(result, "42");
    }

    [Fact]
    public void JsonBool_WrappedAsText()
    {
        var result = ToolResultNormalizer.Normalize("true");
        AssertSingleTextItem(result, "true");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static void AssertSingleTextItem(string json, string expectedText)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var content = root.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var items = content.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("text", items[0].GetProperty("type").GetString());
        Assert.Equal(expectedText, items[0].GetProperty("text").GetString());
    }
}
