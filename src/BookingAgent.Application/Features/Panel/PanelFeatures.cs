namespace BookingAgent.Application.Features.Panel;

using System.Text.Json;
using BookingAgent.Application.Common;
using BookingAgent.Domain;
using BookingAgent.Domain.Assistant;
using BookingAgent.Domain.Bookings;
using Microsoft.Extensions.AI;
using NodaTime;

/// <summary>
/// Tells the business's panel that bookings changed, as soon as the change is saved: held, confirmed, moved,
/// cancelled or expired. A failure to tell is logged, never the booking's failure.
/// </summary>
public interface ICalendarUpdates
{
	/// <summary>Announces changed bookings.</summary>
	/// <param name="business">The business they belong to.</param>
	/// <param name="bookingIds">The bookings.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	Task PublishAsync(BusinessId business, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken);
}

/// <summary>A professional's column in the panel.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
/// <param name="Specialty">The specialty.</param>
public sealed record PanelProfessional(Guid Id, string Name, string Specialty);

/// <summary>A booking in the panel's calendar.</summary>
/// <param name="Id">The booking.</param>
/// <param name="ProfessionalId">With whom.</param>
/// <param name="ServiceName">For what.</param>
/// <param name="CustomerName">For whom.</param>
/// <param name="CustomerEmail">Their email.</param>
/// <param name="LocalStart">When it starts, on the business's clock.</param>
/// <param name="LocalEnd">When it ends.</param>
/// <param name="Status">Held or confirmed.</param>
/// <param name="ViaAssistant">Whether the assistant held it (its steps can be looked at).</param>
public sealed record PanelBooking(
	Guid Id,
	Guid ProfessionalId,
	string ServiceName,
	string CustomerName,
	string CustomerEmail,
	LocalDateTime LocalStart,
	LocalDateTime LocalEnd,
	BookingStatus Status,
	bool ViaAssistant);

/// <summary>A stretch of days of the panel's calendar.</summary>
/// <param name="From">The first day.</param>
/// <param name="Days">How many days.</param>
/// <param name="TimeZone">The business's clock.</param>
/// <param name="Professionals">The columns.</param>
/// <param name="Bookings">The held and confirmed bookings in those days.</param>
public sealed record PanelCalendar(LocalDate From, int Days, string TimeZone, IReadOnlyList<PanelProfessional> Professionals, IReadOnlyList<PanelBooking> Bookings);

/// <summary>A message of the conversation behind a booking.</summary>
/// <param name="Role">"customer" or "assistant".</param>
/// <param name="Text">What was said.</param>
public sealed record TraceMessage(string Role, string Text);

