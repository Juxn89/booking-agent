namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Domain;
using BookingAgent.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>A business's services and professionals.</summary>
/// <param name="db">The database.</param>
internal sealed class CatalogReader(BookingDbContext db) : ICatalogReader
{
	public async Task<CatalogData?> LoadAsync(BusinessId business, CancellationToken cancellationToken)
	{
		var found = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == business, cancellationToken).ConfigureAwait(false);
		if (found is null)
		{
			return null;
		}

		var services = await db.Services.AsNoTracking().Where(s => s.BusinessId == business).ToListAsync(cancellationToken).ConfigureAwait(false);
		var professionals = await db.Professionals.AsNoTracking().Where(p => p.BusinessId == business).ToListAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogData(found, services, professionals);
	}
}

/// <summary>The in-app inbox, read by the customer's email.</summary>
/// <param name="db">The database.</param>
internal sealed class InboxReader(BookingDbContext db) : IInboxReader
{
	public async Task<IReadOnlyList<InboxMessage>> ForEmailAsync(string normalizedEmail, int limit, CancellationToken cancellationToken) =>
		await db.Inbox.AsNoTracking()
			.Join(db.Customers.Where(c => c.Email == normalizedEmail), static m => m.CustomerId, static c => c.Id, static (m, _) => m)
			.OrderByDescending(static m => m.ReceivedAt)
			.Take(limit)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
}

/// <summary>The outbox on Postgres: claimed with <c>FOR UPDATE SKIP LOCKED</c> and leased, so dispatchers never collide.</summary>
/// <param name="db">The database.</param>
internal sealed class NotificationOutbox(BookingDbContext db) : INotificationOutbox
{
	public async Task<IReadOnlyList<DueNotification>> ClaimDueAsync(Instant now, int limit, Duration lease, CancellationToken cancellationToken)
	{
		// Short transaction: lock, lease, commit. Sending happens after, without holding any lock; the lease keeps
		// other dispatchers away meanwhile, and brings the row back if this one dies before recording the outcome.
		var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		List<Notification> claimed;
		await using (transaction.ConfigureAwait(false))
		{
			claimed = await db.Notifications
				.FromSql($"""
					SELECT * FROM notifications
					WHERE status = 'Pending' AND next_attempt_at <= {now}
					ORDER BY next_attempt_at
					LIMIT {limit}
					FOR UPDATE SKIP LOCKED
					""")
				.AsTracking()
				.ToListAsync(cancellationToken)
				.ConfigureAwait(false);
			foreach (var notification in claimed)
			{
				notification.Lease(now + lease);
			}

			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}

		if (claimed.Count == 0)
		{
			return [];
		}

		var bookings = claimed.Select(static n => n.BookingId).Distinct().ToList();
		var subjects = await (
				from booking in db.Bookings.AsNoTracking()
				where bookings.Contains(booking.Id)
				join customer in db.Customers on booking.CustomerId equals customer.Id
				join service in db.Services on booking.ServiceId equals service.Id
				join professional in db.Professionals on booking.ProfessionalId equals professional.Id
				join business in db.Businesses on professional.BusinessId equals business.Id
				select new
				{
					booking.Id,
					booking.Status,
					booking.Period,
					business.TimeZoneId,
					CustomerId = customer.Id,
					CustomerName = customer.Name,
					customer.Email,
					Service = service.Name,
					Professional = professional.Name,
					Business = business.Name,
				})
			.ToDictionaryAsync(static s => s.Id, cancellationToken)
			.ConfigureAwait(false);

		return [.. claimed
			.OrderBy(static n => n.ScheduledFor)
			.Select(n =>
			{
				var s = subjects[n.BookingId];
				return new DueNotification(
					n,
					new NotificationSubject(s.Status, s.Period, DateTimeZoneProviders.Tzdb[s.TimeZoneId], s.CustomerId, s.CustomerName, s.Email, s.Service, s.Professional, s.Business));
			})];
	}

	public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
