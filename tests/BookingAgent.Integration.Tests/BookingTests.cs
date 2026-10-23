namespace BookingAgent.Integration.Tests;

using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

// The use cases on the real database. Sunday 1 Nov 2026 noon UTC; the slots are on Monday 2 Nov in Managua
// (UTC-6), where Dra. López works 08:00-12:00 and 14:00-18:00 and a cleaning takes 45 minutes.
[Collection(PostgresCollection.Name)]
public sealed class BookingTests(PostgresFixture fixture) : IAsyncLifetime
{
	private static readonly Instant NineMonday = Instant.FromUtc(2026, 11, 2, 15, 0);

	private readonly FakeClock _clock = new(Instant.FromUtc(2026, 11, 1, 12, 0));

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	private static HoldRequest Request(Instant start, string who = "ana", Guid? service = null) =>
		new(DemoData.Lopez.Value, service ?? DemoData.Cleaning.Value, start, new CustomerDetails(who, $"{who}@ejemplo.com", null));

	private static async Task<Result<HoldResponse>> HoldAsync(ServiceProvider services, HoldRequest request)
	{
		await using var scope = services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<HoldSlot>().HandleAsync(request, CancellationToken.None);
	}

	private static async Task<Result<BookingResponse>> ConfirmAsync(ServiceProvider services, HoldResponse hold)
	{
		await using var scope = services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<ConfirmHold>().HandleAsync(hold.Booking.Id, hold.ConfirmationToken, CancellationToken.None);
	}

