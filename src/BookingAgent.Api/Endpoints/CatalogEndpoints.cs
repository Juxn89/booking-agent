namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Features.Catalog;
using BookingAgent.Application.Features.Notifications;

/// <summary>What the booking form starts from, and the in-app inbox while customers cannot sign in yet.</summary>
internal static class CatalogEndpoints
{
	/// <summary>Maps the catalog.</summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/v1/businesses/{businessId:guid}/catalog", CatalogAsync).WithTags("Catalog");
		return app;
	}

	/// <summary>
	/// Maps the in-app inbox by email, with no sign-in: the development counterpart of Mailpit's inbox. Customers
	/// get their own, behind the emailed code, on day 7.
	/// </summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static IEndpointRouteBuilder MapDevelopmentInbox(this IEndpointRouteBuilder app)
	{
		app.MapGet("/v1/dev/inbox", InboxAsync).WithTags("Development");
		return app;
	}

	private static async Task<IResult> CatalogAsync(Guid businessId, GetCatalog useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(businessId, cancellationToken)).ToHttp(Results.Ok);

	// GET /v1/dev/inbox?email=ana@ejemplo.com
	private static async Task<IResult> InboxAsync(string? email, ReadInbox useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(email, cancellationToken)).ToHttp(Results.Ok);
}
