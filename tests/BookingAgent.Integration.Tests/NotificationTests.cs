namespace BookingAgent.Integration.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

// The outbox on the real database and the email through a real Mailpit. Sunday 1 Nov 2026 noon UTC; the
// appointment is a cleaning with Dra. López on Monday 2 Nov at 9:00 in Managua (15:00 UTC).
[Collection(PostgresCollection.Name)]
public sealed class NotificationTests(PostgresFixture fixture) : IAsyncLifetime
{
	private const string Ana = "ana@ejemplo.com";

	private static readonly Instant NineMonday = Instant.FromUtc(2026, 11, 2, 15, 0);

	private readonly FakeClock _clock = new(Instant.FromUtc(2026, 11, 1, 12, 0));

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	private static async Task<HoldResponse> HoldAsync(ServiceProvider services)
	{
		await using var scope = services.CreateAsyncScope();
		var request = new HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, NineMonday, new CustomerDetails("Ana Pérez", Ana, null));
		return (await scope.ServiceProvider.GetRequiredService<HoldSlot>().HandleAsync(request, CancellationToken.None)).Value;
	}

	private static async Task<T> RunAsync<T>(ServiceProvider services, Func<IServiceProvider, Task<T>> action)
	{
		await using var scope = services.CreateAsyncScope();
		return await action(scope.ServiceProvider);
	}

	private static Task<DispatchReport> DispatchAsync(ServiceProvider services) =>
		RunAsync(services, static s => s.GetRequiredService<DispatchNotifications>().HandleAsync(CancellationToken.None));

	private async Task<List<string>> MailpitSubjectsAsync()
	{
		using var mailpit = new HttpClient { BaseAddress = fixture.MailpitApi };
		var list = await mailpit.GetFromJsonAsync<JsonElement>(new Uri("api/v1/messages", UriKind.Relative));
		return [.. list.GetProperty("messages").EnumerateArray()
			.Select(static m => $"{m.GetProperty("To")[0].GetProperty("Address").GetString()} | {m.GetProperty("Subject").GetString()}")];
	}

	[Fact]
	public async Task A_booking_is_announced_reminded_and_cancelled_by_email_and_in_the_app()
	{
		await using var services = fixture.Services(_clock);
		var hold = await HoldAsync(services);
		await RunAsync(services, s => s.GetRequiredService<ConfirmHold>().HandleAsync(hold.Booking.Id, hold.ConfirmationToken, CancellationToken.None));

		await using (var db = fixture.CreateContext())
		{
			Assert.Equal(6, await db.Notifications.CountAsync(n => n.Status == NotificationStatus.Pending));
		}

		Assert.Equal(new DispatchReport(Sent: 2, Skipped: 0, Retrying: 0, Failed: 0), await DispatchAsync(services));
		Assert.Equal(0, (await DispatchAsync(services)).Total);
		Assert.Equal([$"{Ana} | Cita confirmada: Limpieza dental, lunes 2 de noviembre a las 9:00"], await MailpitSubjectsAsync());

		// The day before at 9:00 in Managua: the first reminder.
		_clock.Reset(NineMonday - Duration.FromHours(24));
		Assert.Equal(2, (await DispatchAsync(services)).Sent);

		// Cancelled that afternoon: the notice goes out, and the two-hour reminder is skipped when it comes due.
		_clock.Reset(NineMonday - Duration.FromHours(20));
		Assert.True((await RunAsync(services, s => s.GetRequiredService<CancelBooking>().HandleAsync(hold.Booking.Id, null, CancellationToken.None))).IsSuccess);
		Assert.Equal(2, (await DispatchAsync(services)).Sent);
		_clock.Reset(NineMonday - Duration.FromHours(2));
		Assert.Equal(new DispatchReport(Sent: 0, Skipped: 2, Retrying: 0, Failed: 0), await DispatchAsync(services));

		Assert.Equal(3, (await MailpitSubjectsAsync()).Count);
		var inbox = await RunAsync(services, static s => s.GetRequiredService<ReadInbox>().HandleAsync(" ANA@ejemplo.com ", CancellationToken.None));
		Assert.Equal(["Cita cancelada", "Recordatorio", "Cita confirmada"], inbox.Value.Select(static m => m.Subject.Split(':')[0]));
		Assert.All(inbox.Value, m => Assert.Equal(hold.Booking.Id, m.BookingId));
	}

	[Fact]
	public async Task A_confirmation_that_fails_to_save_leaves_no_notification_behind()
	{
		await using var services = fixture.Services(_clock);
		var hold = await HoldAsync(services);

		await using var scope = services.CreateAsyncScope();
		var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
		var booking = (await repository.FindTrackedAsync(new BookingId(hold.Booking.Id), CancellationToken.None))!;
		Assert.Equal(ConfirmOutcome.Confirmed, booking.Confirm(hold.ConfirmationToken, _clock.GetCurrentInstant()));

		// Meanwhile the hold is released elsewhere: the confirmation's save must lose, and its notices with it.
		Assert.True((await RunAsync(services, s => s.GetRequiredService<CancelBooking>().HandleAsync(hold.Booking.Id, null, CancellationToken.None))).IsSuccess);
		await Assert.ThrowsAsync<BookingChangedException>(() => repository.SaveChangesAsync(CancellationToken.None));

		await using var db = fixture.CreateContext();
		Assert.Equal(0, await db.Notifications.CountAsync());
		Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.SingleAsync()).Status);
	}

	[Fact]
	public async Task A_claimed_notification_is_skipped_by_other_dispatchers_until_its_lease_runs_out()
	{
		await using var services = fixture.Services(_clock);
		var hold = await HoldAsync(services);
		await RunAsync(services, s => s.GetRequiredService<ConfirmHold>().HandleAsync(hold.Booking.Id, hold.ConfirmationToken, CancellationToken.None));
		var lease = Duration.FromMinutes(5);
		var now = _clock.GetCurrentInstant();

		Task<IReadOnlyList<DueNotification>> ClaimAsync(Instant at) =>
			RunAsync(services, s => s.GetRequiredService<INotificationOutbox>().ClaimDueAsync(at, 10, lease, CancellationToken.None));

		// Two dispatchers at once share the due notifications, never both taking one.
		var claims = await Task.WhenAll(ClaimAsync(now), ClaimAsync(now));
		var ids = claims.SelectMany(static c => c).Select(static d => d.Notification.Id).ToList();
		Assert.Equal(2, ids.Count);
		Assert.Equal(ids.Count, ids.Distinct().Count());

		// Neither recorded an outcome (as if both crashed): the notifications come back once the lease ends.
		Assert.Empty(await ClaimAsync(now + lease - Duration.FromSeconds(1)));
		Assert.Equal(2, (await ClaimAsync(now + lease)).Count);
	}

	[Fact]
	public async Task An_inbox_asked_for_with_something_that_is_not_an_email_is_refused()
	{
		await using var services = fixture.Services(_clock);

		var result = await RunAsync(services, static s => s.GetRequiredService<ReadInbox>().HandleAsync("ana", CancellationToken.None));

		Assert.Equal((ErrorKind.Validation, InboxErrors.InvalidEmail), (result.Error!.Kind, result.Error.Code));
	}
}
