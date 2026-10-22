namespace BookingAgent.Application.Features.Availability;

using BookingAgent.Application.Common;
using BookingAgent.Domain;
using BookingAgent.Domain.Scheduling;
using NodaTime;

/// <summary>Which slots to look for.</summary>
/// <param name="ProfessionalId">The professional.</param>
/// <param name="ServiceId">The service, which sets the length.</param>
/// <param name="From">The first day, on the business's calendar.</param>
/// <param name="To">The last day, included.</param>
public sealed record AvailabilityQuery(Guid ProfessionalId, Guid ServiceId, LocalDate From, LocalDate To);

/// <summary>What the slots of a professional and a service depend on, as stored.</summary>
/// <param name="Business">The business the professional works for.</param>
/// <param name="Schedule">The professional's weekly hours.</param>
/// <param name="Zone">The business's time zone.</param>
/// <param name="ServiceDuration">How long the service takes.</param>
/// <param name="Busy">Confirmed bookings, holds that have not run out and time off in the period looked at.</param>
/// <param name="Rules">The business's granularity and minimum notice.</param>
public sealed record AvailabilityContext(
	BusinessId Business,
	WeeklySchedule Schedule,
	DateTimeZone Zone,
	Duration ServiceDuration,
	IReadOnlyCollection<Interval> Busy,
	SlotRules Rules);

/// <summary>Loads what availability is computed from.</summary>
public interface IAvailabilitySource
{
	/// <summary>The context for one professional and service over some days.</summary>
	/// <param name="professionalId">The professional.</param>
	/// <param name="serviceId">The service.</param>
	/// <param name="dates">The days, on the business's calendar.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The context, or <see langword="null"/> when the professional does not offer that service.</returns>
	Task<AvailabilityContext?> LoadAsync(Guid professionalId, Guid serviceId, DateInterval dates, CancellationToken cancellationToken);
}

/// <summary>The availability error codes.</summary>
public static class AvailabilityErrors
{
	/// <summary>The professional does not exist, or does not offer the service.</summary>
	public const string NotOffered = "availability.not_offered";

	/// <summary>The range ends before it starts, or is longer than <see cref="Domain.Scheduling.Availability.MaxDays"/>.</summary>
	public const string BadRange = "availability.bad_range";
}

/// <summary>
/// The free start times of a service with a professional: the form and the assistant's
/// <c>check_availability</c> tool both ask this, so neither can offer a time the other would refuse.
/// </summary>
/// <param name="source">Where schedules and bookings come from.</param>
/// <param name="clock">The clock.</param>
public sealed class FindAvailableSlots(IAvailabilitySource source, IClock clock)
{
	/// <summary>Finds them.</summary>
	/// <param name="query">What to look for.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The slots, earliest first, or why there are none to look for.</returns>
	public async Task<Result<IReadOnlyList<AvailableSlot>>> HandleAsync(AvailabilityQuery query, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);
		if (query.To < query.From || Period.Between(query.From, query.To, PeriodUnits.Days).Days >= Availability.MaxDays)
		{
			return Error.Validation(AvailabilityErrors.BadRange, $"Ask for 1 to {Availability.MaxDays} days, the first one before the last.");
		}

		var dates = new DateInterval(query.From, query.To);
		var context = await source.LoadAsync(query.ProfessionalId, query.ServiceId, dates, cancellationToken).ConfigureAwait(false);
		if (context is null)
		{
			return Error.NotFound(AvailabilityErrors.NotOffered, "That professional does not offer that service.");
		}

		return Result<IReadOnlyList<AvailableSlot>>.Success(Availability.Find(new AvailabilityRequest(
			context.Schedule,
			context.Zone,
			dates,
			context.ServiceDuration,
			context.Busy,
			clock.GetCurrentInstant(),
			context.Rules)));
	}
}
