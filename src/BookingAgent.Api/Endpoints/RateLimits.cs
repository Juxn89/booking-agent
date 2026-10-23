namespace BookingAgent.Api.Endpoints;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using BookingAgent.Application.Common;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>How fast one visitor may call the costly or guessable endpoints, bound from the <c>RateLimits</c> section.</summary>
public sealed class RateLimitOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "RateLimits";

	/// <summary>Messages to the assistant per minute: a person types a few; a script would type hundreds, each one paid.</summary>
	[Range(1, 1000)]
	public int AssistantPerMinute { get; set; } = 10;

	/// <summary>Code requests and code checks per minute: a person needs two or three; guessing codes needs thousands.</summary>
	[Range(1, 1000)]
	public int SignInPerMinute { get; set; } = 5;
}

/// <summary>
/// Per-visitor limits on the endpoints that spend money (the assistant) or that a guesser would hammer (sign-in). The
/// visitor is the client address the web app's proxy reports in <c>X-Forwarded-For</c>, trusted only from loopback
/// (the default of <see cref="ForwardedHeadersOptions"/>): a header from anywhere else is ignored.
/// </summary>
internal static class RateLimits
{
	/// <summary>The assistant's policy.</summary>
	public const string Assistant = "assistant";

	/// <summary>The sign-in policy.</summary>
	public const string SignIn = "sign-in";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	// The error code of a refused request, per policy (docs/errors.md).
	private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
	{
		[Assistant] = "assistant.too_many_requests",
		[SignIn] = "signin.too_many_requests",
	};

	/// <summary>Registers the limiter and the forwarded-headers settings.</summary>
	/// <param name="services">The services.</param>
	/// <param name="configuration">The configuration.</param>
	/// <returns>The services.</returns>
	public static IServiceCollection AddRateLimits(this IServiceCollection services, IConfiguration configuration)
	{
		var settings = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
		Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);

		services.Configure<ForwardedHeadersOptions>(static options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor);
		services.AddRateLimiter(options =>
		{
			options.AddPolicy(Assistant, context => PerVisitor(context, settings.AssistantPerMinute));
			options.AddPolicy(SignIn, context => PerVisitor(context, settings.SignInPerMinute));
			options.OnRejected = static async (context, _) =>
			{
				var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? Assistant;
				context.HttpContext.Response.Headers.RetryAfter = ((int)Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);
				await Error.TooManyRequests(Codes.GetValueOrDefault(policy, Codes[Assistant]), "Too many requests in a short time; wait a minute and try again.")
					.ToProblem()
					.ExecuteAsync(context.HttpContext)
					.ConfigureAwait(false);
			};
		});
		return services;
	}

	private static RateLimitPartition<string> PerVisitor(HttpContext context, int perMinute) =>
		RateLimitPartition.GetFixedWindowLimiter(
			context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			_ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = Window, QueueLimit = 0 });
}
