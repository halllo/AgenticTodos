# AgenticTodos

This experimental application aims to explore the following technologies:

- Microsoft Agent Framework
- AG-UI
- WebMCP

## Problems

### AmazonBedrockRuntimeClient does not support AdditionalProperties

Amazon Bedrock Runtime client throws this exception, when used with AG-UI:

```log
Amazon.BedrockRuntime.Model.ValidationException: The model returned the following errors: ag_ui_thread_id: Extra inputs are not permitted
---> Amazon.Runtime.Internal.HttpErrorResponseException: Exception of type 'Amazon.Runtime.Internal.HttpErrorResponseException' was thrown.
at Amazon.Runtime.HttpWebRequestMessage.ProcessHttpResponseMessage(HttpResponseMessage responseMessage)
```

This is reproduced by [AgenticTodos.Tests.AmazonBedrockTest.WithAdditionalModelRequestFields()](./tests/AmazonBedrockTest.cs):

```csharp
var response = await client.GetResponseAsync(
    messages:
    [
        new ChatMessage(ChatRole.User, "Hello. How are you?"),
    ],
    options: new()
    {
        Temperature = 0.0F,
        Tools = [],
        AdditionalProperties = new()
        {
            { "ag_ui_thread_id", "thread_ba818347681144109377b1c044e4f4f6" },
        },
    });
```

We can probably circumvent that by removing these `AdditionalProperties` and reapply them to the reponse.

### AG-UI Client does not support Angular

AG-UI is very well supported by Copilot Kit, but that requires next.js. There is currently no functional Angular support.

We can probably circumvent that by using `@ag-ui/client @ag-ui/core` (⚠️ 4 high severity vulnerabilities) and glue it together.

See [Quickstart - Build clients](https://docs.ag-ui.com/quickstart/clients).
