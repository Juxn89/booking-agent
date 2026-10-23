namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

/// <summary>Schedules, rules and what is already taken, read in one go for a professional and a service.</summary>
/// <param name="db">The database.</param>
/// <param name="clock">The clock: a hold that ran out no longer takes its slot, swept or not.</param>
internal sealed class AvailabilitySource(BookingDbContext db, IClock clock) : IAvailabilitySource
{
	public async Task<AvailabilityContext?> LoadAsync(Guid professionalId, Guid serviceId, DateInterval dates, CancellationToken cancellationToken)
	{
		var professional = await db.Professionals.AsNoTracking()
			.FirstOrDefaultAsync(p => p.Id == new ProfessionalId(professionalId), cancellationToken)
			.ConfigureAwait(false);
		var service = await db.Services.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == new ServiceId(serviceId), cancellationToken)
			.ConfigureAwait(false);
		if (professional is null || service is null || service.BusinessId != professional.BusinessId || !professional.Offers(service.Id))
		{
			return null;
		}

		var business = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == professional.BusinessId, cancellationToken).ConfigureAwait(false);
		var zone = business.Zone;

		// The whole days on the business's clock, from the first one's start to the day after the last.
		var from = dates.Start.AtStartOfDayInZone(zone).ToInstant();
		var to = dates.End.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();
		var now = clock.GetCurrentInstant();

		var booked = await db.Bookings.AsNoTracking()
			.Where(b => b.ProfessionalId == professional.Id
				&& b.Period.Start < to && b.Period.End > from
				&& (b.Status == BookingStatus.Confirmed || (b.Status == BookingStatus.Held && b.HoldExpiresAt > now)))
			.Select(static b => b.Period)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		var away = await db.TimeOff.AsNoTracking()
			.Where(t => t.ProfessionalId == professional.Id && t.Period.Start < to && t.Period.End > from)
			.Select(static t => t.Period)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

		return new AvailabilityContext(business.Id, professional.Schedule, zone, service.Duration, [.. booked, .. away], business.Rules);
	}
}

/// <summary>Bookings and customers on EF Core; the database's refusals become the application's exceptions.</summary>
/// <param name="db">The database.</param>
internal sealed class BookingRepository(BookingDbContext db) : IBookingRepository
{
	public Task<Booking?> FindTrackedAsync(BookingId id, CancellationToken cancellationToken) =>
		db.Bookings.AsTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

	public Task<Customer?> FindCustomerAsync(BusinessId business, string normalizedEmail, CancellationToken cancellationToken) =>
		db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.BusinessId == business && c.Email == normalizedEmail, cancellationToken);

	public async Task<DateTimeZone> ZoneOfAsync(ProfessionalId professional, CancellationToken cancellationToken)
	{
		var zone = await db.Professionals.AsNoTracking()
			.Where(p => p.Id == professional)
			.Join(db.Businesses, static p => p.BusinessId, static b => b.Id, static (_, b) => b.TimeZoneId)
			.SingleAsync(cancellationToken)
			.ConfigureAwait(false);
		return DateTimeZoneProviders.Tzdb[zone];
	}

	public async Task<IReadOnlyList<Booking>> StaleHoldsAsync(ProfessionalId professional, Interval period, Instant now, CancellationToken cancellationToken) =>
		await db.Bookings.AsTracking()
			.Where(b => b.ProfessionalId == professional
				&& b.Status == BookingStatus.Held
				&& b.HoldExpiresAt <= now
				&& b.Period.Start < period.End && b.Period.End > period.Start)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<Booking>> ExpiredHoldsAsync(Instant now, int limit, CancellationToken cancellationToken) =>
		await db.Bookings.AsTracking()
			.Where(b => b.Status == BookingStatus.Held && b.HoldExpiresAt <= now)
			.OrderBy(static b => b.HoldExpiresAt)
			.Take(limit)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

	public void Add(Booking booking) => db.Bookings.Add(booking);

	public void Add(Customer customer) => db.Customers.Add(customer);

	public async Task SaveChangesAsync(CancellationToken cancellationToken)
	{
		// The outbox: the notifications go in with the change they announce, or not at all.
		var changed = db.ChangeTracker.Entries<Booking>()
			.Select(static entry => entry.Entity)
			.Where(static booking => booking.Changes.Count > 0)
			.ToList();
		db.Notifications.AddRange(changed.SelectMany(static booking => booking.Changes).SelectMany(NotificationPlanner.Plan));

		try
		{
			await SaveSerializedAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException exception)
		{
			db.ChangeTracker.Clear();
			throw new BookingChangedException("A booking changed since it was loaded.", exception);
		}
		catch (Exception exception) when (LostTheSlot(exception))
		{
			// The failed rows stay tracked; whatever this unit of work does next must start clean.
			db.ChangeTracker.Clear();
			throw new SlotTakenException("The period overlaps a booking saved a moment before.", exception);
		}

		foreach (var booking in changed)
		{
			booking.ClearChanges();
		}
	}

	// New bookings of one professional are saved one at a time: a transaction-scoped advisory lock per professional,
	// taken before the insert. Without it, fifty requests for the same slot all insert at once and each one's
	// exclusion check waits on the others' uncommitted rows; Postgres untangles that one deadlock per second, and
	// the tail of the queue times out (found by the fifty-request test). With it, each insert sees the previous
	// one committed and loses cleanly to the constraint. Different professionals never wait for each other.
	private async Task SaveSerializedAsync(CancellationToken cancellationToken)
	{
		var professionals = db.ChangeTracker.Entries<Booking>()
			.Where(static entry => entry.State == EntityState.Added)
			.Select(static entry => entry.Entity.ProfessionalId.Value)
			.Distinct()
			.Order()
			.ToList();
		if (professionals.Count == 0 || db.Database.CurrentTransaction is not null)
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			return;
		}

		await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		foreach (var professional in professionals)
		{
			var key = BitConverter.ToInt64(professional.ToByteArray(), 0) ^ BitConverter.ToInt64(professional.ToByteArray(), sizeof(long));
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken).ConfigureAwait(false);
		}

		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	// Two ways to lose a race for a slot. The exclusion constraint finds the other booking committed (23P01), or,
	// should two overlapping inserts ever run at the same moment despite the lock above (a booking saved by other
	// code), Postgres breaks their mutual wait by aborting one (40P01), which lost the slot too. EF Core reports the
	// deadlock wrapped as a transient failure, hence the walk down the inner exceptions.
	private static bool LostTheSlot(Exception exception)
	{
		for (var inner = exception; inner is not null; inner = inner.InnerException)
		{
			if (inner is PostgresException postgres)
			{
				return postgres.SqlState == PostgresErrorCodes.DeadlockDetected
					|| (postgres.SqlState == PostgresErrorCodes.ExclusionViolation && postgres.ConstraintName == BookingConfiguration.NoOverlap);
			}
		}

		return false;
	}
}
