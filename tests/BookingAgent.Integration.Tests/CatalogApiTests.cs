namespace BookingAgent.Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class CatalogApiTests(PostgresFixture fixture) : IDisposable
{
	private readonly HttpClient _client = fixture.Api.CreateClient();

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task The_catalog_lists_the_services_and_who_offers_them()
	{
		var catalog = (await _client.GetFromJsonAsync<CatalogResponse>($"/v1/businesses/{DemoData.Clinic.Value}/catalog"))!;

		Assert.Equal(("Clínica Demo", DemoData.Zone), (catalog.Business.Name, catalog.Business.TimeZone));
		Assert.Equal(5, catalog.Services.Count);
		Assert.Equal(45, catalog.Services.Single(s => s.Id == DemoData.Cleaning.Value).DurationMinutes);
		var lopez = catalog.Professionals.Single(p => p.Id == DemoData.Lopez.Value);
		Assert.Equal([DemoData.Cleaning.Value, DemoData.Orthodontics.Value], lopez.ServiceIds.Order());
	}

	[Fact]
	public async Task An_unknown_business_is_a_404_with_its_code()
	{
		using var response = await _client.GetAsync(new Uri($"/v1/businesses/{Guid.NewGuid()}/catalog", UriKind.Relative));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(CatalogErrors.NotFound, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
	}
}
