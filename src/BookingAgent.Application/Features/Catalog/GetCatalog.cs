namespace BookingAgent.Application.Features.Catalog;

using BookingAgent.Application.Common;
using BookingAgent.Domain;
using BookingAgent.Domain.Catalog;

/// <summary>A business as customers see it.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
/// <param name="TimeZone">The IANA zone its hours are on.</param>
public sealed record BusinessInfo(Guid Id, string Name, string TimeZone);

/// <summary>A service as customers see it.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
/// <param name="DurationMinutes">How long it lasts.</param>
/// <param name="Price">Its price, in the business's currency.</param>
public sealed record ServiceInfo(Guid Id, string Name, int DurationMinutes, decimal Price);

/// <summary>A professional as customers see them.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
/// <param name="Specialty">What they do.</param>
/// <param name="ServiceIds">The services they offer.</param>
public sealed record ProfessionalInfo(Guid Id, string Name, string Specialty, IReadOnlyList<Guid> ServiceIds);

/// <summary>What a business offers and who offers it.</summary>
/// <param name="Business">The business.</param>
/// <param name="Services">Its services, by name.</param>
/// <param name="Professionals">Its professionals, by name.</param>
public sealed record CatalogResponse(BusinessInfo Business, IReadOnlyList<ServiceInfo> Services, IReadOnlyList<ProfessionalInfo> Professionals);

/// <summary>A business's catalog, as stored.</summary>
/// <param name="Business">The business.</param>
/// <param name="Services">Its services.</param>
/// <param name="Professionals">Its professionals.</param>
public sealed record CatalogData(Business Business, IReadOnlyList<Service> Services, IReadOnlyList<Professional> Professionals);

/// <summary>Reads a business's catalog.</summary>
public interface ICatalogReader
{
	/// <summary>Loads it.</summary>
	/// <param name="business">The business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The catalog, or <see langword="null"/> when there is no such business.</returns>
	Task<CatalogData?> LoadAsync(BusinessId business, CancellationToken cancellationToken);
}

/// <summary>The catalog error codes.</summary>
public static class CatalogErrors
{
	/// <summary>No business with that id.</summary>
	public const string NotFound = "catalog.not_found";
}

/// <summary>
/// Services and professionals: what the booking form starts from, and what the assistant's
/// <c>list_services</c> and <c>list_professionals</c> tools will answer with.
/// </summary>
/// <param name="reader">The catalog.</param>
public sealed class GetCatalog(ICatalogReader reader)
{
	/// <summary>Reads it.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The catalog, or not found.</returns>
	public async Task<Result<CatalogResponse>> HandleAsync(Guid businessId, CancellationToken cancellationToken)
	{
		var data = await reader.LoadAsync(new BusinessId(businessId), cancellationToken).ConfigureAwait(false);
		if (data is null)
		{
			return Error.NotFound(CatalogErrors.NotFound, $"There is no business {businessId}.");
		}

		return new CatalogResponse(
			new BusinessInfo(data.Business.Id.Value, data.Business.Name, data.Business.TimeZoneId),
			[.. data.Services
				.OrderBy(static s => s.Name, StringComparer.Ordinal)
				.Select(static s => new ServiceInfo(s.Id.Value, s.Name, (int)s.Duration.TotalMinutes, s.Price))],
			[.. data.Professionals
				.OrderBy(static p => p.Name, StringComparer.Ordinal)
				.Select(static p => new ProfessionalInfo(p.Id.Value, p.Name, p.Specialty, [.. p.Services.Select(static s => s.Value)]))]);
	}
}
