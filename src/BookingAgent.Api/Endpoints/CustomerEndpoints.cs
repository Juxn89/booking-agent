namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Application.Features.Notifications;
using NodaTime;

/// <summary>
/// Sign-in with an emailed code, and what a signed-in customer can do: see their appointments, cancel or move one,
/// read their inbox. The session is an HttpOnly, SameSite=Strict cookie, so scripts cannot read it and other sites
/// cannot send it.
/// </summary>
internal static class CustomerEndpoints
{
	/// <summary>The session cookie.</summary>
	public const string Cookie = "booking_session";

	/// <summary>Maps the routes.</summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
	{
		var business = app.MapGroup("/v1/businesses/{businessId:guid}").WithTags("Customers");
		business.MapPost("/sign-in/code", RequestCodeAsync).RequireRateLimiting(RateLimits.SignIn);
		business.MapPost("/sign-in", SignInAsync).RequireRateLimiting(RateLimits.SignIn);
		business.MapPost("/sign-out", SignOutAsync);

		var me = business.MapGroup("/me");
		me.MapGet("/", MeAsync);
		me.MapGet("/bookings", BookingsAsync);
		me.MapPost("/bookings/{id:guid}/cancel", CancelAsync);
		me.MapPost("/bookings/{id:guid}/reschedule", RescheduleAsync);
		me.MapGet("/inbox", InboxAsync);
		return app;
	}

	/// <summary>The customer signed in to this business, from the cookie, or <see langword="null"/>.</summary>
	/// <param name="http">The request.</param>
	/// <param name="businessId">The business.</param>
	/// <param name="sessions">The sessions.</param>
	/// <returns>The customer.</returns>
	public static Task<SignedInCustomer?> CurrentAsync(HttpContext http, Guid businessId, CustomerSessions sessions) =>
		sessions.ResolveAsync(businessId, http.Request.Cookies[Cookie], http.RequestAborted);

	// Always 202: the answer must not say whether the email has bookings.
	private static async Task<IResult> RequestCodeAsync(Guid businessId, EmailRequest request, RequestSignInCode useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(businessId, request.Email, cancellationToken)).ToHttp(static _ => Results.Accepted());

	private static async Task<IResult> SignInAsync(Guid businessId, CodeRequest request, VerifySignInCode useCase, HttpContext http, CancellationToken cancellationToken)
	{
		var result = await useCase.HandleAsync(businessId, request.Email, request.Code, cancellationToken);
		if (!result.IsSuccess)
		{
			return result.Error!.ToProblem();
		}

		http.Response.Cookies.Append(Cookie, result.Value.Token, new CookieOptions
		{
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Secure = http.Request.IsHttps,
			Path = "/",
			Expires = result.Value.Customer.SessionExpiresAt.ToDateTimeOffset(),
		});
		return Results.Ok(Me.From(result.Value.Customer));
	}

	private static async Task<IResult> SignOutAsync(HttpContext http, CustomerSessions sessions, CancellationToken cancellationToken)
	{
		await sessions.SignOutAsync(http.Request.Cookies[Cookie], cancellationToken);
		http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
		return Results.NoContent();
	}

	private static async Task<IResult> MeAsync(Guid businessId, HttpContext http, CustomerSessions sessions) =>
		await CurrentAsync(http, businessId, sessions) is { } customer ? Results.Ok(Me.From(customer)) : Unauthorized();

	private static async Task<IResult> BookingsAsync(Guid businessId, HttpContext http, CustomerSessions sessions, ListMyBookings useCase, CancellationToken cancellationToken) =>
		await CurrentAsync(http, businessId, sessions) is { } customer
			? Results.Ok(await useCase.HandleAsync(customer, cancellationToken))
			: Unauthorized();

	private static async Task<IResult> CancelAsync(Guid businessId, Guid id, HttpContext http, CustomerSessions sessions, CancelBooking useCase, CancellationToken cancellationToken) =>
		await CurrentAsync(http, businessId, sessions) is { } customer
			? (await useCase.HandleAsync(id, customer.CustomerId, cancellationToken)).ToHttp(Results.Ok)
			: Unauthorized();

	private static async Task<IResult> RescheduleAsync(Guid businessId, Guid id, RescheduleRequest request, HttpContext http, CustomerSessions sessions, RescheduleBooking useCase, CancellationToken cancellationToken) =>
		await CurrentAsync(http, businessId, sessions) is { } customer
			? (await useCase.HandleAsync(id, request.Start, request.ProfessionalId, customer.CustomerId, cancellationToken)).ToHttp(Results.Ok)
			: Unauthorized();

	private static async Task<IResult> InboxAsync(Guid businessId, HttpContext http, CustomerSessions sessions, ReadInbox useCase, CancellationToken cancellationToken) =>
		await CurrentAsync(http, businessId, sessions) is { } customer
			? (await useCase.HandleAsync(customer.Email, cancellationToken)).ToHttp(Results.Ok)
			: Unauthorized();

	private static IResult Unauthorized() =>
		Error.Unauthorized(SignInErrors.Required, "Sign in with the code we email you.").ToProblem();

	/// <summary>Asks for a code.</summary>
	/// <param name="Email">The email the bookings are under.</param>
	internal sealed record EmailRequest(string? Email);

	/// <summary>Signs in.</summary>
	/// <param name="Email">The email the code was sent to.</param>
	/// <param name="Code">The code.</param>
	internal sealed record CodeRequest(string? Email, string? Code);

	/// <summary>Moves a booking.</summary>
	/// <param name="Start">The new start, one the availability offers.</param>
	/// <param name="ProfessionalId">Another professional, or the same when null.</param>
	internal sealed record RescheduleRequest(Instant Start, Guid? ProfessionalId);

	/// <summary>The signed-in customer, for the web app.</summary>
	/// <param name="Name">Their name.</param>
	/// <param name="Email">Their email.</param>
	/// <param name="ExpiresAt">When the session ends.</param>
	internal sealed record Me(string Name, string Email, Instant ExpiresAt)
	{
		public static Me From(SignedInCustomer customer) => new(customer.Name, customer.Email, customer.SessionExpiresAt);
	}
}
