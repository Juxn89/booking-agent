namespace BookingAgent.Application.Features.Notifications;

using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using NodaTime;

/// <summary>
/// Which notifications a change to a booking calls for: a notice at once on every channel and, for an appointment
/// that is on, a reminder a day and two hours before it (plan §2). Pure: the repository calls it while saving the
/// change, so the notifications are written in the same transaction.
/// </summary>
public static class NotificationPlanner
{
	/// <summary>Every channel a customer is reached on. WhatsApp would be one more adapter (plan §2, not in the MVP).</summary>
	public static readonly IReadOnlyList<NotificationChannel> Channels = [NotificationChannel.Email, NotificationChannel.InApp];

	/// <summary>How long before the appointment the reminders go out: the day before, and two hours before.</summary>
	public static readonly IReadOnlyList<Duration> ReminderLeads = [Duration.FromHours(24), Duration.FromHours(2)];

	/// <summary>The notifications for a change.</summary>
	/// <param name="change">What happened.</param>
	/// <returns>The notifications, pending. A reminder whose moment has already passed is not scheduled.</returns>
	public static IEnumerable<Notification> Plan(BookingChanged change)
	{
		ArgumentNullException.ThrowIfNull(change);
		var notice = change.Change switch
		{
			BookingChange.Confirmed => NotificationKind.Confirmed,
			BookingChange.Rescheduled => NotificationKind.Rescheduled,
			BookingChange.Cancelled => NotificationKind.Cancelled,
			_ => throw new ArgumentOutOfRangeException(nameof(change), change.Change, "Unknown change."),
		};

		var reminders = change.Change == BookingChange.Cancelled
			? []
			: ReminderLeads.Select(lead => change.Period.Start - lead).Where(at => at > change.At).ToList();

		foreach (var channel in Channels)
		{
			yield return Notification.Schedule(change.Booking, channel, notice, change.At);
			foreach (var at in reminders)
			{
				yield return Notification.Schedule(change.Booking, channel, NotificationKind.Reminder, at);
			}
		}
	}
}
