namespace BookingAgent.Application.Features.Customers;

using BookingAgent.Application.Features.Bookings;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using Microsoft.Extensions.Options;
using NodaTime;

/// <summary>A booking as its customer sees it.</summary>
/// <param name="Id">The booking.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="ServiceName">Its name.</param>
/// <param name="ProfessionalId">The professional.</param>
/// <param name="ProfessionalName">Their name.</param>
/// <param name="Start">When.</param>
/// <param name="LocalStart">When, on the business's clock.</param>
/// <param name="CanChange">Whether it can still be cancelled or moved (the business's cut-off).</param>
public sealed record MyBooking(Guid Id, Guid ServiceId, string ServiceName, Guid ProfessionalId, string ProfessionalName, Instant Start, LocalDateTime LocalStart, bool CanChange);

/// <summary>A customer's booking, with the names to show it by.</summary>
/// <param name="Booking">The booking.</param>
/// <param name="ServiceName">The service.</param>
/// <param name="ProfessionalName">The professional.</param>
/// <param name="Zone">The business's clock.</param>
public sealed record CustomerBooking(Booking Booking, string ServiceName, string ProfessionalName, DateTimeZone Zone);

/// <summary>Reads a customer's bookings.</summary>
public interface IMyBookingsReader
{
	/// <summary>The customer's confirmed bookings that have not started, earliest first.</summary>
	/// <param name="customer">The customer.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The bookings.</returns>
	Task<IReadOnlyList<CustomerBooking>> UpcomingAsync(CustomerId customer, Instant now, CancellationToken cancellationToken);
}

/// <summary>
/// A signed-in customer's upcoming appointments: what "Mis citas" and the assistant's <c>find_my_bookings</c> show.
/// </summary>
/// <param name="reader">The bookings.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">The cut-off.</param>
public sealed class ListMyBookings(IMyBookingsReader reader, IClock clock, IOptions<BookingOptions> options)
{
	/// <summary>Lists them.</summary>
	/// <param name="customer">The signed-in customer.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The bookings, earliest first.</returns>
	public async Task<IReadOnlyList<MyBooking>> HandleAsync(SignedInCustomer customer, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(customer);
		var now = clock.GetCurrentInstant();
		var cutoff = Duration.FromTimeSpan(options.Value.CancellationCutoff);
		var bookings = await reader.UpcomingAsync(customer.CustomerId, now, cancellationToken).ConfigureAwait(false);
		return [.. bookings.Select(b => new MyBooking(
			b.Booking.Id.Value,
			b.Booking.ServiceId.Value,
			b.ServiceName,
			b.Booking.ProfessionalId.Value,
			b.ProfessionalName,
			b.Booking.Period.Start,
			b.Booking.Period.Start.InZone(b.Zone).LocalDateTime,
			now <= b.Booking.Period.Start - cutoff))];
	}
}
