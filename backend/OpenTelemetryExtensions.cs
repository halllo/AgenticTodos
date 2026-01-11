using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgenticTodos.Backend;

public static class OpenTelemetryExtensions
{
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    ;
            })
            .WithTracing(tracing =>
            {
                string[] excludedRequestPaths = ["/health", "/alive"];
                tracing
                    .AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                    {
                        tracing.Filter = context =>
                            !excludedRequestPaths.Any(excludedPath =>
                                context.Request.Path.StartsWithSegments(excludedPath));
                    })
                    .AddHttpClientInstrumentation()
                    //.AddGrpcClientInstrumentation()
                    ;
            });

        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}