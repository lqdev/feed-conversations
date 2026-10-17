using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FeedConversations.ServiceDefaults;

public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter("FeedConversations"))
            .WithTracing(traces => traces.AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = context => context.Request.Path.StartsWithSegments("/health");
            }));
        // Remote client URLs/content are deliberately excluded from telemetry.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddOtlpExporter())
                .WithTracing(t => t.AddOtlpExporter());
        }
        return builder;
    }

    public static void MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapGet("/alive", () => new { status = "alive" });
    }
}
