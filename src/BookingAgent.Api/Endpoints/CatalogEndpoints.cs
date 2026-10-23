namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Features.Catalog;

/// <summary>What the booking form and the assistant start from.</summary>
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

	private static async Task<IResult> CatalogAsync(Guid businessId, GetCatalog useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(businessId, cancellationToken)).ToHttp(Results.Ok);
}
