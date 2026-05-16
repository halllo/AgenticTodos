#pragma warning disable ASPIREJAVASCRIPT001
#pragma warning disable ASPIREAWSPUBLISHERS001

var builder = DistributedApplication.CreateBuilder(args);

//builder.AddAzureContainerAppEnvironment("agentic-todos");

builder.AddAWSCDKEnvironment(
    name: "agentic-todos",
    cdkDefaultsProviderFactory: Aspire.Hosting.AWS.Deployment.CDKDefaultsProviderFactory.Preview_V1,
    environmentResourceConfig: new Aspire.Hosting.AWS.Deployment.AWSCDKEnvironmentResourceConfig
    {
        AWSSDKConfig = builder.AddAWSSDKConfig().WithRegion(Amazon.RegionEndpoint.EUCentral1)
    });

var mcpserver = builder.AddProject<Projects.AgenticTodos_McpServer>("AgenticTodos-McpServer")
    .WithExternalHttpEndpoints()
    .PublishAsECSFargateServiceWithALB() //until https://github.com/aws/integrations-on-dotnet-aspire-for-aws/pull/200
    ;

var backend = builder.AddProject<Projects.AgenticTodos_Backend>("AgenticTodos-Backend")
    .WithReference(mcpserver)
    .WithExternalHttpEndpoints()
    .PublishAsECSFargateServiceWithALB() //until https://github.com/aws/integrations-on-dotnet-aspire-for-aws/pull/200
    ;

var element = builder.AddViteApp("AgenticTodos-Frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 3000;
    })
    .WithReference(backend)
    .WithExternalHttpEndpoints()
    .PublishAsStaticWebsite("/agents", backend, options =>
    {
        options.OutputPath = "dist/agentic-todos/browser";
    })
    .WithCloudFrontBackendBehavior("/agents/*", backend)
    .WithCloudFrontBackendBehavior("/mcp/*", mcpserver)
    .PublishAsS3WithCloudFront(config =>
    {
        config.OutputPath = "dist/agentic-todos/browser";
    })
    ;

builder.Build().Run();
