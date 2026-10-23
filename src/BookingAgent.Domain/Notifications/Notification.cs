namespace BookingAgent.Domain.Notifications;

using NodaTime;

/// <summary>How a notification reaches the customer.</summary>
public enum NotificationChannel
{
	/// <summary>An email.</summary>
	Email = 1,

	/// <summary>A message in the customer's inbox in the app.</summary>
	InApp = 2,
}

/// <summary>What a notification says.</summary>
public enum NotificationKind
{
	/// <summary>The booking is confirmed.</summary>
	Confirmed = 1,

	/// <summary>The booking was moved to a new time.</summary>
	Rescheduled = 2,

	/// <summary>The booking was cancelled.</summary>
	Cancelled = 3,

	/// <summary>The appointment is coming up.</summary>
	Reminder = 4,
}

/// <summary>Where a notification is.</summary>
public enum NotificationStatus
{
	/// <summary>Waiting to be sent, now or later.</summary>
	Pending = 1,

	/// <summary>Delivered to its channel.</summary>
	Sent = 2,

	/// <summary>No longer worth sending, e.g. a reminder for a booking that was cancelled.</summary>
	Skipped = 3,

	/// <summary>Every attempt failed; someone has to look at it.</summary>
	Failed = 4,
}

/// <summary>
/// One message to one customer on one channel, written to the outbox in the same transaction as the change it
/// announces and sent later by the dispatcher. Delivery is at least once: a channel that sees the same
/// notification twice must recognise it by <see cref="Id"/>.
/// </summary>
public sealed class Notification
{
	/// <summary>Longest error or skip reason kept.</summary>
	public const int MaxDetailLength = 500;

	private Notification(Guid id, BookingId bookingId, NotificationChannel channel, NotificationKind kind, Instant scheduledFor)
	{
		Id = id;
		BookingId = bookingId;
		Channel = channel;
		Kind = kind;
		ScheduledFor = scheduledFor;
		NextAttemptAt = scheduledFor;
		Status = NotificationStatus.Pending;
	}

	/// <summary>The id; channels use it to recognise a notification delivered twice.</summary>
	public Guid Id { get; private set; }

	/// <summary>The booking it is about.</summary>
	public BookingId BookingId { get; private set; }

	/// <summary>How it is sent.</summary>
	public NotificationChannel Channel { get; private set; }

	/// <summary>What it says.</summary>
	public NotificationKind Kind { get; private set; }

	/// <summary>When it should go out.</summary>
	public Instant ScheduledFor { get; private set; }

	/// <summary>Where it is.</summary>
	public NotificationStatus Status { get; private set; }

	/// <summary>How many times sending it was tried.</summary>
	public int Attempts { get; private set; }

	/// <summary>The earliest moment the dispatcher may pick it up: the schedule, a retry, or the end of a lease.</summary>
	public Instant NextAttemptAt { get; private set; }

	/// <summary>When it was delivered.</summary>
	public Instant? SentAt { get; private set; }

	/// <summary>Why it was skipped, or the last error.</summary>
	public string? Detail { get; private set; }

	/// <summary>Schedules a notification.</summary>
	/// <param name="bookingId">The booking it is about.</param>
	/// <param name="channel">How it is sent.</param>
	/// <param name="kind">What it says.</param>
	/// <param name="scheduledFor">When it should go out.</param>
	/// <returns>The notification, pending.</returns>
	public static Notification Schedule(BookingId bookingId, NotificationChannel channel, NotificationKind kind, Instant scheduledFor) =>
		new(Guid.CreateVersion7(), bookingId, channel, kind, scheduledFor);

	/// <summary>
	/// Takes it for one dispatcher until <paramref name="until"/>: another one skips it meanwhile, and if this one
	/// dies before recording the outcome, it becomes due again when the lease ends.
	/// </summary>
	/// <param name="until">The end of the lease.</param>
	public void Lease(Instant until) => NextAttemptAt = until;

	/// <summary>Records the delivery.</summary>
	/// <param name="now">The current instant.</param>
	public void MarkSent(Instant now)
	{
		Attempts++;
		Status = NotificationStatus.Sent;
		SentAt = now;
		Detail = null;
	}

	/// <summary>Records that it is no longer worth sending.</summary>
	/// <param name="reason">Why.</param>
	public void Skip(string reason)
	{
		Status = NotificationStatus.Skipped;
		Detail = Trim(reason);
	}

	/// <summary>Records a failed attempt and schedules the next one, waiting twice as long each time.</summary>
	/// <param name="error">What went wrong.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="maxAttempts">After how many attempts to give up.</param>
	/// <param name="firstRetry">The wait before the first retry.</param>
	/// <returns><see langword="true"/> when it will be tried again; <see langword="false"/> when it gave up.</returns>
	public bool Fail(string error, Instant now, int maxAttempts, Duration firstRetry)
	{
		Attempts++;
		Detail = Trim(error);
		if (Attempts >= maxAttempts)
		{
			Status = NotificationStatus.Failed;
			return false;
		}

		NextAttemptAt = now + (firstRetry * (1L << (Attempts - 1)));
		return true;
	}

	private static string Trim(string text) =>
		string.IsNullOrEmpty(text) || text.Length <= MaxDetailLength ? text ?? string.Empty : text[..MaxDetailLength];
}

/// <summary>A message in a customer's inbox in the app: the in-app channel's delivery.</summary>
public sealed class InboxMessage
{
	private InboxMessage(Guid id, CustomerId customerId, BookingId bookingId, string subject, string body, Instant receivedAt)
	{
		Id = id;
		CustomerId = customerId;
		BookingId = bookingId;
		Subject = subject;
		Body = body;
		ReceivedAt = receivedAt;
	}

	/// <summary>The id of the notification it delivers: delivering it twice leaves one message.</summary>
	public Guid Id { get; private set; }

	/// <summary>Whose inbox.</summary>
	public CustomerId CustomerId { get; private set; }

	/// <summary>The booking it is about.</summary>
	public BookingId BookingId { get; private set; }

	/// <summary>The subject.</summary>
	public string Subject { get; private set; }

	/// <summary>The text.</summary>
	public string Body { get; private set; }

	/// <summary>When it arrived.</summary>
	public Instant ReceivedAt { get; private set; }

	/// <summary>Delivers a notification to the inbox.</summary>
	/// <param name="notificationId">The notification.</param>
	/// <param name="customerId">Whose inbox.</param>
	/// <param name="bookingId">The booking.</param>
	/// <param name="subject">The subject.</param>
	/// <param name="body">The text.</param>
	/// <param name="now">The current instant.</param>
	/// <returns>The message.</returns>
	public static InboxMessage Deliver(Guid notificationId, CustomerId customerId, BookingId bookingId, string subject, string body, Instant now) =>
		new(notificationId, customerId, bookingId, subject, body, now);
}
