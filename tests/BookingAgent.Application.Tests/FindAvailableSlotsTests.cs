namespace BookingAgent.Application.Tests;

using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Domain;
using BookingAgent.Domain.Scheduling;
using NodaTime;
using NodaTime.Testing;

public sealed class FindAvailableSlotsTests
{
	private static readonly Guid Professional = Guid.NewGuid();
	private static readonly Guid Service = Guid.NewGuid();
	private static readonly LocalDate Monday = new(2026, 10, 26);

	private readonly FakeSource _source = new();

	private FindAvailableSlots UseCase(Instant now) => new(_source, new FakeClock(now));

	[Fact]
	public async Task The_slots_come_from_the_stored_schedule_and_skip_what_is_taken()
	{
		var zone = DateTimeZoneProviders.Tzdb["America/Managua"];
		var nineThirty = zone.AtStrictly(Monday.At(new LocalTime(9, 30))).ToInstant();
		_source.Context = new AvailabilityContext(
			BusinessId.New(),
			new WeeklySchedule([new WorkingHours(IsoDayOfWeek.Monday, new LocalTime(9, 0), new LocalTime(11, 0))]),
			zone,
			Duration.FromMinutes(30),
			[new Interval(nineThirty, nineThirty + Duration.FromMinutes(30))],
			new SlotRules(Duration.FromMinutes(30), Duration.Zero));

		var result = await UseCase(Instant.FromUtc(2026, 10, 1, 0, 0)).HandleAsync(new AvailabilityQuery(Professional, Service, Monday, Monday), CancellationToken.None);

		Assert.Equal(["09:00", "10:00", "10:30"], result.Value.Select(static s => s.LocalStart.ToString("HH:mm", null)));
		Assert.Equal((Professional, Service, new DateInterval(Monday, Monday)), _source.Asked);
	}

	[Fact]
	public async Task A_service_the_professional_does_not_offer_is_not_found()
	{
		var result = await UseCase(Instant.FromUtc(2026, 10, 1, 0, 0)).HandleAsync(new AvailabilityQuery(Professional, Service, Monday, Monday), CancellationToken.None);

		Assert.Equal((AvailabilityErrors.NotOffered, ErrorKind.NotFound), (result.Error!.Code, result.Error.Kind));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(Availability.MaxDays)]
	public async Task A_backwards_or_too_long_range_is_refused_before_loading_anything(int days)
	{
		var result = await UseCase(Instant.FromUtc(2026, 10, 1, 0, 0)).HandleAsync(new AvailabilityQuery(Professional, Service, Monday, Monday.PlusDays(days)), CancellationToken.None);

		Assert.Equal(AvailabilityErrors.BadRange, result.Error!.Code);
		Assert.Null(_source.Asked);
	}

	private sealed class FakeSource : IAvailabilitySource
	{
		public AvailabilityContext? Context { get; set; }

		public (Guid, Guid, DateInterval)? Asked { get; private set; }

		public Task<AvailabilityContext?> LoadAsync(Guid professionalId, Guid serviceId, DateInterval dates, CancellationToken cancellationToken)
		{
			Asked = (professionalId, serviceId, dates);
			return Task.FromResult(Context);
		}
	}
}
