namespace BookingAgent.Application.Tests;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

public sealed class NotificationTests
{
	private static readonly DateTimeZone Managua = DateTimeZoneProviders.Tzdb["America/Managua"];

	// Monday 2 Nov 2026, 9:00 in Managua (UTC-6), 45 minutes.
	private static readonly Interval Appointment = new(Instant.FromUtc(2026, 11, 2, 15, 0), Instant.FromUtc(2026, 11, 2, 15, 45));

	private static NotificationSubject Subject(BookingStatus status = BookingStatus.Confirmed) =>
		new(status, Appointment, Managua, CustomerId.New(), "Ana Pérez", "ana@ejemplo.com", "Limpieza dental", "Dra. Ana López", "Clínica Demo");

	[Fact]
	public void A_confirmation_days_ahead_is_announced_at_once_and_reminded_a_day_and_two_hours_before_on_every_channel()
	{
		var now = Appointment.Start - Duration.FromDays(3);

		var planned = NotificationPlanner.Plan(new BookingChanged(BookingId.New(), BookingChange.Confirmed, Appointment, now)).ToList();

		Assert.Equal(6, planned.Count);
		foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.InApp })
		{
			Assert.Equal(
				[(NotificationKind.Confirmed, now), (NotificationKind.Reminder, Appointment.Start - Duration.FromHours(24)), (NotificationKind.Reminder, Appointment.Start - Duration.FromHours(2))],
				planned.Where(n => n.Channel == channel).Select(static n => (n.Kind, n.ScheduledFor)));
		}
	}

	[Fact]
	public void A_booking_made_for_the_same_afternoon_gets_no_reminder_that_would_already_be_late()
	{
		var now = Appointment.Start - Duration.FromHours(3);

		var reminders = NotificationPlanner.Plan(new BookingChanged(BookingId.New(), BookingChange.Rescheduled, Appointment, now))
			.Where(static n => n.Kind == NotificationKind.Reminder)
			.Select(static n => n.ScheduledFor)
			.Distinct();

		Assert.Equal([Appointment.Start - Duration.FromHours(2)], reminders);
	}

	[Fact]
	public void A_cancellation_is_announced_on_every_channel_and_reminded_of_never()
	{
		var planned = NotificationPlanner.Plan(new BookingChanged(BookingId.New(), BookingChange.Cancelled, Appointment, Appointment.Start - Duration.FromDays(2))).ToList();

		Assert.All(planned, static n => Assert.Equal(NotificationKind.Cancelled, n.Kind));
		Assert.Equal(NotificationPlanner.Channels, planned.Select(static n => n.Channel));
	}

	[Fact]
	public void Messages_say_when_on_the_business_clock_in_spanish()
	{
		var notification = Notification.Schedule(BookingId.New(), NotificationChannel.Email, NotificationKind.Confirmed, Appointment.Start);

		var message = NotificationTemplates.Render(notification, Subject());

		Assert.Equal("Cita confirmada: Limpieza dental, lunes 2 de noviembre a las 9:00", message.Subject);
		Assert.StartsWith("Hola, Ana Pérez: tu cita de Limpieza dental con Dra. Ana López quedó confirmada para el lunes 2 de noviembre a las 9:00 (hora de Managua).", message.Body, StringComparison.Ordinal);
		Assert.Equal((notification.Id, "ana@ejemplo.com"), (message.NotificationId, message.ToEmail));
	}

	[Fact]
	public async Task A_pass_sends_each_notification_on_its_channel_and_saves_each_outcome()
	{
		var email = new FakeChannel(NotificationChannel.Email);
		var inApp = new FakeChannel(NotificationChannel.InApp);
		var outbox = new FakeOutbox(Due(NotificationChannel.Email), Due(NotificationChannel.InApp));

		var report = await Dispatcher(outbox, email, inApp).HandleAsync(CancellationToken.None);

		Assert.Equal(new DispatchReport(Sent: 2, Skipped: 0, Retrying: 0, Failed: 0), report);
		Assert.Single(email.Sent);
		Assert.Single(inApp.Sent);
		Assert.Equal(2, outbox.Saves);
		Assert.All(outbox.Due, static d => Assert.Equal(NotificationStatus.Sent, d.Notification.Status));
	}

	[Fact]
	public async Task A_failing_channel_does_not_hold_back_the_others_and_its_notification_is_retried_later()
	{
		var email = new FakeChannel(NotificationChannel.Email) { Failure = new IOException("Connection refused") };
		var inApp = new FakeChannel(NotificationChannel.InApp);
		var outbox = new FakeOutbox(Due(NotificationChannel.Email), Due(NotificationChannel.InApp));
		var clock = new FakeClock(Appointment.Start - Duration.FromDays(1));

		var report = await Dispatcher(outbox, email, inApp, clock).HandleAsync(CancellationToken.None);

		Assert.Equal(new DispatchReport(Sent: 1, Skipped: 0, Retrying: 1, Failed: 0), report);
		var failed = outbox.Due[0].Notification;
		Assert.Equal((NotificationStatus.Pending, 1, "IOException: Connection refused"), (failed.Status, failed.Attempts, failed.Detail));
		Assert.Equal(clock.GetCurrentInstant() + Duration.FromSeconds(30), failed.NextAttemptAt);
	}

	[Fact]
	public async Task A_reminder_for_a_booking_no_longer_on_is_skipped_but_its_cancellation_notice_goes_out()
	{
		var email = new FakeChannel(NotificationChannel.Email);
		var outbox = new FakeOutbox(
			Due(NotificationChannel.Email, NotificationKind.Reminder, BookingStatus.Cancelled),
			Due(NotificationChannel.Email, NotificationKind.Cancelled, BookingStatus.Cancelled));

		var report = await Dispatcher(outbox, email).HandleAsync(CancellationToken.None);

		Assert.Equal(new DispatchReport(Sent: 1, Skipped: 1, Retrying: 0, Failed: 0), report);
		Assert.StartsWith("Cita cancelada", Assert.Single(email.Sent).Subject, StringComparison.Ordinal);
		Assert.Equal("The booking is no longer confirmed.", outbox.Due[0].Notification.Detail);
	}

	[Fact]
	public async Task A_notification_for_a_channel_nobody_registered_fails_instead_of_disappearing()
	{
		var outbox = new FakeOutbox(Due(NotificationChannel.InApp));

		var report = await Dispatcher(outbox, new FakeChannel(NotificationChannel.Email)).HandleAsync(CancellationToken.None);

		Assert.Equal(1, report.Retrying);
		Assert.Contains("No InApp channel", outbox.Due[0].Notification.Detail, StringComparison.Ordinal);
	}

	private static DueNotification Due(NotificationChannel channel, NotificationKind kind = NotificationKind.Confirmed, BookingStatus status = BookingStatus.Confirmed) =>
		new(Notification.Schedule(BookingId.New(), channel, kind, Appointment.Start - Duration.FromDays(1)), Subject(status));

	private static DispatchNotifications Dispatcher(FakeOutbox outbox, FakeChannel email, FakeChannel? inApp = null, FakeClock? clock = null) =>
		new(
			outbox,
			inApp is null ? [email] : [email, inApp],
			clock ?? new FakeClock(Appointment.Start - Duration.FromDays(1)),
			Options.Create(new NotificationOptions()));

	private sealed class FakeOutbox(params DueNotification[] due) : INotificationOutbox
	{
		public IReadOnlyList<DueNotification> Due { get; } = due;

		public int Saves { get; private set; }

		public Task<IReadOnlyList<DueNotification>> ClaimDueAsync(Instant now, int limit, Duration lease, CancellationToken cancellationToken) =>
			Task.FromResult(Due);

		public Task SaveChangesAsync(CancellationToken cancellationToken)
		{
			Saves++;
			return Task.CompletedTask;
		}
	}

	private sealed class FakeChannel(NotificationChannel channel) : INotificationChannel
	{
		public List<OutgoingMessage> Sent { get; } = [];

		public Exception? Failure { get; init; }

		public NotificationChannel Channel => channel;

		public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
		{
			if (Failure is not null)
			{
				throw Failure;
			}

			Sent.Add(message);
			return Task.CompletedTask;
		}
	}
}
