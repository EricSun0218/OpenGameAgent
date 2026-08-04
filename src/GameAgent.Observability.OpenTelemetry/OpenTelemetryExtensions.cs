using GameAgent.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GameAgent.Observability.OpenTelemetry;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddGameAgentOpenTelemetryMetrics(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddSingleton<OpenTelemetryRuntimeMetricsSink>();
        services.TryAddSingleton<IRuntimeMetricsSink>(
            static provider => provider.GetRequiredService<OpenTelemetryRuntimeMetricsSink>());
        return services;
    }

    public static OpenTelemetryBuilder AddGameAgentRuntimeInstrumentation(this OpenTelemetryBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.WithMetrics(static metrics => metrics.AddMeter(OpenTelemetryRuntimeMetricsSink.MeterName));
        builder.WithTracing(static tracing => tracing.AddSource(GameAgentTelemetry.ActivitySourceName));
        return builder;
    }
}
