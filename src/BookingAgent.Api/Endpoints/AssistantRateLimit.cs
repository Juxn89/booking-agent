namespace BookingAgent.Api.Endpoints;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using BookingAgent.Application.Common;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>How fast one visitor may write to the assistant, bound from the <c>AssistantRateLimit</c> section.</summary>
public sealed class AssistantRateLimitOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "AssistantRateLimit";

	/// <summary>Messages per minute per visitor: a person types a few; a script would type hundreds, each one paid.</summary>
	[Range(1, 1000)]
	public int PermitsPerMinute { get; set; } = 10;
}

/// <summary>
/// A per-visitor limit on the assistant, the one endpoint that spends money on every request. The visitor is the
/// client address the web app's proxy reports in <c>X-Forwarded-For</c>, trusted only from loopback (the
/// default of <see cref="ForwardedHeadersOptions"/>): a header from anywhere else is ignored.
/// </summary>
internal static class AssistantRateLimit
{
	/// <summary>The policy's name, for the endpoint.</summary>
	public const string Policy = "assistant";

	/// <summary>The error code of a refused message (docs/errors.md).</summary>
	public const string TooManyRequests = "assistant.too_many_requests";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	/// <summary>Registers the limiter and the forwarded-headers settings.</summary>
	/// <param name="services">The services.</param>
	/// <param name="configuration">The configuration.</param>
	/// <returns>The services.</returns>
	public static IServiceCollection AddAssistantRateLimit(this IServiceCollection services, IConfiguration configuration)
	{
		var settings = configuration.GetSection(AssistantRateLimitOptions.SectionName).Get<AssistantRateLimitOptions>() ?? new AssistantRateLimitOptions();
		Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);

		services.Configure<ForwardedHeadersOptions>(static options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor);
		services.AddRateLimiter(options =>
		{
			options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
				context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
				_ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PermitsPerMinute, Window = Window, QueueLimit = 0 }));
			options.OnRejected = static async (context, _) =>
			{
				context.HttpContext.Response.Headers.RetryAfter = ((int)Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
				await Error.TooManyRequests(TooManyRequests, "Too many messages in a short time; wait a minute and write again.")
					.ToProblem()
					.ExecuteAsync(context.HttpContext)
					.ConfigureAwait(false);
			};
		});
		return services;
	}
}
