namespace BookingAgent.Domain.Scheduling;

using NodaTime;

/// <summary>How slots are laid out.</summary>
/// <param name="Granularity">The step between possible start times: 15 minutes gives 09:00, 09:15, 09:30…</param>
/// <param name="MinimumNotice">How far ahead of now the earliest bookable start is.</param>
public sealed record SlotRules(Duration Granularity, Duration MinimumNotice);

/// <summary>A start time someone can book.</summary>
/// <param name="Period">When the appointment would happen, as instants: its length is real time.</param>
/// <param name="LocalStart">Its start on the business's clock, what a customer is shown.</param>
public sealed record AvailableSlot(Interval Period, LocalDateTime LocalStart);

/// <summary>Everything the slots of one professional and one service depend on.</summary>
/// <param name="Schedule">The professional's weekly hours.</param>
/// <param name="Zone">The business's time zone: the schedule's local times are on its clock.</param>
/// <param name="Dates">The days to look at, on the business's calendar.</param>
/// <param name="ServiceDuration">How long the service takes.</param>
/// <param name="Busy">Periods already taken: bookings, holds and time off.</param>
/// <param name="Now">The current instant.</param>
/// <param name="Rules">Granularity and notice.</param>
public sealed record AvailabilityRequest(
	WeeklySchedule Schedule,
	DateTimeZone Zone,
	DateInterval Dates,
	Duration ServiceDuration,
	IReadOnlyCollection<Interval> Busy,
	Instant Now,
	SlotRules Rules);

/// <summary>
/// The free start times of a service with a professional. Start times are laid out on the business's clock
/// (09:00, 09:30…), and an appointment's length is real time, so the two daylight-saving transitions behave
/// the way a receptionist would expect: an hour that does not exist (the clock jumps from 02:00 to 03:00) is
/// never offered, and an hour that happens twice (03:00 back to 02:00) is offered once, at its first
/// occurrence, so a customer is never shown two identical times.
/// </summary>
public static class Availability
{
	/// <summary>The longest range one request may cover: a search is for the coming weeks, not a year.</summary>
	public const int MaxDays = 62;

	/// <summary>Finds the free slots.</summary>
	/// <param name="request">What to look for.</param>
	/// <returns>The slots, in chronological order.</returns>
	/// <exception cref="ArgumentException">A duration or granularity that is not positive, or too many days.</exception>
	public static IReadOnlyList<AvailableSlot> Find(AvailabilityRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		Validate(request);

		var earliest = request.Now + request.Rules.MinimumNotice;
		var busy = request.Busy.OrderBy(static b => b.Start).ToList();
		var slots = new List<AvailableSlot>();

		foreach (var date in request.Dates)
		{
			foreach (var stretch in request.Schedule.On(date.DayOfWeek))
			{
				// The stretch's real end: on a transition day it is as long as the clock says, not as the hours say.
				var stretchEnd = request.Zone.AtLeniently(date.At(stretch.End)).ToInstant();

				for (var local = date.At(stretch.Start); local < date.At(stretch.End); local += Period.FromTicks(request.Rules.Granularity.BclCompatibleTicks))
				{
					var mapping = request.Zone.MapLocal(local);
					if (mapping.Count == 0)
					{
						// Skipped by the clock: this local time never happens on that day.
						continue;
					}

					var start = mapping.First().ToInstant();
					var period = new Interval(start, start + request.ServiceDuration);
					if (period.End > stretchEnd || start < earliest || Overlaps(period, busy))
					{
						continue;
					}

					slots.Add(new AvailableSlot(period, local));
				}
			}
		}

		return slots;
	}

	// Periods are half-open: an appointment ending at 10:00 leaves 10:00 free.
	private static bool Overlaps(Interval period, List<Interval> busy)
	{
		foreach (var taken in busy)
		{
			if (taken.Start >= period.End)
			{
				return false;
			}

			if (taken.End > period.Start)
			{
				return true;
			}
		}

		return false;
	}

	private static void Validate(AvailabilityRequest request)
	{
		if (request.ServiceDuration <= Duration.Zero)
		{
			throw new ArgumentException("A service lasts some time.", nameof(request));
		}

		if (request.Rules.Granularity <= Duration.Zero)
		{
			throw new ArgumentException("Slots need a positive granularity.", nameof(request));
		}

		if (request.Rules.MinimumNotice < Duration.Zero)
		{
			throw new ArgumentException("The minimum notice cannot be negative.", nameof(request));
		}

		if (Period.Between(request.Dates.Start, request.Dates.End, PeriodUnits.Days).Days >= MaxDays)
		{
			throw new ArgumentException($"Look at most {MaxDays} days ahead at a time.", nameof(request));
		}
	}
}
