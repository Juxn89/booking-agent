namespace BookingAgent.Application.Abstractions;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using NodaTime;

/// <summary>Where bookings and customers live. One instance is one unit of work.</summary>
public interface IBookingRepository
{
	/// <summary>A booking, tracked, to change it.</summary>
	/// <param name="id">Its id.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The booking, or <see langword="null"/>.</returns>
	Task<Booking?> FindTrackedAsync(BookingId id, CancellationToken cancellationToken);

	/// <summary>A customer of a business, by email.</summary>
	/// <param name="business">The business.</param>
	/// <param name="normalizedEmail">The email as <see cref="Customer.NormalizeEmail"/> writes it.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The customer, or <see langword="null"/>.</returns>
	Task<Customer?> FindCustomerAsync(BusinessId business, string normalizedEmail, CancellationToken cancellationToken);

	/// <summary>The time zone of the business a professional works for.</summary>
	/// <param name="professional">The professional.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The business's clock.</returns>
	Task<DateTimeZone> ZoneOfAsync(ProfessionalId professional, CancellationToken cancellationToken);

	/// <summary>
	/// Loads, tracked, the holds of a professional that overlap a period and have run out but still count as held:
	/// the exclusion constraint does not know about time, so they must be released before the period can be taken.
	/// </summary>
	/// <param name="professional">The professional.</param>
	/// <param name="period">The period about to be held.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The holds.</returns>
	Task<IReadOnlyList<Booking>> StaleHoldsAsync(ProfessionalId professional, Interval period, Instant now, CancellationToken cancellationToken);

	/// <summary>Loads, tracked, holds that ran out, oldest first.</summary>
	/// <param name="now">The current instant.</param>
	/// <param name="limit">How many at most.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The holds.</returns>
	Task<IReadOnlyList<Booking>> ExpiredHoldsAsync(Instant now, int limit, CancellationToken cancellationToken);

	/// <summary>Stages a new booking.</summary>
	/// <param name="booking">The booking.</param>
	void Add(Booking booking);

	/// <summary>Stages a new customer.</summary>
	/// <param name="customer">The customer.</param>
	void Add(Customer customer);

	/// <summary>Saves everything staged and changed, in one transaction.</summary>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	/// <exception cref="Common.SlotTakenException">The period overlaps a booking another request saved first.</exception>
	/// <exception cref="Common.BookingChangedException">A booking changed since it was loaded.</exception>
	Task SaveChangesAsync(CancellationToken cancellationToken);
}
