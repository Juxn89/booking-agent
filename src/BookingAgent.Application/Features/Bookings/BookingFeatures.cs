namespace BookingAgent.Application.Features.Bookings;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Scheduling;
using Microsoft.Extensions.Options;
using NodaTime;

/// <summary>The booking rules, bound from the <c>Bookings</c> section.</summary>
public sealed class BookingOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Bookings";

	/// <summary>How long a customer has to confirm a held slot: long enough to read the summary, short enough not to block others.</summary>
	[Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
	public TimeSpan HoldDuration { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>The last moment before an appointment it can still be cancelled or moved.</summary>
	[Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
	public TimeSpan CancellationCutoff { get; set; } = TimeSpan.FromHours(2);

	/// <summary>How many other times to offer when the one asked for is not free.</summary>
	[Range(1, 10)]
	public int Alternatives { get; set; } = 3;
}

/// <summary>Who the booking is for.</summary>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email: how they confirm, get reminders and find their bookings again.</param>
/// <param name="Phone">Their phone, optional.</param>
public sealed record CustomerDetails(string Name, string Email, string? Phone);

/// <summary>A request to hold a slot.</summary>
/// <param name="ProfessionalId">With whom.</param>
/// <param name="ServiceId">For what.</param>
/// <param name="Start">The slot's start, as offered by the availability.</param>
/// <param name="Customer">For whom.</param>
public sealed record HoldRequest(Guid ProfessionalId, Guid ServiceId, Instant Start, CustomerDetails Customer);

/// <summary>A booking as the API shows it.</summary>
/// <param name="Id">The id.</param>
/// <param name="ProfessionalId">With whom.</param>
/// <param name="ServiceId">For what.</param>
/// <param name="Start">When it starts.</param>
/// <param name="End">When it ends.</param>
/// <param name="LocalStart">Its start on the business's clock.</param>
/// <param name="Status">Where it is.</param>
/// <param name="HoldExpiresAt">While held, until when.</param>
public sealed record BookingResponse(
	Guid Id,
	Guid ProfessionalId,
	Guid ServiceId,
	Instant Start,
	Instant End,
	LocalDateTime LocalStart,
	BookingStatus Status,
	Instant? HoldExpiresAt)
{
	/// <summary>Maps a booking.</summary>
	/// <param name="booking">The booking.</param>
	/// <param name="zone">The business's clock.</param>
	/// <returns>The response.</returns>
	public static BookingResponse From(Booking booking, DateTimeZone zone)
	{
		ArgumentNullException.ThrowIfNull(booking);
		ArgumentNullException.ThrowIfNull(zone);
		return new(
			booking.Id.Value,
			booking.ProfessionalId.Value,
			booking.ServiceId.Value,
			booking.Period.Start,
			booking.Period.End,
			booking.Period.Start.InZone(zone).LocalDateTime,
			booking.Status,
			booking.Status == BookingStatus.Held ? booking.HoldExpiresAt : null);
	}
}

/// <summary>A held slot and the token that confirms it: the token is returned here once and never again.</summary>
/// <param name="Booking">The hold.</param>
/// <param name="ConfirmationToken">What confirms it.</param>
public sealed record HoldResponse(BookingResponse Booking, string ConfirmationToken);

/// <summary>The booking error codes (docs/errors.md).</summary>
public static class BookingErrors
{
	/// <summary>The customer's name or email is missing or not valid.</summary>
	public const string InvalidCustomer = "booking.invalid_customer";

	/// <summary>The time asked for is not a free slot of that professional and service.</summary>
	public const string SlotUnavailable = "booking.slot_unavailable";

	/// <summary>Someone else took the slot a moment before.</summary>
	public const string SlotTaken = "booking.slot_taken";

	/// <summary>No booking with that id, or a confirmation token that is not its own.</summary>
	public const string NotFound = "booking.not_found";

	/// <summary>The hold ran out before it was confirmed.</summary>
	public const string HoldExpired = "booking.hold_expired";

	/// <summary>The booking was cancelled.</summary>
	public const string Cancelled = "booking.cancelled";

	/// <summary>Too close to the appointment to cancel or move it.</summary>
	public const string TooLate = "booking.too_late";

	/// <summary>Only a confirmed booking can be moved.</summary>
	public const string NotConfirmed = "booking.not_confirmed";

	/// <summary>The booking changed while the request was handled; trying again sees the new state.</summary>
	public const string Changed = "booking.changed";

	/// <summary>The <see cref="Error.Details"/> key with the nearest free starts, ISO 8601, comma-separated.</summary>
	public const string AlternativesDetail = "alternatives";
}

/// <summary>
/// Holds a slot for a customer and returns the token that confirms it. The form and the assistant's
/// <c>hold_slot</c> tool both call this; neither confirms anything. The slot must be one the availability offers
/// now, and the database's exclusion constraint has the last word when two requests want it at once.
/// </summary>
/// <param name="availability">Schedules, bookings and rules.</param>
/// <param name="bookings">The bookings.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">Hold duration and alternatives.</param>
public sealed class HoldSlot(IAvailabilitySource availability, IBookingRepository bookings, IClock clock, IOptions<BookingOptions> options)
{
	/// <summary>Holds it.</summary>
	/// <param name="request">What to hold, for whom.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The hold and its token, or why the slot cannot be held (with other times when there are).</returns>
	public async Task<Result<HoldResponse>> HandleAsync(HoldRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.Customer is null || string.IsNullOrWhiteSpace(request.Customer.Name) || !Customer.IsEmail(Customer.NormalizeEmail(request.Customer.Email)))
		{
			return Error.Validation(BookingErrors.InvalidCustomer, "A booking needs the customer's name and a valid email.");
		}

		return await HoldAsync(
			new ProfessionalId(request.ProfessionalId),
			new ServiceId(request.ServiceId),
			request.Start,
			business => FindOrCreateAsync(business, request.Customer, cancellationToken),
			replaces: null,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Holds a slot for a known customer, optionally as the reschedule of a confirmed booking.</summary>
	/// <param name="professional">With whom.</param>
	/// <param name="service">For what.</param>
	/// <param name="start">When.</param>
	/// <param name="customer">Resolves the customer once the business is known.</param>
	/// <param name="replaces">The booking a reschedule replaces.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The hold, or why not.</returns>
	internal async Task<Result<HoldResponse>> HoldAsync(
		ProfessionalId professional,
		ServiceId service,
		Instant start,
		Func<BusinessId, Task<Result<CustomerId>>> customer,
		BookingId? replaces,
		CancellationToken cancellationToken)
	{
		var now = clock.GetCurrentInstant();
		var (context, slots) = await AvailableAsync(professional, service, start, now, cancellationToken).ConfigureAwait(false);
		if (context is null)
		{
			return Error.NotFound(AvailabilityErrors.NotOffered, "That professional does not offer that service.");
		}

		var slot = slots.FirstOrDefault(s => s.Period.Start == start);
		if (slot is null)
		{
			return Unavailable(BookingErrors.SlotUnavailable, "That time is not free for that professional and service.", slots, start);
		}

		var customerId = await customer(context.Business).ConfigureAwait(false);
		if (!customerId.IsSuccess)
		{
			return customerId.Error!;
		}

		foreach (var stale in await bookings.StaleHoldsAsync(professional, slot.Period, now, cancellationToken).ConfigureAwait(false))
		{
			stale.Expire(now);
		}

		var (booking, token) = Booking.Hold(professional, service, customerId.Value, slot.Period, now, Duration.FromTimeSpan(options.Value.HoldDuration), replaces);
		bookings.Add(booking);
		try
		{
			await bookings.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (SlotTakenException)
		{
			var (_, freeNow) = await AvailableAsync(professional, service, start, clock.GetCurrentInstant(), cancellationToken).ConfigureAwait(false);
			return Unavailable(BookingErrors.SlotTaken, "Someone else took that time a moment ago.", freeNow, start);
		}

		return new HoldResponse(BookingResponse.From(booking, context.Zone), token);
	}

	// The day before and after too: the slot's local date can differ from its UTC date.
	private async Task<(AvailabilityContext? Context, IReadOnlyList<AvailableSlot> Slots)> AvailableAsync(
		ProfessionalId professional,
		ServiceId service,
		Instant start,
		Instant now,
		CancellationToken cancellationToken)
	{
		var day = start.InUtc().Date;
		var dates = new DateInterval(day.PlusDays(-1), day.PlusDays(1));
		var context = await availability.LoadAsync(professional.Value, service.Value, dates, cancellationToken).ConfigureAwait(false);
		return context is null
			? (null, [])
			: (context, Domain.Scheduling.Availability.Find(new AvailabilityRequest(context.Schedule, context.Zone, dates, context.ServiceDuration, context.Busy, now, context.Rules)));
	}

	// The nearest free starts around the one asked for, so the caller (or the assistant) can offer them at once.
	private Error Unavailable(string code, string message, IReadOnlyList<AvailableSlot> slots, Instant asked)
	{
		var nearest = slots
			.OrderBy(s => Math.Abs((s.Period.Start - asked).TotalTicks))
			.Take(options.Value.Alternatives)
			.OrderBy(static s => s.Period.Start)
			.Select(static s => s.Period.Start.ToString("g", CultureInfo.InvariantCulture));
		return Error.Conflict(code, message, new Dictionary<string, string> { [BookingErrors.AlternativesDetail] = string.Join(',', nearest) });
	}

	private async Task<Result<CustomerId>> FindOrCreateAsync(BusinessId business, CustomerDetails details, CancellationToken cancellationToken)
	{
		var existing = await bookings.FindCustomerAsync(business, Customer.NormalizeEmail(details.Email), cancellationToken).ConfigureAwait(false);
		if (existing is not null)
		{
			return existing.Id;
		}

		try
		{
			var customer = Customer.Create(business, details.Name, details.Email, details.Phone);
			bookings.Add(customer);
			return customer.Id;
		}
		catch (ArgumentException)
		{
			return Error.Validation(BookingErrors.InvalidCustomer, "A booking needs the customer's name and a valid email.");
		}
	}
}

/// <summary>
/// Confirms a hold with its token: the customer's click, never the assistant. A reschedule's confirmation frees the
/// booking it replaces in the same transaction, so there is never a moment with both or neither.
/// </summary>
/// <param name="bookings">The bookings.</param>
/// <param name="clock">The clock.</param>
public sealed class ConfirmHold(IBookingRepository bookings, IClock clock)
{
	/// <summary>Confirms it.</summary>
	/// <param name="id">The hold.</param>
	/// <param name="token">The token the hold came with.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The confirmed booking, or why not. A wrong token reads as not found: it says nothing about the booking.</returns>
	public async Task<Result<BookingResponse>> HandleAsync(Guid id, string? token, CancellationToken cancellationToken)
	{
		var booking = await bookings.FindTrackedAsync(new BookingId(id), cancellationToken).ConfigureAwait(false);
		if (booking is null)
		{
			return NotFound(id);
		}

		var now = clock.GetCurrentInstant();
		var outcome = booking.Confirm(token ?? string.Empty, now);
		if (outcome == ConfirmOutcome.WrongToken)
		{
			return NotFound(id);
		}

		if (outcome == ConfirmOutcome.Confirmed && booking.Replaces is { } replaced
			&& await bookings.FindTrackedAsync(replaced, cancellationToken).ConfigureAwait(false) is { } original)
		{
			original.ReplaceWith(booking, now);
		}

		try
		{
			await bookings.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (BookingChangedException)
		{
			return Error.Conflict(BookingErrors.Changed, "The booking changed in the meantime; ask for it again.");
		}

		return outcome switch
		{
			ConfirmOutcome.Expired => Error.Conflict(BookingErrors.HoldExpired, "The hold ran out before it was confirmed; the time may still be free."),
			ConfirmOutcome.Cancelled => Error.Conflict(BookingErrors.Cancelled, "This booking was cancelled."),
			_ => BookingResponse.From(booking, await bookings.ZoneOfAsync(booking.ProfessionalId, cancellationToken).ConfigureAwait(false)),
		};
	}

	private static Error NotFound(Guid id) => Error.NotFound(BookingErrors.NotFound, $"There is no hold {id} with that token.");
}

/// <summary>Cancels a booking, unless the appointment is inside the business's cut-off.</summary>
/// <param name="bookings">The bookings.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">The cut-off.</param>
public sealed class CancelBooking(IBookingRepository bookings, IClock clock, IOptions<BookingOptions> options)
{
	/// <summary>Cancels it.</summary>
	/// <param name="id">The booking.</param>
	/// <param name="requestedBy">The signed-in customer asking, who must own it; <see langword="null"/> for the business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The cancelled booking, or why not. Someone else's booking reads as not found.</returns>
	public async Task<Result<BookingResponse>> HandleAsync(Guid id, CustomerId? requestedBy, CancellationToken cancellationToken)
	{
		var booking = await bookings.FindTrackedAsync(new BookingId(id), cancellationToken).ConfigureAwait(false);
		if (booking is null || (requestedBy is { } owner && booking.CustomerId != owner))
		{
			return Error.NotFound(BookingErrors.NotFound, $"There is no booking {id}.");
		}

		if (booking.Cancel(clock.GetCurrentInstant(), Duration.FromTimeSpan(options.Value.CancellationCutoff)) == CancelOutcome.TooLate)
		{
			return Error.Conflict(BookingErrors.TooLate, $"Bookings can be cancelled until {options.Value.CancellationCutoff.TotalHours:0.#} hours before.");
		}

		try
		{
			await bookings.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (BookingChangedException)
		{
			return Error.Conflict(BookingErrors.Changed, "The booking changed in the meantime; ask for it again.");
		}

		return BookingResponse.From(booking, await bookings.ZoneOfAsync(booking.ProfessionalId, cancellationToken).ConfigureAwait(false));
	}
}

/// <summary>
/// Moves a confirmed booking: holds the new time as its replacement, and the customer confirms it as any other hold.
/// Until then the original stands, so a move that is never confirmed changes nothing.
/// </summary>
/// <param name="hold">Holds the new time.</param>
/// <param name="bookings">The bookings.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">The cut-off.</param>
public sealed class RescheduleBooking(HoldSlot hold, IBookingRepository bookings, IClock clock, IOptions<BookingOptions> options)
{
	/// <summary>Holds the new time.</summary>
	/// <param name="id">The confirmed booking to move.</param>
	/// <param name="newStart">The new start, one the availability offers.</param>
	/// <param name="professionalId">Another professional, or the same when <see langword="null"/>.</param>
	/// <param name="requestedBy">The signed-in customer asking, who must own it; <see langword="null"/> for the business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The replacement hold and its token, or why not. Someone else's booking reads as not found.</returns>
	public async Task<Result<HoldResponse>> HandleAsync(Guid id, Instant newStart, Guid? professionalId, CustomerId? requestedBy, CancellationToken cancellationToken)
	{
		var original = await bookings.FindTrackedAsync(new BookingId(id), cancellationToken).ConfigureAwait(false);
		if (original is null || (requestedBy is { } owner && original.CustomerId != owner))
		{
			return Error.NotFound(BookingErrors.NotFound, $"There is no booking {id}.");
		}

		if (original.Status != BookingStatus.Confirmed)
		{
			return Error.Conflict(BookingErrors.NotConfirmed, "Only a confirmed booking can be moved.");
		}

		if (clock.GetCurrentInstant() > original.Period.Start - Duration.FromTimeSpan(options.Value.CancellationCutoff))
		{
			return Error.Conflict(BookingErrors.TooLate, $"Bookings can be moved until {options.Value.CancellationCutoff.TotalHours:0.#} hours before.");
		}

		return await hold.HoldAsync(
			professionalId is { } other ? new ProfessionalId(other) : original.ProfessionalId,
			original.ServiceId,
			newStart,
			_ => Task.FromResult(Result<CustomerId>.Success(original.CustomerId)),
			original.Id,
			cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>Releases holds nobody confirmed in time, so the calendar shows them free again.</summary>
/// <param name="bookings">The bookings.</param>
/// <param name="clock">The clock.</param>
public sealed class ExpireHolds(IBookingRepository bookings, IClock clock)
{
	/// <summary>The most released in one pass; the sweeper comes back for the rest.</summary>
	public const int BatchSize = 100;

	/// <summary>Releases them.</summary>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>How many were released.</returns>
	public async Task<int> HandleAsync(CancellationToken cancellationToken)
	{
		var now = clock.GetCurrentInstant();
		var expired = await bookings.ExpiredHoldsAsync(now, BatchSize, cancellationToken).ConfigureAwait(false);
		var released = expired.Count(hold => hold.Expire(now));
		if (released > 0)
		{
			await bookings.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		return released;
	}
}
