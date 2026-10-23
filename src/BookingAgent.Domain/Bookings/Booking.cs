namespace BookingAgent.Domain.Bookings;

using System.Security.Cryptography;
using System.Text;
using NodaTime;

/// <summary>Where a booking is.</summary>
public enum BookingStatus
{
	/// <summary>Held for a few minutes, waiting for the customer to confirm it.</summary>
	Held = 1,

	/// <summary>Confirmed by the customer.</summary>
	Confirmed = 2,

	/// <summary>No longer takes the slot: see <see cref="CancellationReason"/>.</summary>
	Cancelled = 3,
}

/// <summary>Why a booking stopped taking its slot.</summary>
public enum CancellationReason
{
	/// <summary>Nobody confirmed the hold in time.</summary>
	HoldExpired = 1,

	/// <summary>The customer cancelled.</summary>
	ByCustomer = 2,

	/// <summary>It was moved: the booking that replaced it is confirmed.</summary>
	Rescheduled = 3,
}

/// <summary>What a confirmation attempt did.</summary>
public enum ConfirmOutcome
{
	/// <summary>Confirmed now.</summary>
	Confirmed = 1,

	/// <summary>Already confirmed with this token: the same request sent twice.</summary>
	AlreadyConfirmed = 2,

	/// <summary>The token is not this hold's.</summary>
	WrongToken = 3,

	/// <summary>The hold ran out before the confirmation; it is now cancelled.</summary>
	Expired = 4,

	/// <summary>It was cancelled before, for another reason.</summary>
	Cancelled = 5,
}

/// <summary>What a cancellation attempt did.</summary>
public enum CancelOutcome
{
	/// <summary>Cancelled now.</summary>
	Cancelled = 1,

	/// <summary>Already cancelled.</summary>
	AlreadyCancelled = 2,

	/// <summary>Too close to the appointment: the business's cut-off has passed.</summary>
	TooLate = 3,
}

/// <summary>
/// One appointment: a professional, a service, a customer and a period. It is first <b>held</b> for a few minutes
/// and comes with a one-time token; only someone with that token can confirm it, and the assistant never gets
/// one. Held and confirmed bookings take their slot (the database's exclusion constraint makes that absolute);
/// a cancelled one frees it.
/// </summary>
public sealed class Booking
{
	/// <summary>Random bytes in a confirmation token: 256 bits, unguessable.</summary>
	public const int TokenBytes = 32;

	private readonly List<BookingChanged> _changes = [];

	private Booking(
		BookingId id,
		ProfessionalId professionalId,
		ServiceId serviceId,
		CustomerId customerId,
		Interval period,
		Instant createdAt,
		Instant holdExpiresAt,
		string confirmationTokenHash,
		BookingId? replaces)
	{
		Id = id;
		ProfessionalId = professionalId;
		ServiceId = serviceId;
		CustomerId = customerId;
		Period = period;
		CreatedAt = createdAt;
		HoldExpiresAt = holdExpiresAt;
		ConfirmationTokenHash = confirmationTokenHash;
		Replaces = replaces;
		Status = BookingStatus.Held;
	}

	/// <summary>The id.</summary>
	public BookingId Id { get; private set; }

	/// <summary>With whom.</summary>
	public ProfessionalId ProfessionalId { get; private set; }

	/// <summary>For what.</summary>
	public ServiceId ServiceId { get; private set; }

	/// <summary>For whom.</summary>
	public CustomerId CustomerId { get; private set; }

	/// <summary>When, half-open: an appointment ending at 10:00 leaves 10:00 free.</summary>
	public Interval Period { get; private set; }

	/// <summary>Where it is.</summary>
	public BookingStatus Status { get; private set; }

	/// <summary>When it was held.</summary>
	public Instant CreatedAt { get; private set; }

	/// <summary>Until when the hold lasts; past it, the hold no longer counts.</summary>
	public Instant HoldExpiresAt { get; private set; }

	/// <summary>SHA-256 of the confirmation token, hex: the token itself is never stored.</summary>
	public string ConfirmationTokenHash { get; private set; }

	/// <summary>When it was confirmed.</summary>
	public Instant? ConfirmedAt { get; private set; }

	/// <summary>When it was cancelled.</summary>
	public Instant? CancelledAt { get; private set; }

	/// <summary>Why it was cancelled.</summary>
	public CancellationReason? CancellationReason { get; private set; }

	/// <summary>When a reschedule: the confirmed booking this one replaces once confirmed.</summary>
	public BookingId? Replaces { get; private set; }

	/// <summary>The changes since it was loaded that the customer must be told about; not stored.</summary>
	public IReadOnlyList<BookingChanged> Changes => _changes;

	/// <summary>Forgets the recorded changes, once they have been turned into notifications and saved.</summary>
	public void ClearChanges() => _changes.Clear();

