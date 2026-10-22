namespace BookingAgent.Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Domain.Bookings;
using BookingAgent.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

// Over HTTP with the real clock: the slots are on next week's Monday in Managua, always in the future.
[Collection(PostgresCollection.Name)]
public sealed class BookingApiTests(PostgresFixture fixture) : IAsyncLifetime
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	}.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

	private static readonly DateTimeZone Managua = DateTimeZoneProviders.Tzdb[DemoData.Zone];

	private readonly HttpClient _client = fixture.Api.CreateClient();

	// Eight to fourteen days ahead: past any minimum notice, whatever day the tests run.
	private static LocalDate NextMonday =>
		SystemClock.Instance.GetCurrentInstant().InZone(Managua).Date.Next(IsoDayOfWeek.Monday).PlusWeeks(1);

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync()
	{
		_client.Dispose();
		return Task.CompletedTask;
	}

	private async Task<List<SlotDto>> SlotsAsync(LocalDate day)
	{
		var url = $"/v1/professionals/{DemoData.Lopez.Value}/availability?serviceId={DemoData.Cleaning.Value}&from={day:yyyy-MM-dd}";
		return (await _client.GetFromJsonAsync<List<SlotDto>>(url, Json))!;
	}

	[Fact]
	public async Task A_slot_is_found_held_and_confirmed_over_http_and_then_no_longer_offered()
	{
		var monday = NextMonday;
		var slots = await SlotsAsync(monday);
		Assert.Equal(new LocalDateTime(monday.Year, monday.Month, monday.Day, 8, 0), slots[0].LocalStart);

		var nine = slots.Single(s => s.LocalStart.Hour == 9 && s.LocalStart.Minute == 0);
		using var held = await _client.PostAsJsonAsync(
			"/v1/holds",
			new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, nine.Start, new CustomerDetails("Ana Pérez", "ana@ejemplo.com", null)),
			Json);
		Assert.Equal(HttpStatusCode.Created, held.StatusCode);
		var hold = (await held.Content.ReadFromJsonAsync<HoldResponse>(Json))!;
		Assert.Equal(BookingStatus.Held, hold.Booking.Status);
		Assert.Equal(Duration.FromMinutes(45), hold.Booking.End - hold.Booking.Start);

		Assert.DoesNotContain(await SlotsAsync(monday), s => s.Start == nine.Start);

		using var wrong = await _client.PostAsJsonAsync($"/v1/holds/{hold.Booking.Id}/confirm", new { token = "not-the-token" }, Json);
		Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

		using var confirmed = await _client.PostAsJsonAsync($"/v1/holds/{hold.Booking.Id}/confirm", new { token = hold.ConfirmationToken }, Json);
		using var again = await _client.PostAsJsonAsync($"/v1/holds/{hold.Booking.Id}/confirm", new { token = hold.ConfirmationToken }, Json);
		Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (confirmed.StatusCode, again.StatusCode));
		Assert.Equal(BookingStatus.Confirmed, (await confirmed.Content.ReadFromJsonAsync<BookingResponse>(Json))!.Status);
	}

	[Fact]
	public async Task A_taken_slot_is_a_409_with_the_nearest_free_times()
	{
		var nine = (await SlotsAsync(NextMonday)).Single(s => s.LocalStart.Hour == 9 && s.LocalStart.Minute == 0);
		var request = new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, nine.Start, new CustomerDetails("Ana", "ana@ejemplo.com", null));
		using (await _client.PostAsJsonAsync("/v1/holds", request, Json))
		{
		}

		using var second = await _client.PostAsJsonAsync("/v1/holds", request with { Customer = new CustomerDetails("Beto", "beto@ejemplo.com", null) }, Json);

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
		var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(BookingErrors.SlotUnavailable, problem.GetProperty("code").GetString());
		Assert.Equal(3, problem.GetProperty(BookingErrors.AlternativesDetail).GetString()!.Split(',').Length);
	}

	[Theory]
	[InlineData("serviceId=01920000-0000-7000-8000-000000000204&from=2026-11-02", HttpStatusCode.NotFound)]
	[InlineData("serviceId=01920000-0000-7000-8000-000000000202&from=next-monday", HttpStatusCode.BadRequest)]
	[InlineData("serviceId=01920000-0000-7000-8000-000000000202&from=2026-11-10&to=2026-11-02", HttpStatusCode.BadRequest)]
	public async Task Bad_availability_requests_are_problems_not_empty_lists(string query, HttpStatusCode expected)
	{
		using var response = await _client.GetAsync(new Uri($"/v1/professionals/{DemoData.Lopez.Value}/availability?{query}", UriKind.Relative));

		Assert.Equal(expected, response.StatusCode);
	}

	[Fact]
	public async Task A_hold_without_a_valid_email_is_refused()
	{
		var nine = (await SlotsAsync(NextMonday)).First();
		using var response = await _client.PostAsJsonAsync(
			"/v1/holds",
			new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, nine.Start, new CustomerDetails("Ana", "ana-sin-arroba", null)),
			Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(BookingErrors.InvalidCustomer, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
	}

	private sealed record SlotDto(Instant Start, Instant End, LocalDateTime LocalStart);
}
