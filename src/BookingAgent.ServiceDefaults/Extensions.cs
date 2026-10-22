namespace Microsoft.Extensions.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

/// <summary>
/// Aspire's service defaults: OpenTelemetry, health checks, service discovery and HTTP resilience. Kept in the
/// namespace the Aspire template uses, so <c>AddServiceDefaults()</c> resolves without a using.
/// </summary>
public static class Extensions
{
	private const string HealthEndpointPath = "/health";
	private const string AlivenessEndpointPath = "/alive";
	private const string LiveTag = "live";

	// Npgsql's own ActivitySource. Without it a trace stops at the endpoint and never shows the queries.
	private const string NpgsqlActivitySource = "Npgsql";

	/// <summary>Adds the defaults.</summary>
	/// <typeparam name="TBuilder">The host builder.</typeparam>
	/// <param name="builder">The builder.</param>
	/// <returns>The builder.</returns>
	public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		builder.ConfigureOpenTelemetry();
		builder.AddDefaultHealthChecks();
		builder.Services.AddServiceDiscovery();
		builder.Services.ConfigureHttpClientDefaults(http =>
		{
			http.AddStandardResilienceHandler();
			http.AddServiceDiscovery();
		});

		return builder;
	}

	/// <summary>Logs, metrics and traces, exported over OTLP when the AppHost provides an endpoint.</summary>
	/// <typeparam name="TBuilder">The host builder.</typeparam>
	/// <param name="builder">The builder.</param>
	/// <returns>The builder.</returns>
	public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		builder.Logging.AddOpenTelemetry(logging =>
		{
			logging.IncludeFormattedMessage = true;
			logging.IncludeScopes = true;
		});

		builder.Services.AddOpenTelemetry()
			.WithMetrics(metrics => metrics
				.AddAspNetCoreInstrumentation()
				.AddHttpClientInstrumentation()
				.AddRuntimeInstrumentation())
			.WithTracing(tracing => tracing
				.AddSource(builder.Environment.ApplicationName)
				.AddSource(NpgsqlActivitySource)
				.AddAspNetCoreInstrumentation(options =>
					options.Filter = context =>
						!context.Request.Path.StartsWithSegments(HealthEndpointPath)
						&& !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
				.AddHttpClientInstrumentation());

		if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
		{
			builder.Services.AddOpenTelemetry().UseOtlpExporter();
		}

		return builder;
	}

	/// <summary>A liveness check that only proves the process answers.</summary>
	/// <typeparam name="TBuilder">The host builder.</typeparam>
	/// <param name="builder">The builder.</param>
	/// <returns>The builder.</returns>
	public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
		where TBuilder : IHostApplicationBuilder
	{
		builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);
		return builder;
	}

	/// <summary>
	/// Health endpoints, in Development only: they reveal what the service depends on
	/// (https://aka.ms/aspire/healthchecks).
	/// </summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static WebApplication MapDefaultEndpoints(this WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);
		if (app.Environment.IsDevelopment())
		{
			app.MapHealthChecks(HealthEndpointPath);
			app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains(LiveTag) });
		}

		return app;
	}
}
