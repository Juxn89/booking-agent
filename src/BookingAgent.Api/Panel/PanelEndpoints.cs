namespace BookingAgent.Api.Panel;

using System.Security.Cryptography;
using System.Text;
using BookingAgent.Api.Endpoints;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Panel;
using BookingAgent.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using NodaTime.Text;

/// <summary>The business's panel, bound from the <c>Panel</c> section.</summary>
public sealed class PanelOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Panel";

	/// <summary>
	/// The staff's access key, from the environment (the AppHost reads it from the git-ignored .env). Without one the
	/// panel stays closed: an unset key never means an open door.
	/// </summary>
	public string? AccessKey { get; set; }

	/// <summary>How long a panel session lasts: a working day.</summary>
	public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>The panel error codes (docs/errors.md).</summary>
internal static class PanelAccessErrors
{
	public const string NotConfigured = "panel.not_configured";

	public const string InvalidKey = "panel.invalid_key";

	public const string Required = "panel.required";
}

/// <summary>
/// Panel sessions: the business id and an expiry, sealed with ASP.NET Data Protection into an HttpOnly,
/// SameSite=Strict cookie. Nothing is stored; tampering or expiry makes it unreadable.
/// </summary>
/// <param name="protection">Data protection.</param>
internal sealed class PanelSessions(IDataProtectionProvider protection)
{
	public const string Cookie = "panel_session";

	private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("BookingAgent.Panel.Session").ToTimeLimitedDataProtector();

	public string Issue(Guid businessId, TimeSpan lifetime) => _protector.Protect(businessId.ToString("N"), lifetime);

	/// <summary>Whether the request carries a live panel session for the business.</summary>
	public bool IsSignedIn(HttpContext http, Guid businessId)
	{
		if (http.Request.Cookies[Cookie] is not { Length: > 0 } token)
		{
			return false;
		}

		try
		{
			return Guid.TryParseExact(_protector.Unprotect(token), "N", out var signedInto) && signedInto == businessId;
		}
		catch (CryptographicException)
		{
			return false;
		}
	}
}

/// <summary>
/// The panel's routes: sign in with the access key, the calendar a week at a time, the steps behind a booking the
/// assistant made, and cancelling for the business. Changes arrive live on <see cref="CalendarHub"/>.
/// </summary>
internal static class PanelEndpoints
{
	private const int DefaultDays = 7;

	public static IEndpointRouteBuilder MapPanelEndpoints(this IEndpointRouteBuilder app)
	{
		var panel = app.MapGroup("/v1/businesses/{businessId:guid}/panel").WithTags("Panel");
		panel.MapPost("/sign-in", SignIn).RequireRateLimiting(RateLimits.SignIn);
		panel.MapPost("/sign-out", SignOut);
		panel.MapGet("/calendar", CalendarAsync);
		panel.MapGet("/bookings/{id:guid}/trace", TraceAsync);
		panel.MapPost("/bookings/{id:guid}/cancel", CancelAsync);
		app.MapHub<CalendarHub>(CalendarHub.Path);
		return app;
	}

