namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Application.Features.Panel;
using BookingAgent.Domain;
using BookingAgent.Domain.Assistant;
using BookingAgent.Domain.Bookings;
using Microsoft.EntityFrameworkCore;
using NodaTime;

/// <summary>What the business's panel shows, read without tracking.</summary>
/// <param name="db">The database.</param>
internal sealed class PanelReader(BookingDbContext db) : IPanelReader
{
	public async Task<DateTimeZone?> ZoneAsync(BusinessId business, CancellationToken cancellationToken)
	{
		var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == business).Select(static b => b.TimeZoneId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		return zone is null ? null : DateTimeZoneProviders.Tzdb[zone];
	}

	public async Task<IReadOnlyList<PanelProfessional>> ProfessionalsAsync(BusinessId business, CancellationToken cancellationToken) =>
		await db.Professionals.AsNoTracking()
			.Where(p => p.BusinessId == business)
			.OrderBy(static p => p.Name)
			.Select(static p => new PanelProfessional(p.Id.Value, p.Name, p.Specialty))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<PanelRow>> BookingsAsync(BusinessId business, Interval period, Instant now, CancellationToken cancellationToken)
	{
		var rows = await (
				from booking in db.Bookings.AsNoTracking()
				where booking.Period.Start < period.End && booking.Period.End > period.Start
					&& (booking.Status == BookingStatus.Confirmed || (booking.Status == BookingStatus.Held && booking.HoldExpiresAt > now))
				join professional in db.Professionals on booking.ProfessionalId equals professional.Id
				where professional.BusinessId == business
				join service in db.Services on booking.ServiceId equals service.Id
				join customer in db.Customers on booking.CustomerId equals customer.Id
				orderby booking.Period.Start
				select new { booking, Service = service.Name, Customer = customer.Name, customer.Email })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. rows.Select(static r => new PanelRow(r.booking, r.Service, r.Customer, r.Email))];
	}

	// A booking the assistant held is the hold_id in the output of a hold_slot or propose_reschedule step.
	public async Task<IReadOnlyDictionary<Guid, Guid>> HeldByAssistantAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken)
	{
		if (bookingIds.Count == 0)
		{
			return new Dictionary<Guid, Guid>();
		}

		var ids = bookingIds.ToArray();
		var rows = await db.Database
			.SqlQuery<HeldBy>($"""
				SELECT (output->>'hold_id')::uuid AS booking_id, conversation_id
				FROM agent_steps
				WHERE tool_name IN ('hold_slot', 'propose_reschedule')
				  AND output ? 'hold_id'
				  AND (output->>'hold_id')::uuid = ANY({ids})
				""")
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return rows.GroupBy(static r => r.BookingId).ToDictionary(static g => g.Key, static g => g.First().ConversationId);
	}

	public async Task<(AgentConversation Conversation, IReadOnlyList<AgentStep> Steps)?> ConversationAsync(BusinessId business, Guid conversationId, CancellationToken cancellationToken)
	{
		var conversation = await db.AgentConversations.AsNoTracking()
			.FirstOrDefaultAsync(c => c.Id == conversationId && c.BusinessId == business, cancellationToken)
			.ConfigureAwait(false);
		if (conversation is null)
		{
			return null;
		}

		var steps = await db.AgentSteps.AsNoTracking()
			.Where(s => s.ConversationId == conversationId)
			.OrderBy(static s => s.Turn).ThenBy(static s => s.Ordinal)
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return (conversation, steps);
	}

	private sealed record HeldBy(Guid BookingId, Guid ConversationId);
}

/// <summary>For hosts without a live panel (the evals, the tests that do not look): changes go unannounced.</summary>
internal sealed class NoCalendarUpdates : ICalendarUpdates
{
	public Task PublishAsync(BusinessId business, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken) => Task.CompletedTask;
}
