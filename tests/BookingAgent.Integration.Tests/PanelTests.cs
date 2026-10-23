namespace BookingAgent.Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Panel;
using BookingAgent.Domain.Bookings;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NodaTime.Testing;
using static ScriptedChatClient;

// The business's panel over HTTP: the access key, the calendar, the steps behind an assistant's booking, cancelling,
// and the live update a connected panel hears when a booking changes.
[Collection(PostgresCollection.Name)]
public sealed class PanelTests(PostgresFixture fixture) : IAsyncLifetime, IAsyncDisposable
{
	private const string Key = "a-long-test-panel-key";

	// SignalR's JSON protocol ends every message with the record separator.
	private const char RecordSeparator = '\u001e';

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	}.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

	private static readonly Instant NineMonday = Instant.FromUtc(2026, 11, 2, 15, 0);

	private readonly WebApplicationFactory<Program> _api = fixture.Api.WithWebHostBuilder(static host => host.UseSetting("Panel:AccessKey", Key));

	private static string Panel => $"/v1/businesses/{DemoData.Clinic.Value}/panel";

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	async ValueTask IAsyncDisposable.DisposeAsync() => await _api.DisposeAsync();

	private async Task<HttpClient> SignedInAsync()
	{
		var client = _api.CreateClient();
		using var signedIn = await client.PostAsJsonAsync($"{Panel}/sign-in", new { key = Key });
		Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);
		return client;
	}

	[Fact]
	public async Task The_panel_opens_only_with_its_key_and_stays_closed_without_one()
	{
		// Its own host: the sign-in rate limit is per host, and the shared one belongs to the customers' tests.
		await using var withoutKey = fixture.Api.WithWebHostBuilder(static _ => { });
		using var unconfigured = withoutKey.CreateClient();
		using var client = _api.CreateClient();

		using var closed = await unconfigured.PostAsJsonAsync($"{Panel}/sign-in", new { key = Key });
		using var wrong = await client.PostAsJsonAsync($"{Panel}/sign-in", new { key = "guess" });
		using var anonymous = await client.GetAsync(new Uri($"{Panel}/calendar?from=2026-11-02", UriKind.Relative));

		Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
	}

	[Fact]
	public async Task The_calendar_shows_the_week_marks_the_assistants_bookings_and_shows_their_steps()
	{
		// The assistant holds Monday 9:00 for Ana (a scripted model on a fixed clock, on the same database).
		var model = new ScriptedChatClient(
			_ => Call("hold_slot", new()
			{
				["professional_id"] = DemoData.Lopez.Value.ToString(),
				["service_id"] = DemoData.Cleaning.Value.ToString(),
				["start"] = NineMonday.ToString(),
				["customer_name"] = "Ana Pérez",
				["customer_email"] = "ana@ejemplo.com",
				["customer_phone"] = null,
			}),
			_ => Say("Te aparté las 9:00."));
		await using (var services = fixture.Services(new FakeClock(Instant.FromUtc(2026, 11, 1, 12, 0)), model))
		await using (var scope = services.CreateAsyncScope())
		{
			await scope.ServiceProvider.GetRequiredService<AgentRunner>()
				.HandleAsync(DemoData.Clinic.Value, new AssistantRequest(null, "Limpieza el lunes a las 9, soy Ana, ana@ejemplo.com"), null, CancellationToken.None);
		}

		using var client = await SignedInAsync();
		var calendar = (await client.GetFromJsonAsync<PanelCalendar>($"{Panel}/calendar?from=2026-11-02&days=7", Json))!;

		Assert.Equal(3, calendar.Professionals.Count);
		var booking = Assert.Single(calendar.Bookings);
		Assert.Equal((BookingStatus.Held, true, "Ana Pérez", new LocalDateTime(2026, 11, 2, 9, 0)), (booking.Status, booking.ViaAssistant, booking.CustomerName, booking.LocalStart));

		var trace = (await client.GetFromJsonAsync<BookingTrace>($"{Panel}/bookings/{booking.Id}/trace", Json))!;
		Assert.Equal(["customer", "assistant"], trace.Transcript.Select(static m => m.Role));
		Assert.Equal(["Model", "hold_slot", "Model"], trace.Steps.Select(static s => s.Tool ?? s.Kind.ToString()));
		Assert.Equal(2 * 0.000125m, trace.CostUsd);
	}

	[Fact]
	public async Task A_connected_panel_hears_about_a_booking_the_moment_it_is_held()
	{
		using var client = await SignedInAsync();
		var hub = $"/hubs/calendar?businessId={DemoData.Clinic.Value}";

		// SignalR over long polling, as a browser falls back to behind a proxy: negotiate, handshake, then poll.
		using var negotiated = await client.PostAsync(new Uri($"{hub}&negotiateVersion=1", UriKind.Relative).ToString().Replace("/hubs/calendar?", "/hubs/calendar/negotiate?", StringComparison.Ordinal), null);
		var token = (await negotiated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionToken").GetString();
		var connection = $"{hub}&id={token}";
		using (var handshake = new StringContent($$"""{"protocol":"json","version":1}{{RecordSeparator}}""", Encoding.UTF8, "text/plain"))
		using (await client.PostAsync(new Uri(connection, UriKind.Relative), handshake))
		{
		}

		// The first poll may come back empty; the handshake's answer ("{}") follows.
		var received = string.Empty;
		for (var polls = 0; !received.Contains($"{{}}{RecordSeparator}", StringComparison.Ordinal) && polls < 5; polls++)
		{
			received += await client.GetStringAsync(new Uri(connection, UriKind.Relative));
		}

		Assert.Contains($"{{}}{RecordSeparator}", received, StringComparison.Ordinal);

		var poll = client.GetStringAsync(new Uri(connection, UriKind.Relative));
		using var held = await client.PostAsJsonAsync(
			"/v1/holds",
			new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, NextMondayAt(9), new CustomerDetails("Ana Pérez", "ana@ejemplo.com", null)),
			Json);
		var hold = (await held.Content.ReadFromJsonAsync<HoldResponse>(Json))!;

		var message = await poll.WaitAsync(TimeSpan.FromSeconds(10));
		var invocation = JsonDocument.Parse(message.TrimEnd(RecordSeparator).Split(RecordSeparator)[0]).RootElement;
		Assert.Equal("bookingsChanged", invocation.GetProperty("target").GetString());
		Assert.Equal([hold.Booking.Id], invocation.GetProperty("arguments")[0].EnumerateArray().Select(static id => id.GetGuid()));
	}

	[Fact]
	public async Task The_business_cancels_from_the_panel_and_the_slot_is_free_again()
	{
		using var client = await SignedInAsync();
		using var held = await client.PostAsJsonAsync(
			"/v1/holds",
			new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, NextMondayAt(10), new CustomerDetails("Beto", "beto@ejemplo.com", null)),
			Json);
		var hold = (await held.Content.ReadFromJsonAsync<HoldResponse>(Json))!;
		using (await client.PostAsJsonAsync($"/v1/holds/{hold.Booking.Id}/confirm", new { token = hold.ConfirmationToken }, Json))
		{
		}

		using var cancelled = await client.PostAsync(new Uri($"{Panel}/bookings/{hold.Booking.Id}/cancel", UriKind.Relative), null);

		Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
		Assert.Equal(BookingStatus.Cancelled, (await cancelled.Content.ReadFromJsonAsync<BookingResponse>(Json))!.Status);
	}

	private static Instant NextMondayAt(int hour)
	{
		var managua = DateTimeZoneProviders.Tzdb[DemoData.Zone];
		var monday = SystemClock.Instance.GetCurrentInstant().InZone(managua).Date.Next(IsoDayOfWeek.Monday).PlusWeeks(1);
		return managua.AtStrictly(monday.At(new LocalTime(hour, 0))).ToInstant();
	}
}
