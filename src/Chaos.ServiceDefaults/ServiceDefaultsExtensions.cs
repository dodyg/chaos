using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Chaos.ServiceDefaults;

/// <summary>
/// Defaults every Chaos service shares (PLAN2 §8.7): telemetry exported over OTLP when Aspire (or the
/// host) provides an endpoint, a liveness check, and the two health endpoints. The endpoints are
/// mapped in every environment because the container host probes them in production too; they
/// return only a status word, never data.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Tag for checks that only prove the process is running (<c>/health/live</c>).</summary>
    public const string LiveTag = "live";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Logs carry request IDs, account IDs, kinds and counts only (PLAN2 §8.5). Formatted
        // messages are exported as written; scopes are left out so request state never leaks.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(LivePath, StringComparison.Ordinal) &&
                        !context.Request.Path.StartsWithSegments(ReadyPath, StringComparison.Ordinal))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(LiveTag) });
        app.MapHealthChecks(ReadyPath);
        return app;
    }
}
