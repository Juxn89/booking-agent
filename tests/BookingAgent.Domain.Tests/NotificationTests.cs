namespace BookingAgent.Domain.Tests;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using NodaTime;

public sealed class NotificationTests
{
	private static readonly Instant Now = Instant.FromUtc(2026, 10, 26, 14, 0);
	private static readonly Interval Tomorrow = new(Instant.FromUtc(2026, 10, 27, 15, 0), Instant.FromUtc(2026, 10, 27, 15, 30));
	private static readonly Duration FiveMinutes = Duration.FromMinutes(5);
	private static readonly Duration ThirtySeconds = Duration.FromSeconds(30);

	private static (Booking Booking, string Token) Hold(BookingId? replaces = null) =>
		Booking.Hold(ProfessionalId.New(), ServiceId.New(), CustomerId.New(), Tomorrow, Now, FiveMinutes, replaces);

	[Fact]
	public void Confirming_records_the_change_once_and_a_repeated_confirmation_records_nothing()
	{
		var (booking, token) = Hold();

		booking.Confirm(token, Now);
		booking.Confirm(token, Now + Duration.FromSeconds(1));

		var change = Assert.Single(booking.Changes);
		Assert.Equal(new BookingChanged(booking.Id, BookingChange.Confirmed, Tomorrow, Now), change);
	}

	[Fact]
	public void Confirming_a_reschedule_records_a_move_and_the_replaced_booking_records_nothing()
	{
		var (original, originalToken) = Hold();
		original.Confirm(originalToken, Now);
		original.ClearChanges();
		var (move, moveToken) = Hold(replaces: original.Id);

		move.Confirm(moveToken, Now);
		original.ReplaceWith(move, Now);

		Assert.Equal(BookingChange.Rescheduled, Assert.Single(move.Changes).Change);
		Assert.Empty(original.Changes);
	}

	[Fact]
	public void Cancelling_an_appointment_records_it_but_releasing_or_expiring_a_hold_does_not()
	{
		var (confirmed, token) = Hold();
		confirmed.Confirm(token, Now);
		confirmed.ClearChanges();
		var (released, _) = Hold();
		var (expired, _) = Hold();

		confirmed.Cancel(Now, Duration.FromHours(2));
		released.Cancel(Now, Duration.FromHours(2));
		expired.Expire(Now + FiveMinutes);

		Assert.Equal(BookingChange.Cancelled, Assert.Single(confirmed.Changes).Change);
		Assert.Empty(released.Changes);
		Assert.Empty(expired.Changes);
	}

	[Fact]
	public void A_failing_notification_waits_twice_as_long_each_time_and_is_given_up_after_the_last_attempt()
	{
		var notification = Notification.Schedule(BookingId.New(), NotificationChannel.Email, NotificationKind.Confirmed, Now);

		var waits = Enumerable.Range(0, 3).Select(_ =>
		{
			Assert.True(notification.Fail("smtp down", Now, maxAttempts: 4, ThirtySeconds));
			return notification.NextAttemptAt - Now;
		}).ToList();
		var retriedAgain = notification.Fail("smtp down", Now, maxAttempts: 4, ThirtySeconds);

		Assert.Equal([ThirtySeconds, Duration.FromMinutes(1), Duration.FromMinutes(2)], waits);
		Assert.False(retriedAgain);
		Assert.Equal((NotificationStatus.Failed, 4, "smtp down"), (notification.Status, notification.Attempts, notification.Detail));
	}

	[Fact]
	public void A_sent_notification_forgets_its_last_error_and_a_long_error_is_cut()
	{
		var notification = Notification.Schedule(BookingId.New(), NotificationChannel.Email, NotificationKind.Reminder, Now);

		notification.Fail(new string('x', Notification.MaxDetailLength * 2), Now, maxAttempts: 5, ThirtySeconds);
		Assert.Equal(Notification.MaxDetailLength, notification.Detail!.Length);
		notification.MarkSent(Now + Duration.FromMinutes(1));

		Assert.Equal((NotificationStatus.Sent, 2, null, Now + Duration.FromMinutes(1)), (notification.Status, notification.Attempts, notification.Detail, notification.SentAt));
	}
}