	private static IResult SignIn(Guid businessId, KeyRequest request, PanelSessions sessions, Microsoft.Extensions.Options.IOptions<PanelOptions> options, HttpContext http)
	{
		var settings = options.Value;
		if (string.IsNullOrEmpty(settings.AccessKey))
		{
			return Error.Unavailable(PanelAccessErrors.NotConfigured, "The panel has no access key configured (Panel:AccessKey).").ToProblem();
		}

		// Constant time over hashes: neither the key's length nor how much of it matched leaks.
		var typed = SHA256.HashData(Encoding.UTF8.GetBytes(request.Key ?? string.Empty));
		var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AccessKey));
		if (!CryptographicOperations.FixedTimeEquals(typed, expected))
		{
			return Error.Unauthorized(PanelAccessErrors.InvalidKey, "That is not the panel's key.").ToProblem();
		}

		http.Response.Cookies.Append(PanelSessions.Cookie, sessions.Issue(businessId, settings.SessionLifetime), new CookieOptions
		{
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Secure = http.Request.IsHttps,
			Path = "/",
			MaxAge = settings.SessionLifetime,
		});
		return Results.NoContent();
	}

	private static IResult SignOut(HttpContext http)
	{
		http.Response.Cookies.Delete(PanelSessions.Cookie, new CookieOptions { Path = "/" });
		return Results.NoContent();
	}

	// GET .../panel/calendar?from=2026-10-26&days=7
	private static async Task<IResult> CalendarAsync(Guid businessId, string? from, int? days, PanelSessions sessions, HttpContext http, GetPanelCalendar useCase, CancellationToken cancellationToken)
	{
		if (!sessions.IsSignedIn(http, businessId))
		{
			return Required();
		}

		var first = LocalDatePattern.Iso.Parse(from ?? string.Empty);
		return first.Success
			? (await useCase.HandleAsync(businessId, first.Value, days ?? DefaultDays, cancellationToken)).ToHttp(Results.Ok)
			: Error.Validation(PanelErrors.BadRange, "Give from as a date like 2026-10-26.").ToProblem();
	}

	private static async Task<IResult> TraceAsync(Guid businessId, Guid id, PanelSessions sessions, HttpContext http, GetBookingTrace useCase, CancellationToken cancellationToken) =>
		sessions.IsSignedIn(http, businessId)
			? (await useCase.HandleAsync(businessId, id, cancellationToken)).ToHttp(Results.Ok)
			: Required();

	// The business may cancel any of its bookings: no customer is asking, so no owner check, but the cut-off holds.
	private static async Task<IResult> CancelAsync(Guid businessId, Guid id, PanelSessions sessions, HttpContext http, CancelBooking useCase, CancellationToken cancellationToken) =>
		sessions.IsSignedIn(http, businessId)
			? (await useCase.HandleAsync(id, null, cancellationToken)).ToHttp(Results.Ok)
			: Required();

	private static IResult Required() => Error.Unauthorized(PanelAccessErrors.Required, "Sign in to the panel with its access key.").ToProblem();

	/// <summary>Signs in to the panel.</summary>
	/// <param name="Key">The access key.</param>
	internal sealed record KeyRequest(string? Key);
}

/// <summary>
/// The panel's live channel: a client joins its business's group when its panel session is good, and hears
/// "bookingsChanged" whenever a booking of the business is held, confirmed, moved, cancelled or released.
/// </summary>
/// <param name="sessions">Panel sessions.</param>
internal sealed class CalendarHub(PanelSessions sessions) : Hub
{
	public const string Path = "/hubs/calendar";

	public const string BookingsChanged = "bookingsChanged";

	public static string Group(Guid businessId) => $"business:{businessId:N}";

	public override async Task OnConnectedAsync()
	{
		var http = Context.GetHttpContext();
		if (http is null || !Guid.TryParse(http.Request.Query["businessId"], out var businessId) || !sessions.IsSignedIn(http, businessId))
		{
			Context.Abort();
			return;
		}

		await Groups.AddToGroupAsync(Context.ConnectionId, Group(businessId));
		await base.OnConnectedAsync();
	}
}

/// <summary>Announces booking changes to the business's connected panels.</summary>
/// <param name="hub">The hub.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class SignalRCalendarUpdates(IHubContext<CalendarHub> hub, ILogger<SignalRCalendarUpdates> logger) : ICalendarUpdates
{
	public async Task PublishAsync(BusinessId business, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken)
	{
		try
		{
			await hub.Clients.Group(CalendarHub.Group(business.Value)).SendAsync(CalendarHub.BookingsChanged, bookingIds, cancellationToken);
		}
#pragma warning disable CA1031 // The booking is saved; a panel that misses one update catches up on its next reload.
		catch (Exception exception)
#pragma warning restore CA1031
		{
			LogFailed(exception, bookingIds.Count);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Announcing {Count} booking change(s) to the panel failed.")]
	private partial void LogFailed(Exception exception, int count);
}
