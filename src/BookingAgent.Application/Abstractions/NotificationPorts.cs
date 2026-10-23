namespace BookingAgent.Application.Abstractions;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using NodaTime;

/// <summary>What a notification is about, as it stands when it is sent.</summary>
/// <param name="Status">The booking's status now: a reminder for a booking no longer confirmed is not sent.</param>
/// <param name="Period">The appointment.</param>
/// <param name="Zone">The business's clock, to say when on it.</param>
/// <param name="CustomerId">Whom it goes to.</param>
/// <param name="CustomerName">Their name.</param>
/// <param name="CustomerEmail">Their email.</param>
/// <param name="ServiceName">The service.</param>
/// <param name="ProfessionalName">The professional.</param>
/// <param name="BusinessName">The business.</param>
public sealed record NotificationSubject(
	BookingStatus Status,
	Interval Period,
	DateTimeZone Zone,
	CustomerId CustomerId,
	string CustomerName,
	string CustomerEmail,
	string ServiceName,
	string ProfessionalName,
	string BusinessName);

/// <summary>A notification that is due, with what it is about.</summary>
/// <param name="Notification">The notification, tracked: its outcome is saved through the outbox.</param>
/// <param name="Subject">The booking it is about.</param>
public sealed record DueNotification(Notification Notification, NotificationSubject Subject);

/// <summary>A notification rendered for a channel.</summary>
/// <param name="NotificationId">The notification: channels use it to recognise one delivered twice.</param>
/// <param name="CustomerId">Whom it goes to.</param>
/// <param name="BookingId">What it is about.</param>
/// <param name="ToName">Their name.</param>
/// <param name="ToEmail">Their email.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Body">The text.</param>
public sealed record OutgoingMessage(Guid NotificationId, CustomerId CustomerId, BookingId BookingId, string ToName, string ToEmail, string Subject, string Body);

/// <summary>The notifications waiting to be sent. One instance is one unit of work.</summary>
public interface INotificationOutbox
{
	/// <summary>
	/// Takes up to <paramref name="limit"/> due notifications for this dispatcher, leasing them until
	/// <paramref name="now"/> + <paramref name="lease"/> so that another dispatcher skips them.
	/// </summary>
	/// <param name="now">The current instant.</param>
	/// <param name="limit">How many at most.</param>
	/// <param name="lease">How long they are this dispatcher's.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The notifications, oldest first.</returns>
	Task<IReadOnlyList<DueNotification>> ClaimDueAsync(Instant now, int limit, Duration lease, CancellationToken cancellationToken);

	/// <summary>Saves the outcomes recorded so far, and whatever a channel staged with them.</summary>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A way to reach a customer: email, the in-app inbox, and one day WhatsApp.</summary>
public interface INotificationChannel
{
	/// <summary>Which channel this is.</summary>
	NotificationChannel Channel { get; }

	/// <summary>Delivers a message. Throws when it could not; the dispatcher retries.</summary>
	/// <param name="message">The message.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>Reads the in-app inbox.</summary>
public interface IInboxReader
{
	/// <summary>The newest messages for the customers with an email.</summary>
	/// <param name="normalizedEmail">The email as <see cref="Domain.Catalog.Customer.NormalizeEmail"/> writes it.</param>
	/// <param name="limit">How many at most.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The messages, newest first.</returns>
	Task<IReadOnlyList<InboxMessage>> ForEmailAsync(string normalizedEmail, int limit, CancellationToken cancellationToken);
}
