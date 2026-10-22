namespace BookingAgent.Domain.Scheduling;

using NodaTime;

/// <summary>One stretch of work on a weekday, in the business's local time: "Monday 09:00-13:00".</summary>
/// <param name="Day">The weekday.</param>
/// <param name="Start">When it starts, local time.</param>
/// <param name="End">When it ends, local time, after <paramref name="Start"/> on the same day.</param>
public sealed record WorkingHours(IsoDayOfWeek Day, LocalTime Start, LocalTime End);

/// <summary>
/// A professional's weekly hours: any number of stretches per day (a morning and an afternoon, with lunch in
/// between), none of them overlapping, none crossing midnight. A night shift is two stretches on two days.
/// </summary>
public sealed class WeeklySchedule
{
	private readonly Dictionary<IsoDayOfWeek, IReadOnlyList<WorkingHours>> _byDay;

	/// <summary>Creates the schedule.</summary>
	/// <param name="hours">The stretches, in any order.</param>
	/// <exception cref="ArgumentException">A stretch ends before it starts, or two overlap on the same day.</exception>
	public WeeklySchedule(IEnumerable<WorkingHours> hours)
	{
		ArgumentNullException.ThrowIfNull(hours);

		var all = hours.ToList();
		foreach (var stretch in all)
		{
			if (stretch.Day == IsoDayOfWeek.None)
			{
				throw new ArgumentException("A stretch needs a weekday.", nameof(hours));
			}

			if (stretch.End <= stretch.Start)
			{
				throw new ArgumentException($"{stretch.Day} {stretch.Start:HH:mm}-{stretch.End:HH:mm} ends before it starts.", nameof(hours));
			}
		}

		_byDay = all
			.GroupBy(static h => h.Day)
			.ToDictionary(static g => g.Key, static g => (IReadOnlyList<WorkingHours>)[.. g.OrderBy(static h => h.Start)]);

		foreach (var (day, stretches) in _byDay)
		{
			for (var i = 1; i < stretches.Count; i++)
			{
				if (stretches[i].Start < stretches[i - 1].End)
				{
					throw new ArgumentException($"Two stretches overlap on {day}.", nameof(hours));
				}
			}
		}
	}

	/// <summary>The stretches of one weekday, earliest first.</summary>
	/// <param name="day">The weekday.</param>
	/// <returns>The stretches; empty on a day off.</returns>
	public IReadOnlyList<WorkingHours> On(IsoDayOfWeek day) => _byDay.TryGetValue(day, out var stretches) ? stretches : [];
}
