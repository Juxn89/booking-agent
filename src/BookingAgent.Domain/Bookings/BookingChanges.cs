namespace BookingAgent.Domain.Bookings;

using NodaTime;

/// <summary>What happened to a booking that the customer must hear about.</summary>
public enum BookingChange
{
	/// <summary>A new booking was confirmed.</summary>
	Confirmed = 1,

	/// <summary>A confirmed booking was cancelled by the customer or the business.</summary>
	Cancelled = 2,

	/// <summary>A booking was moved: this one, confirmed, replaces an earlier one.</summary>
	Rescheduled = 3,
}

/// <summary>
/// Recorded by a <see cref="Booking"/> when it changes in a way someone must be told about. Whoever saves the
/// booking turns these into notifications in the same transaction, so a confirmation is never saved without its
/// notice, and a notice never exists for a change that was not saved.
/// </summary>
/// <param name="Booking">The booking.</param>
/// <param name="Change">What happened.</param>
/// <param name="Period">The appointment's period at the time.</param>
/// <param name="At">When.</param>
public sealed record BookingChanged(BookingId Booking, BookingChange Change, Interval Period, Instant At);
