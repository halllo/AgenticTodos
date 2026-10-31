using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// A2UI in MCP Apps (https://a2ui.org/guides/a2ui-in-mcp-apps/): tools return A2UI v0.9 messages as an
// embedded `application/a2ui+json` resource, and the app at ui://a2ui/app.html renders them.
public class A2uiApp()
{
    const string URI = "ui://a2ui/app.html";
    const string A2UI_MIME_TYPE = "application/a2ui+json";
    const string VERSION = "v0.9";
    const string BASIC_CATALOG = "https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json";
    static readonly string[] MessageKinds = ["createSurface", "updateComponents", "updateDataModel", "deleteSurface"];

    static int counter;

    [McpServerTool, Description("""
        Renders an interactive UI in the chat from A2UI v0.9 messages. Use it whenever a form, card, list,
        picker or dashboard shows something better than text.

        `messages` is an ordered array; each item has exactly one of these keys (`"version":"v0.9"` is optional):
        1. {"createSurface":{"surfaceId":"s1","catalogId":"https://a2ui.org/specification/v0_9/catalogs/basic/catalog.json"}} - always first.
        2. {"updateComponents":{"surfaceId":"s1","components":[...]}} - a flat list; exactly one has "id":"root".
           Containers reference children by id, never inline.
        3. {"updateDataModel":{"surfaceId":"s1","path":"/","value":<object>}} - optional initial data.

        Component: {"id":"...","component":"<Name>", ...props}. Basic catalog:
        - Layout: Column/Row {"children":["id",...],"justify"?,"align"?}, Card {"child":"id"}, List {"children":[...]},
          Tabs {"tabs":[{"title":"..","child":"id"}]}, Divider {}.
        - Content: Text {"text":..,"variant"?:"h1".."h5"|"body"|"caption"} (simple markdown ok), Image {"url":..},
          Icon {"name":"check"|"add"|"delete"|"edit"|"info"|"event"|...}.
        - Input: TextField {"label":..,"value":{"path":"/x"}}, CheckBox {"label":..,"value":{"path":"/b"}},
          Slider {"label":..,"min":0,"max":10,"value":{"path":"/n"}}, DateTimeInput {"value":{"path":"/d"},"enableDate":true},
          ChoicePicker {"label":..,"options":[{"label":"A","value":"a"}],"value":{"path":"/c"}},
          Button {"child":"<Text id>","variant"?:"primary","action":{"event":{"name":"submit","context":{"name":{"path":"/x"}}}}}.
        Dynamic values are a literal or {"path":"/json/pointer"} into the data model.

        When the user presses a button, you receive a user message starting with "[A2UI action]" naming the
        event and its resolved context. Respond to it, e.g. by calling this tool again with a new surface.
        """)]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]
    public CallToolResult RenderA2ui(
        [Description("A2UI v0.9 messages: createSurface, then updateComponents, then optionally updateDataModel.")]
        JsonElement[] messages)
    {
        var normalized = new JsonArray();
        foreach (var message in messages)
        {
            if (JsonNode.Parse(message.GetRawText()) is not JsonObject obj)
                throw new McpException("Every A2UI message must be a JSON object.");
            var kinds = MessageKinds.Where(obj.ContainsKey).ToArray();
            if (kinds.Length != 1)
                throw new McpException(
                    $"Every A2UI message needs exactly one of {string.Join(", ", MessageKinds)}; got: {string.Join(", ", obj.Select(p => p.Key))}.");
            obj["version"] = VERSION;
            if (kinds[0] == "createSurface" && obj["createSurface"] is JsonObject create)
                create["catalogId"] ??= BASIC_CATALOG;
            normalized.Add(obj);
        }

        var surfaces = normalized
            .Select(m => m!.AsObject().First(p => p.Key != "version").Value?["surfaceId"]?.GetValue<string>())
            .Where(id => id is not null)
            .Distinct();
        return A2uiResult($"Rendered A2UI surface(s): {string.Join(", ", surfaces)}.", normalized);
    }

    [McpServerTool, Description("Shows a small A2UI counter widget whose button increments a counter on the MCP server.")]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]
    public CallToolResult ShowA2uiCounter() =>
        A2uiResult($"Counter is at {counter}.", JsonNode.Parse($$$"""
            [
              {"version":"{{{VERSION}}}","createSurface":{"surfaceId":"counter","catalogId":"{{{BASIC_CATALOG}}}"}},
              {"version":"{{{VERSION}}}","updateComponents":{"surfaceId":"counter","components":[
                {"id":"root","component":"Card","child":"body"},
                {"id":"body","component":"Column","children":["title","value","increase"],"align":"center"},
                {"id":"title","component":"Text","text":"Server counter","variant":"h3"},
                {"id":"value","component":"Text","text":{"path":"/counter"},"variant":"h1"},
                {"id":"increase","component":"Button","variant":"primary","child":"increase-label",
                 "action":{"event":{"name":"increase_counter"}} },
                {"id":"increase-label","component":"Text","text":"Increase"}
              ]}},
              {"version":"{{{VERSION}}}","updateDataModel":{"surfaceId":"counter","path":"/","value":{"counter":{{{counter}}}} }}
            ]
            """)!.AsArray());

    // App-only (visibility "app"): called by the A2UI app when the counter's button is pressed, never by the model.
    [McpServerTool, Description("Increments the A2UI counter and returns the data model update.")]
    [McpMeta("ui", JsonValue = """{"visibility":["app"]}""")]
    public CallToolResult IncreaseCounter()
    {
        var value = Interlocked.Increment(ref counter);
        return A2uiResult($"Counter is at {value}.", new JsonArray(new JsonObject
        {
            ["version"] = VERSION,
            ["updateDataModel"] = new JsonObject
            {
                ["surfaceId"] = "counter",
                ["path"] = "/counter",
                ["value"] = value,
            },
        }));
    }

    [McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
    public async Task<string> A2uiUIResource() =>
        await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "a2ui-app", "dist", "a2ui-app.html"));

    static CallToolResult A2uiResult(string text, JsonArray messages) => new()
    {
        Content =
        [
            new TextContentBlock { Text = text },
            new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri = "a2ui://surface",
                    MimeType = A2UI_MIME_TYPE,
                    Text = messages.ToJsonString(),
                    // Actions the app routes to this server via tools/call; all others go to the agent.
                    Meta = new JsonObject { ["a2ui"] = new JsonObject { ["serverActions"] = new JsonArray("increase_counter") } },
                },
            },
        ],
    };
}
