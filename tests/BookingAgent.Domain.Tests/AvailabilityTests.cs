namespace BookingAgent.Domain.Tests;

using BookingAgent.Domain.Scheduling;
using NodaTime;

public sealed class AvailabilityTests
{
	// Managua has no daylight saving: UTC-6 all year. The demo clinic's zone.
	private static readonly DateTimeZone Managua = DateTimeZoneProviders.Tzdb["America/Managua"];

	// Madrid does: in 2026 the clock jumps 02:00 → 03:00 on 29 March and falls back 03:00 → 02:00 on 25 October.
	private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

	private static readonly LocalDate Monday = new(2026, 10, 26);

	private static readonly SlotRules HalfHourly = new(Duration.FromMinutes(30), Duration.Zero);

	private static readonly Instant LongAgo = Instant.FromUtc(2026, 1, 1, 0, 0);

	private static WeeklySchedule Hours(params (IsoDayOfWeek Day, int From, int To)[] stretches) =>
		new(stretches.Select(static s => new WorkingHours(s.Day, new LocalTime(s.From, 0), new LocalTime(s.To, 0))));

	private static IReadOnlyList<AvailableSlot> Find(
		WeeklySchedule schedule,
		LocalDate from,
		int minutes = 60,
		DateTimeZone? zone = null,
		Interval[]? busy = null,
		Instant? now = null,
		LocalDate? to = null,
		SlotRules? rules = null) =>
		Availability.Find(new AvailabilityRequest(
			schedule,
			zone ?? Managua,
			new DateInterval(from, to ?? from),
			Duration.FromMinutes(minutes),
			busy ?? [],
			now ?? LongAgo,
			rules ?? HalfHourly));

	private static string[] Times(IEnumerable<AvailableSlot> slots) => [.. slots.Select(static s => s.LocalStart.ToString("HH:mm", null))];

	private static Interval At(LocalDate date, int fromHour, int fromMinute, int toHour, int toMinute, DateTimeZone? zone = null) =>
		new((zone ?? Managua).AtStrictly(date.At(new LocalTime(fromHour, fromMinute))).ToInstant(),
			(zone ?? Managua).AtStrictly(date.At(new LocalTime(toHour, toMinute))).ToInstant());

	[Fact]
	public void Slots_start_on_the_granularity_and_end_inside_the_stretch()
	{
		var slots = Find(Hours((IsoDayOfWeek.Monday, 9, 12)), Monday);

		Assert.Equal(["09:00", "09:30", "10:00", "10:30", "11:00"], Times(slots));
		Assert.All(slots, static s => Assert.Equal(Duration.FromHours(1), s.Period.Duration));
	}

	[Fact]
	public void Lunch_between_two_stretches_is_never_offered()
	{
		var slots = Find(Hours((IsoDayOfWeek.Monday, 9, 11), (IsoDayOfWeek.Monday, 14, 16)), Monday);

		Assert.Equal(["09:00", "09:30", "10:00", "14:00", "14:30", "15:00"], Times(slots));
	}

	[Fact]
	public void A_day_without_hours_has_no_slots()
	{
		Assert.Empty(Find(Hours((IsoDayOfWeek.Tuesday, 9, 17)), Monday));
	}

	[Fact]
	public void A_taken_period_removes_only_the_slots_it_overlaps_and_its_end_is_free_again()
	{
		var slots = Find(Hours((IsoDayOfWeek.Monday, 9, 12)), Monday, busy: [At(Monday, 9, 30, 10, 0)]);

		// 09:00 and 09:30 would run into 09:30-10:00; 10:00 starts exactly when it ends.
		Assert.Equal(["10:00", "10:30", "11:00"], Times(slots));
	}

	[Fact]
	public void Time_off_spanning_days_closes_all_of_them()
	{
		var schedule = Hours((IsoDayOfWeek.Monday, 9, 12), (IsoDayOfWeek.Tuesday, 9, 12), (IsoDayOfWeek.Wednesday, 9, 12));
		var vacation = new Interval(At(Monday, 0, 0, 1, 0).Start, At(Monday.PlusDays(2), 0, 0, 1, 0).Start);

		var slots = Find(schedule, Monday, busy: [vacation], to: Monday.PlusDays(2));

		Assert.All(slots, static s => Assert.Equal(IsoDayOfWeek.Wednesday, s.LocalStart.DayOfWeek));
		Assert.Equal(5, slots.Count);
	}

