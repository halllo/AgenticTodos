# AgenticTodos

This experimental application aims to explore the following technologies:

- Microsoft Agent Framework
- AG-UI
- WebMCP

## Problems

Amazon Bedrock Runtime client throws this exception, when used with AG-UI:

```log
Amazon.BedrockRuntime.Model.ValidationException: The model returned the following errors: ag_ui_thread_id: Extra inputs are not permitted
---> Amazon.Runtime.Internal.HttpErrorResponseException: Exception of type 'Amazon.Runtime.Internal.HttpErrorResponseException' was thrown.
at Amazon.Runtime.HttpWebRequestMessage.ProcessHttpResponseMessage(HttpResponseMessage responseMessage)
```
