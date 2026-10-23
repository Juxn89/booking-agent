namespace BookingAgent.Application.Features.Notifications;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Common;
using BookingAgent.Domain.Catalog;
using NodaTime;

/// <summary>A message in the in-app inbox.</summary>
/// <param name="Id">Its id, the notification's.</param>
/// <param name="BookingId">The booking it is about.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The text.</param>
/// <param name="ReceivedAt">When it arrived.</param>
public sealed record InboxItem(Guid Id, Guid BookingId, string Subject, string Body, Instant ReceivedAt);

/// <summary>The inbox error codes.</summary>
public static class InboxErrors
{
	/// <summary>The email is not a plausible address.</summary>
	public const string InvalidEmail = "inbox.invalid_email";
}

/// <summary>The in-app inbox of a customer, by email; the API serves it to the signed-in customer only.</summary>
/// <param name="inbox">The inbox.</param>
public sealed class ReadInbox(IInboxReader inbox)
{
	/// <summary>The most messages returned.</summary>
	public const int Limit = 50;

	/// <summary>Reads it.</summary>
	/// <param name="email">The customer's email.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The messages, newest first.</returns>
	public async Task<Result<IReadOnlyList<InboxItem>>> HandleAsync(string? email, CancellationToken cancellationToken)
	{
		var normalized = Customer.NormalizeEmail(email ?? string.Empty);
		if (!Customer.IsEmail(normalized))
		{
			return Error.Validation(InboxErrors.InvalidEmail, "Give the email the booking was made with.");
		}

		var messages = await inbox.ForEmailAsync(normalized, Limit, cancellationToken).ConfigureAwait(false);
		return messages.Select(static m => new InboxItem(m.Id, m.BookingId.Value, m.Subject, m.Body, m.ReceivedAt)).ToList();
	}
}