	/// <summary>Holds a slot for <paramref name="holdFor"/>.</summary>
	/// <param name="professionalId">With whom.</param>
	/// <param name="serviceId">For what.</param>
	/// <param name="customerId">For whom.</param>
	/// <param name="period">When.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="holdFor">How long the customer has to confirm.</param>
	/// <param name="replaces">When rescheduling, the confirmed booking this one will replace.</param>
	/// <returns>The booking, and the token that confirms it: returned once, never stored.</returns>
	/// <exception cref="ArgumentException">An empty period, one in the past, or a non-positive hold.</exception>
	public static (Booking Booking, string Token) Hold(
		ProfessionalId professionalId,
		ServiceId serviceId,
		CustomerId customerId,
		Interval period,
		Instant now,
		Duration holdFor,
		BookingId? replaces = null)
	{
		if (period.Duration <= Duration.Zero || period.Start < now)
		{
			throw new ArgumentException("A booking is for a future period.", nameof(period));
		}

		if (holdFor <= Duration.Zero)
		{
			throw new ArgumentException("A hold lasts some time.", nameof(holdFor));
		}

		var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
		var booking = new Booking(BookingId.New(), professionalId, serviceId, customerId, period, now, now + holdFor, Hash(token), replaces);
		return (booking, token);
	}

	/// <summary>Whether it takes its slot at <paramref name="now"/>: confirmed, or held and not yet expired.</summary>
	/// <param name="now">The current instant.</param>
	/// <returns><see langword="true"/> when it does.</returns>
	public bool TakesSlot(Instant now) =>
		Status == BookingStatus.Confirmed || (Status == BookingStatus.Held && now < HoldExpiresAt);

	/// <summary>Confirms the hold, if the token is its own and it has not run out.</summary>
	/// <param name="token">The token <see cref="Hold"/> returned.</param>
	/// <param name="now">The current instant.</param>
	/// <returns>What happened.</returns>
	public ConfirmOutcome Confirm(string token, Instant now)
	{
		if (!Matches(token))
		{
			return ConfirmOutcome.WrongToken;
		}

		switch (Status)
		{
			case BookingStatus.Confirmed:
				return ConfirmOutcome.AlreadyConfirmed;
			case BookingStatus.Cancelled:
				return CancellationReason == Bookings.CancellationReason.HoldExpired ? ConfirmOutcome.Expired : ConfirmOutcome.Cancelled;
		}

		if (now >= HoldExpiresAt)
		{
			Expire(now);
			return ConfirmOutcome.Expired;
		}

		Status = BookingStatus.Confirmed;
		ConfirmedAt = now;
		_changes.Add(new BookingChanged(Id, Replaces is null ? BookingChange.Confirmed : BookingChange.Rescheduled, Period, now));
		return ConfirmOutcome.Confirmed;
	}

	/// <summary>Releases a hold nobody confirmed in time.</summary>
	/// <param name="now">The current instant.</param>
	/// <returns><see langword="true"/> when it was a hold that had run out, now cancelled.</returns>
	public bool Expire(Instant now)
	{
		if (Status != BookingStatus.Held || now < HoldExpiresAt)
		{
			return false;
		}

		CancelAs(Bookings.CancellationReason.HoldExpired, now);
		return true;
	}

	/// <summary>Cancels it, unless the appointment is closer than <paramref name="cutoff"/>.</summary>
	/// <param name="now">The current instant.</param>
	/// <param name="cutoff">The business's last moment to cancel, before the start.</param>
	/// <returns>What happened.</returns>
	public CancelOutcome Cancel(Instant now, Duration cutoff)
	{
		if (Status == BookingStatus.Cancelled)
		{
			return CancelOutcome.AlreadyCancelled;
		}

		// A hold is not an appointment yet: releasing it is always allowed.
		if (Status == BookingStatus.Confirmed && now > Period.Start - cutoff)
		{
			return CancelOutcome.TooLate;
		}

		// Releasing a hold tells nobody anything: there was no appointment yet.
		if (Status == BookingStatus.Confirmed)
		{
			_changes.Add(new BookingChanged(Id, BookingChange.Cancelled, Period, now));
		}

		CancelAs(Bookings.CancellationReason.ByCustomer, now);
		return CancelOutcome.Cancelled;
	}

	/// <summary>Frees the slot of a confirmed booking whose reschedule was just confirmed.</summary>
	/// <param name="replacement">The confirmed booking that replaces it.</param>
	/// <param name="now">The current instant.</param>
	/// <exception cref="InvalidOperationException">The replacement is not a confirmed reschedule of this booking.</exception>
	public void ReplaceWith(Booking replacement, Instant now)
	{
		ArgumentNullException.ThrowIfNull(replacement);
		if (replacement.Replaces != Id || replacement.Status != BookingStatus.Confirmed || Status != BookingStatus.Confirmed)
		{
			throw new InvalidOperationException($"Booking {replacement.Id} is not a confirmed reschedule of {Id}.");
		}

		CancelAs(Bookings.CancellationReason.Rescheduled, now);
	}

	private void CancelAs(CancellationReason reason, Instant now)
	{
		Status = BookingStatus.Cancelled;
		CancellationReason = reason;
		CancelledAt = now;
	}

	// Constant time: how much of a wrong token matched must not leak through the response time.
	private bool Matches(string token) =>
		!string.IsNullOrEmpty(token)
		&& CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(ConfirmationTokenHash));

	private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