/// <summary>A step of the assistant behind a booking.</summary>
/// <param name="Turn">The customer's message it answered.</param>
/// <param name="Ordinal">Its place in the turn.</param>
/// <param name="Kind">Model or tool.</param>
/// <param name="Tool">The tool, for a tool step.</param>
/// <param name="Input">The tool's input, JSON, emails masked.</param>
/// <param name="Output">The tool's output, JSON, emails masked.</param>
/// <param name="LatencyMs">How long it took.</param>
/// <param name="InputTokens">Input tokens of a model step.</param>
/// <param name="OutputTokens">Output tokens of a model step.</param>
/// <param name="CostUsd">Its cost.</param>
public sealed record TraceStep(int Turn, int Ordinal, AgentStepKind Kind, string? Tool, string? Input, string? Output, int LatencyMs, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>The "why" of a booking the assistant held: the conversation and every step, with what it cost.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Transcript">What the customer and the assistant said.</param>
/// <param name="Steps">The model and tool calls.</param>
/// <param name="CostUsd">The conversation's cost.</param>
public sealed record BookingTrace(Guid BookingId, Guid ConversationId, IReadOnlyList<TraceMessage> Transcript, IReadOnlyList<TraceStep> Steps, decimal CostUsd);

/// <summary>A booking row with its names, as stored.</summary>
/// <param name="Booking">The booking.</param>
/// <param name="ServiceName">The service.</param>
/// <param name="CustomerName">The customer.</param>
/// <param name="CustomerEmail">Their email.</param>
public sealed record PanelRow(Booking Booking, string ServiceName, string CustomerName, string CustomerEmail);

/// <summary>Reads what the panel shows.</summary>
public interface IPanelReader
{
	/// <summary>The business's clock, or <see langword="null"/> when there is no such business.</summary>
	/// <param name="business">The business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The zone.</returns>
	Task<DateTimeZone?> ZoneAsync(BusinessId business, CancellationToken cancellationToken);

	/// <summary>The business's professionals, by name.</summary>
	/// <param name="business">The business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The professionals.</returns>
	Task<IReadOnlyList<PanelProfessional>> ProfessionalsAsync(BusinessId business, CancellationToken cancellationToken);

	/// <summary>The held and confirmed bookings of the business overlapping a period.</summary>
	/// <param name="business">The business.</param>
	/// <param name="period">The period.</param>
	/// <param name="now">The current instant: a hold that ran out is left out.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The bookings.</returns>
	Task<IReadOnlyList<PanelRow>> BookingsAsync(BusinessId business, Interval period, Instant now, CancellationToken cancellationToken);

	/// <summary>Of these bookings, the ones the assistant held, by the conversation that held them.</summary>
	/// <param name="bookingIds">The bookings.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>Booking id → conversation id.</returns>
	Task<IReadOnlyDictionary<Guid, Guid>> HeldByAssistantAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken);

	/// <summary>A conversation of the business with its steps.</summary>
	/// <param name="business">The business.</param>
	/// <param name="conversationId">The conversation.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The conversation and its steps in order, or <see langword="null"/>.</returns>
	Task<(AgentConversation Conversation, IReadOnlyList<AgentStep> Steps)?> ConversationAsync(BusinessId business, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>The panel error codes (docs/errors.md).</summary>
public static class PanelErrors
{
	/// <summary>The range is not a plausible stretch of days.</summary>
	public const string BadRange = "panel.bad_range";

	/// <summary>No business, or no conversation behind that booking.</summary>
	public const string NotFound = "panel.not_found";
}

/// <summary>
/// The business's calendar, a week at a time: who has what and when, held or confirmed, and which bookings the
/// assistant made. The panel reloads it whenever <see cref="ICalendarUpdates"/> announces a change.
/// </summary>
/// <param name="reader">The panel's data.</param>
/// <param name="clock">The clock.</param>
public sealed class GetPanelCalendar(IPanelReader reader, IClock clock)
{
	/// <summary>The most days in one request: a month.</summary>
	public const int MaxDays = 31;

	/// <summary>Reads it.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="from">The first day, on the business's calendar.</param>
	/// <param name="days">How many days.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The calendar.</returns>
	public async Task<Result<PanelCalendar>> HandleAsync(Guid businessId, LocalDate from, int days, CancellationToken cancellationToken)
	{
		if (days is < 1 or > MaxDays)
		{
			return Error.Validation(PanelErrors.BadRange, $"Ask for 1 to {MaxDays} days.");
		}

		var business = new BusinessId(businessId);
		if (await reader.ZoneAsync(business, cancellationToken).ConfigureAwait(false) is not { } zone)
		{
			return Error.NotFound(PanelErrors.NotFound, $"There is no business {businessId}.");
		}

		var period = new Interval(from.AtStartOfDayInZone(zone).ToInstant(), from.PlusDays(days).AtStartOfDayInZone(zone).ToInstant());
		var professionals = await reader.ProfessionalsAsync(business, cancellationToken).ConfigureAwait(false);
		var rows = await reader.BookingsAsync(business, period, clock.GetCurrentInstant(), cancellationToken).ConfigureAwait(false);
		var viaAssistant = await reader.HeldByAssistantAsync([.. rows.Select(static r => r.Booking.Id.Value)], cancellationToken).ConfigureAwait(false);

		return new PanelCalendar(
			from,
			days,
			zone.Id,
			professionals,
			[.. rows.Select(r => new PanelBooking(
				r.Booking.Id.Value,
				r.Booking.ProfessionalId.Value,
				r.ServiceName,
				r.CustomerName,
				r.CustomerEmail,
				r.Booking.Period.Start.InZone(zone).LocalDateTime,
				r.Booking.Period.End.InZone(zone).LocalDateTime,
				r.Booking.Status,
				viaAssistant.ContainsKey(r.Booking.Id.Value)))]);
	}
}

/// <summary>The conversation and the steps behind a booking the assistant held.</summary>
/// <param name="reader">The panel's data.</param>
public sealed class GetBookingTrace(IPanelReader reader)
{
	/// <summary>Reads it.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="bookingId">The booking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The trace, or not found when the booking was not made by the assistant.</returns>
	public async Task<Result<BookingTrace>> HandleAsync(Guid businessId, Guid bookingId, CancellationToken cancellationToken)
	{
		var held = await reader.HeldByAssistantAsync([bookingId], cancellationToken).ConfigureAwait(false);
		if (!held.TryGetValue(bookingId, out var conversationId)
			|| await reader.ConversationAsync(new BusinessId(businessId), conversationId, cancellationToken).ConfigureAwait(false) is not { } found)
		{
			return Error.NotFound(PanelErrors.NotFound, "The assistant did not make this booking.");
		}

		var (conversation, steps) = found;
		var history = JsonSerializer.Deserialize<List<ChatMessage>>(conversation.History, AIJsonUtilities.DefaultOptions) ?? [];
		var transcript = history
			.Where(static m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant) && !string.IsNullOrWhiteSpace(m.Text))
			.Select(static m => new TraceMessage(m.Role == ChatRole.User ? "customer" : "assistant", m.Text))
			.ToList();
		return new BookingTrace(
			bookingId,
			conversation.Id,
			transcript,
			[.. steps.Select(static s => new TraceStep(s.Turn, s.Ordinal, s.Kind, s.ToolName, s.Input, s.Output, s.LatencyMs, s.InputTokens, s.OutputTokens, s.CostUsd))],
			conversation.CostUsd);
	}
}
