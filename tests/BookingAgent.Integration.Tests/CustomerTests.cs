namespace BookingAgent.Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Domain.Bookings;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NodaTime.Testing;
using static ScriptedChatClient;

// Sign-in with an emailed code and what a signed-in customer may do, over HTTP with the real clock (next week's
// Monday in Managua), the code read from Mailpit; then the assistant's tools for a customer's own bookings, with a
// scripted model and a fixed clock.
[Collection(PostgresCollection.Name)]
public sealed partial class CustomerTests(PostgresFixture fixture) : IAsyncLifetime
{
	private const string Ana = "ana@ejemplo.com";

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	}.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

	private static readonly DateTimeZone Managua = DateTimeZoneProviders.Tzdb[DemoData.Zone];

	private static string Business => $"/v1/businesses/{DemoData.Clinic.Value}";

	private static LocalDate NextMonday =>
		SystemClock.Instance.GetCurrentInstant().InZone(Managua).Date.Next(IsoDayOfWeek.Monday).PlusWeeks(1);

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	private static Instant At(int hour) => Managua.AtStrictly(NextMonday.At(new LocalTime(hour, 0))).ToInstant();

	private static async Task<BookingResponse> BookAsync(HttpClient client, int hour, string email)
	{
		using var held = await client.PostAsJsonAsync(
			"/v1/holds",
			new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, At(hour), new CustomerDetails("Ana Pérez", email, null)),
			Json);
		var hold = (await held.Content.ReadFromJsonAsync<HoldResponse>(Json))!;
		using var confirmed = await client.PostAsJsonAsync($"/v1/holds/{hold.Booking.Id}/confirm", new { token = hold.ConfirmationToken }, Json);
		return (await confirmed.Content.ReadFromJsonAsync<BookingResponse>(Json))!;
	}

	private async Task<List<string>> CodesSentToAsync(string email)
	{
		using var mailpit = new HttpClient { BaseAddress = fixture.MailpitApi };
		var list = await mailpit.GetFromJsonAsync<JsonElement>(new Uri("api/v1/messages", UriKind.Relative));
		return [.. list.GetProperty("messages").EnumerateArray()
			.Where(m => m.GetProperty("To")[0].GetProperty("Address").GetString() == email)
			.Select(static m => SixDigits().Match(m.GetProperty("Subject").GetString()!).Value)];
	}

	private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

	[Fact]
	public async Task A_customer_signs_in_with_the_emailed_code_and_moves_and_cancels_only_their_own_bookings()
	{
		using var client = fixture.Api.CreateClient();
		var mine = await BookAsync(client, 9, Ana);
		var theirs = await BookAsync(client, 14, "beto@ejemplo.com");

		using (var anonymous = await client.GetAsync(new Uri($"{Business}/me/bookings", UriKind.Relative)))
		{
			Assert.Equal((HttpStatusCode.Unauthorized, SignInErrors.Required), (anonymous.StatusCode, await CodeAsync(anonymous)));
		}

		// The same answer for an email without bookings, and no second code within the cooldown.
		foreach (var email in new[] { "nadie@ejemplo.com", Ana, Ana })
		{
			using var asked = await client.PostAsJsonAsync($"{Business}/sign-in/code", new { email });
			Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
		}

		Assert.Empty(await CodesSentToAsync("nadie@ejemplo.com"));
		var code = Assert.Single(await CodesSentToAsync(Ana));

		using (var wrong = await client.PostAsJsonAsync($"{Business}/sign-in", new { email = Ana, code = code == "000000" ? "111111" : "000000" }))
		{
			Assert.Equal((HttpStatusCode.BadRequest, SignInErrors.InvalidCode), (wrong.StatusCode, await CodeAsync(wrong)));
		}

		using (var signedIn = await client.PostAsJsonAsync($"{Business}/sign-in", new { email = " ANA@ejemplo.com ", code }))
		{
			Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
			var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"));
			Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
		}

		var bookings = (await client.GetFromJsonAsync<List<MyBooking>>($"{Business}/me/bookings", Json))!;
		Assert.Equal([(mine.Id, true)], bookings.Select(static b => (b.Id, b.CanChange)));

		using (var notMine = await client.PostAsync(new Uri($"{Business}/me/bookings/{theirs.Id}/cancel", UriKind.Relative), null))
		{
			Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);
		}

		// Moving holds the new time; the customer confirms it like any hold, and the old time is freed.
		using var moved = await client.PostAsJsonAsync($"{Business}/me/bookings/{mine.Id}/reschedule", new { start = At(11) }, Json);
		var replacement = (await moved.Content.ReadFromJsonAsync<HoldResponse>(Json))!;
		using (await client.PostAsJsonAsync($"/v1/holds/{replacement.Booking.Id}/confirm", new { token = replacement.ConfirmationToken }, Json))
		{
		}

		bookings = (await client.GetFromJsonAsync<List<MyBooking>>($"{Business}/me/bookings", Json))!;
		Assert.Equal([new LocalTime(11, 0)], bookings.Select(static b => b.LocalStart.TimeOfDay));

		using (var cancelled = await client.PostAsync(new Uri($"{Business}/me/bookings/{replacement.Booking.Id}/cancel", UriKind.Relative), null))
		{
			Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
		}

		Assert.Empty((await client.GetFromJsonAsync<List<MyBooking>>($"{Business}/me/bookings", Json))!);
		using (var inbox = await client.GetAsync(new Uri($"{Business}/me/inbox", UriKind.Relative)))
		{
			Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
		}

		using (await client.PostAsync(new Uri($"{Business}/sign-out", UriKind.Relative), null))
		{
		}

		using var after = await client.GetAsync(new Uri($"{Business}/me", UriKind.Relative));
		Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
		await using var db = fixture.CreateContext();
		Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.SingleAsync(b => b.Id == new Domain.BookingId(theirs.Id))).Status);
	}

	[Fact]
	public async Task The_assistant_asks_an_anonymous_customer_to_sign_in_and_only_proposes_changes_to_a_signed_in_one()
	{
		var clock = new FakeClock(Instant.FromUtc(2026, 11, 1, 12, 0));
		var nineMonday = Instant.FromUtc(2026, 11, 2, 15, 0);
		var model = new ScriptedChatClient(
			_ => Call("find_my_bookings", []),
			_ => Say("Para ver tus citas, identifícate con el código que te enviamos."),
			_ => Call("find_my_bookings", []),
			messages => Call("propose_cancel", new() { ["booking_id"] = LastResult(messages, "find_my_bookings").GetProperty("bookings")[0].GetProperty("booking_id").GetString() }),
			_ => Say("Pulsa Cancelar cita en la tarjeta si quieres cancelarla."),
			_ => Call("find_my_bookings", []),
			messages => Call("propose_reschedule", new()
			{
				["booking_id"] = LastResult(messages, "find_my_bookings").GetProperty("bookings")[0].GetProperty("booking_id").GetString(),
				["start"] = (nineMonday + Duration.FromHours(2)).ToString(),
			}),
			_ => Say("Te aparté las 11:00; confírmalo con el botón."));
		await using var services = fixture.Services(clock, model);

		HoldResponse booked;
		await using (var scope = services.CreateAsyncScope())
		{
			var request = new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, nineMonday, new CustomerDetails("Ana Pérez", Ana, null));
			booked = (await scope.ServiceProvider.GetRequiredService<HoldSlot>().HandleAsync(request, CancellationToken.None)).Value;
			await scope.ServiceProvider.GetRequiredService<ConfirmHold>().HandleAsync(booked.Booking.Id, booked.ConfirmationToken, CancellationToken.None);
		}

		SignedInCustomer ana;
		await using (var db = fixture.CreateContext())
		{
			var customer = await db.Customers.SingleAsync(c => c.Email == Ana);
			ana = new SignedInCustomer(customer.Id, customer.BusinessId, customer.Name, customer.Email, clock.GetCurrentInstant() + Duration.FromHours(2));
		}

		async Task<AssistantReply> SendAsync(string message, SignedInCustomer? customer)
		{
			await using var scope = services.CreateAsyncScope();
			return (await scope.ServiceProvider.GetRequiredService<AgentRunner>()
				.HandleAsync(DemoData.Clinic.Value, new AssistantRequest(null, message), customer, CancellationToken.None)).Value;
		}

		var anonymous = await SendAsync("¿Qué citas tengo?", null);
		var cancel = await SendAsync("Cancela mi cita del lunes", ana);
		var move = await SendAsync("Pásala a las 11", ana);

		Assert.True(anonymous.SignInRequired);
		Assert.Equal(booked.Booking.Id, Assert.Single(cancel.Cancellations).BookingId);
		var card = Assert.Single(move.Holds);
		Assert.Equal((booked.Booking.Id, new LocalDateTime(2026, 11, 2, 11, 0)), (card.Replaces, card.LocalStart));

		// Proposals only: the appointment still stands, and the move waits for the customer's Confirmar.
		await using var after = fixture.CreateContext();
		Assert.Equal(BookingStatus.Confirmed, (await after.Bookings.SingleAsync(b => b.Id == new Domain.BookingId(booked.Booking.Id))).Status);
		Assert.Equal(BookingStatus.Held, (await after.Bookings.SingleAsync(b => b.Id == new Domain.BookingId(card.HoldId))).Status);
	}

	[GeneratedRegex("[0-9]{6}")]
	private static partial Regex SixDigits();
}