	[Fact]
	public async Task Fifty_customers_asking_for_the_same_slot_at_once_get_exactly_one_booking()
	{
		await using var services = fixture.Services(_clock);
		var start = new TaskCompletionSource();
		var elapsed = System.Diagnostics.Stopwatch.StartNew();

		var attempts = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
		{
			await start.Task;
			return await HoldAsync(services, Request(NineMonday, $"cliente{i}"));
		})).ToList();
		start.SetResult();
		var results = await Task.WhenAll(attempts);

		Assert.Single(results, static r => r.IsSuccess);

		// Losing must be quick, not just correct: without the per-professional lock the losers queue behind one
		// another's uncommitted rows and the last ones hit the 30-second command timeout.
		Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"fifty holds took {elapsed.Elapsed}");

		// Whoever lost either saw the slot taken before trying, or was stopped by the constraint: both are a 409.
		Assert.All(results.Where(static r => !r.IsSuccess), static r =>
		{
			Assert.Contains(r.Error!.Code, new[] { BookingErrors.SlotTaken, BookingErrors.SlotUnavailable });
			Assert.Equal(ErrorKind.Conflict, r.Error.Kind);
		});

		await using var db = fixture.CreateContext();
		Assert.Equal(1, await db.Bookings.CountAsync(b => b.Status == BookingStatus.Held));
	}

	[Fact]
	public async Task The_database_refuses_an_overlap_even_when_the_checks_before_it_are_skipped()
	{
		await using var services = fixture.Services(_clock);
		Assert.True((await HoldAsync(services, Request(NineMonday))).IsSuccess);

		await using var scope = services.CreateAsyncScope();
		var repository = scope.ServiceProvider.GetRequiredService<BookingAgent.Application.Abstractions.IBookingRepository>();
		var customer = await scope.ServiceProvider.GetRequiredService<BookingDbContext>().Customers.SingleAsync();
		var overlapping = new Interval(NineMonday + Duration.FromMinutes(30), NineMonday + Duration.FromMinutes(75));
		repository.Add(Booking.Hold(DemoData.Lopez, DemoData.Cleaning, customer.Id, overlapping, _clock.GetCurrentInstant(), Duration.FromMinutes(5)).Booking);

		await Assert.ThrowsAsync<SlotTakenException>(() => repository.SaveChangesAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Back_to_back_appointments_fit_because_periods_are_half_open()
	{
		await using var services = fixture.Services(_clock);
		var orthodontics = DemoData.Orthodontics.Value;

		Assert.True((await HoldAsync(services, Request(NineMonday, "ana", orthodontics))).IsSuccess);
		Assert.True((await HoldAsync(services, Request(NineMonday + Duration.FromMinutes(30), "beto", orthodontics))).IsSuccess);
	}

	[Fact]
	public async Task A_slot_outside_the_professionals_hours_or_not_on_the_grid_is_refused_with_alternatives()
	{
		await using var services = fixture.Services(_clock);

		var lunch = await HoldAsync(services, Request(Instant.FromUtc(2026, 11, 2, 18, 30)));
		var offGrid = await HoldAsync(services, Request(NineMonday + Duration.FromMinutes(10)));

		Assert.Equal(BookingErrors.SlotUnavailable, lunch.Error!.Code);
		Assert.Equal(BookingErrors.SlotUnavailable, offGrid.Error!.Code);
		Assert.Equal(3, offGrid.Error.Details![BookingErrors.AlternativesDetail].Split(',').Length);
	}

	[Fact]
	public async Task A_hold_nobody_confirmed_frees_its_slot_for_the_next_customer_and_cannot_be_confirmed_any_more()
	{
		await using var services = fixture.Services(_clock);
		var first = (await HoldAsync(services, Request(NineMonday, "ana"))).Value;
		Assert.Equal(BookingErrors.SlotUnavailable, (await HoldAsync(services, Request(NineMonday, "beto"))).Error!.Code);

		_clock.Advance(Duration.FromMinutes(6));
		var second = await HoldAsync(services, Request(NineMonday, "beto"));
		var late = await ConfirmAsync(services, first);

		Assert.True(second.IsSuccess);
		Assert.Equal(BookingErrors.HoldExpired, late.Error!.Code);
		await using var db = fixture.CreateContext();
		var expired = await db.Bookings.SingleAsync(b => b.Id == new BookingId(first.Booking.Id));
		Assert.Equal((BookingStatus.Cancelled, CancellationReason.HoldExpired), (expired.Status, expired.CancellationReason!.Value));
	}

	[Fact]
	public async Task The_sweeper_releases_holds_that_ran_out_and_leaves_the_rest()
	{
		await using var services = fixture.Services(_clock);
		await HoldAsync(services, Request(NineMonday, "ana"));
		_clock.Advance(Duration.FromMinutes(3));
		await HoldAsync(services, Request(NineMonday + Duration.FromHours(1), "beto"));
		_clock.Advance(Duration.FromMinutes(3));

		await using var scope = services.CreateAsyncScope();
		var released = await scope.ServiceProvider.GetRequiredService<ExpireHolds>().HandleAsync(CancellationToken.None);

		Assert.Equal(1, released);
		await using var db = fixture.CreateContext();
		Assert.Equal(1, await db.Bookings.CountAsync(b => b.Status == BookingStatus.Held));
	}

	[Fact]
	public async Task A_moved_booking_keeps_its_time_until_the_new_one_is_confirmed_and_then_frees_it()
	{
		await using var services = fixture.Services(_clock);
		var original = (await HoldAsync(services, Request(NineMonday))).Value;
		await ConfirmAsync(services, original);

		HoldResponse replacement;
		await using (var scope = services.CreateAsyncScope())
		{
			replacement = (await scope.ServiceProvider.GetRequiredService<RescheduleBooking>()
				.HandleAsync(original.Booking.Id, NineMonday + Duration.FromHours(1), null, null, CancellationToken.None)).Value;
		}

		await using (var db = fixture.CreateContext())
		{
			Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.SingleAsync(b => b.Id == new BookingId(original.Booking.Id))).Status);
		}

		Assert.True((await ConfirmAsync(services, replacement)).IsSuccess);

		await using (var db = fixture.CreateContext())
		{
			var moved = await db.Bookings.SingleAsync(b => b.Id == new BookingId(original.Booking.Id));
			Assert.Equal((BookingStatus.Cancelled, CancellationReason.Rescheduled), (moved.Status, moved.CancellationReason!.Value));
			Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.SingleAsync(b => b.Id == new BookingId(replacement.Booking.Id))).Status);
		}

		// The original time is free again.
		Assert.True((await HoldAsync(services, Request(NineMonday, "beto"))).IsSuccess);
	}

	[Fact]
	public async Task A_confirmed_booking_cannot_be_cancelled_inside_the_cutoff()
	{
		await using var services = fixture.Services(_clock);
		var hold = (await HoldAsync(services, Request(NineMonday))).Value;
		await ConfirmAsync(services, hold);

		_clock.Reset(NineMonday - Duration.FromMinutes(90));
		await using var scope = services.CreateAsyncScope();
		var result = await scope.ServiceProvider.GetRequiredService<CancelBooking>().HandleAsync(hold.Booking.Id, null, CancellationToken.None);

		Assert.Equal(BookingErrors.TooLate, result.Error!.Code);
	}

	[Fact]
	public async Task A_returning_customer_is_found_by_email_whatever_the_case()
	{
		await using var services = fixture.Services(_clock);
		await HoldAsync(services, Request(NineMonday, "ana"));
		await HoldAsync(services, Request(NineMonday + Duration.FromHours(1), "ANA"));

		await using var db = fixture.CreateContext();
		Assert.Equal(1, await db.Customers.CountAsync());
	}
}