	[Fact]
	public void Nothing_starts_sooner_than_the_minimum_notice()
	{
		var now = At(Monday, 9, 10, 9, 11).Start;

		var slots = Find(Hours((IsoDayOfWeek.Monday, 9, 12)), Monday, now: now, rules: HalfHourly with { MinimumNotice = Duration.FromHours(1) });

		Assert.Equal(["10:30", "11:00"], Times(slots));
	}

	[Fact]
	public void Local_hours_are_the_business_clock()
	{
		var slot = Find(Hours((IsoDayOfWeek.Monday, 9, 10)), Monday).Single();

		// Managua is UTC-6.
		Assert.Equal(Instant.FromUtc(2026, 10, 26, 15, 0), slot.Period.Start);
	}

	[Fact]
	public void The_hour_the_clock_skips_in_spring_is_never_offered_and_appointments_last_real_time()
	{
		var springForward = new LocalDate(2026, 3, 29);
		var night = Hours((IsoDayOfWeek.Sunday, 1, 5));

		var slots = Find(night, springForward, minutes: 30, zone: Madrid);

		Assert.DoesNotContain(slots, static s => s.LocalStart.Hour == 2);
		Assert.Equal(["01:00", "01:30", "03:00", "03:30", "04:00", "04:30"], Times(slots));
		Assert.All(slots, static s => Assert.Equal(Duration.FromMinutes(30), s.Period.Duration));

		// 01:30 CET runs into 03:00 CEST: thirty real minutes.
		Assert.Equal(Madrid.AtStrictly(springForward.At(new LocalTime(3, 0))).ToInstant(), slots[1].Period.End);
	}

	[Fact]
	public void The_hour_the_clock_repeats_in_autumn_is_offered_once()
	{
		var fallBack = new LocalDate(2026, 10, 25);
		var night = Hours((IsoDayOfWeek.Sunday, 1, 4));

		var slots = Find(night, fallBack, minutes: 30, zone: Madrid);

		Assert.Equal(["01:00", "01:30", "02:00", "02:30", "03:00", "03:30"], Times(slots));

		// 02:00 and 02:30 happen twice; the slot is the first, in summer time (+02:00).
		Assert.Equal(Instant.FromUtc(2026, 10, 25, 0, 0), slots[2].Period.Start);
		Assert.All(slots.Zip(slots.Skip(1)), static pair => Assert.True(pair.First.Period.Start < pair.Second.Period.Start));
	}

	[Fact]
	public void A_service_longer_than_the_stretch_never_fits()
	{
		Assert.Empty(Find(Hours((IsoDayOfWeek.Monday, 9, 10)), Monday, minutes: 90));
	}

	[Fact]
	public void Bad_requests_are_refused()
	{
		var schedule = Hours((IsoDayOfWeek.Monday, 9, 12));

		Assert.Throws<ArgumentException>(() => Find(schedule, Monday, minutes: 0));
		Assert.Throws<ArgumentException>(() => Find(schedule, Monday, rules: HalfHourly with { Granularity = Duration.Zero }));
		Assert.Throws<ArgumentException>(() => Find(schedule, Monday, to: Monday.PlusDays(Availability.MaxDays)));
	}

	[Fact]
	public void A_schedule_refuses_backwards_or_overlapping_stretches()
	{
		Assert.Throws<ArgumentException>(() => Hours((IsoDayOfWeek.Monday, 12, 9)));
		Assert.Throws<ArgumentException>(() => Hours((IsoDayOfWeek.Monday, 9, 12), (IsoDayOfWeek.Monday, 11, 14)));
		Assert.NotNull(Hours((IsoDayOfWeek.Monday, 9, 12), (IsoDayOfWeek.Monday, 12, 14)));
	}
}
