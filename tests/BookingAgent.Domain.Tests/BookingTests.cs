namespace BookingAgent.Domain.Tests;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using NodaTime;

public sealed class BookingTests
{
	private static readonly Instant Now = Instant.FromUtc(2026, 10, 26, 14, 0);
	private static readonly Interval Tomorrow = new(Instant.FromUtc(2026, 10, 27, 15, 0), Instant.FromUtc(2026, 10, 27, 15, 30));
	private static readonly Duration FiveMinutes = Duration.FromMinutes(5);
	private static readonly Duration TwoHours = Duration.FromHours(2);

	private static (Booking Booking, string Token) Hold(Interval? period = null, BookingId? replaces = null) =>
		Booking.Hold(ProfessionalId.New(), ServiceId.New(), CustomerId.New(), period ?? Tomorrow, Now, FiveMinutes, replaces);

	[Fact]
	public void A_hold_takes_its_slot_until_it_runs_out_and_its_token_is_never_stored()
	{
		var (booking, token) = Hold();

		Assert.Equal(BookingStatus.Held, booking.Status);
		Assert.True(booking.TakesSlot(Now + Duration.FromMinutes(4)));
		Assert.False(booking.TakesSlot(Now + FiveMinutes));
		Assert.NotEqual(token, booking.ConfirmationTokenHash);
		Assert.True(token.Length >= 43, "256 random bits, base64url");
	}

	[Fact]
	public void Two_holds_never_share_a_token()
	{
		Assert.NotEqual(Hold().Token, Hold().Token);
	}

	[Fact]
	public void Only_its_own_token_confirms_a_hold_and_sending_it_twice_is_harmless()
	{
		var (booking, token) = Hold();
		var (_, otherToken) = Hold();

		Assert.Equal(ConfirmOutcome.WrongToken, booking.Confirm(otherToken, Now));
		Assert.Equal(ConfirmOutcome.WrongToken, booking.Confirm(string.Empty, Now));
		Assert.Equal(BookingStatus.Held, booking.Status);

		Assert.Equal(ConfirmOutcome.Confirmed, booking.Confirm(token, Now + Duration.FromMinutes(1)));
		Assert.Equal(ConfirmOutcome.AlreadyConfirmed, booking.Confirm(token, Now + Duration.FromMinutes(2)));
		Assert.Equal((BookingStatus.Confirmed, Now + Duration.FromMinutes(1)), (booking.Status, booking.ConfirmedAt!.Value));
		Assert.True(booking.TakesSlot(Now + Duration.FromDays(1)));
	}

	[Fact]
	public void A_hold_confirmed_too_late_is_cancelled_and_frees_its_slot()
	{
		var (booking, token) = Hold();

		Assert.Equal(ConfirmOutcome.Expired, booking.Confirm(token, Now + FiveMinutes));
		Assert.Equal((BookingStatus.Cancelled, CancellationReason.HoldExpired), (booking.Status, booking.CancellationReason!.Value));
		Assert.Equal(ConfirmOutcome.Expired, booking.Confirm(token, Now + FiveMinutes));
	}

	[Fact]
	public void Expire_releases_only_holds_that_ran_out()
	{
		var (held, _) = Hold();
		var (confirmed, token) = Hold();
		confirmed.Confirm(token, Now);

		Assert.False(held.Expire(Now + Duration.FromMinutes(1)));
		Assert.True(held.Expire(Now + FiveMinutes));
		Assert.False(confirmed.Expire(Now + Duration.FromDays(2)));
		Assert.Equal(BookingStatus.Confirmed, confirmed.Status);
	}

	[Fact]
	public void A_confirmed_booking_cannot_be_cancelled_inside_the_cutoff_but_a_hold_always_can()
	{
		var (booking, token) = Hold();
		booking.Confirm(token, Now);
		var justBefore = Tomorrow.Start - TwoHours - Duration.FromMinutes(1);
		var tooLate = Tomorrow.Start - Duration.FromMinutes(90);

		Assert.Equal(CancelOutcome.TooLate, booking.Cancel(tooLate, TwoHours));
		Assert.Equal(CancelOutcome.Cancelled, booking.Cancel(justBefore, TwoHours));
		Assert.Equal(CancelOutcome.AlreadyCancelled, booking.Cancel(justBefore, TwoHours));
		Assert.Equal(CancellationReason.ByCustomer, booking.CancellationReason);

		var (hold, _) = Hold(new Interval(Now + Duration.FromMinutes(30), Now + Duration.FromMinutes(60)));
		Assert.Equal(CancelOutcome.Cancelled, hold.Cancel(Now, TwoHours));
	}

	[Fact]
	public void A_confirmed_reschedule_frees_the_original()
	{
		var (original, token) = Hold();
		original.Confirm(token, Now);
		var later = new Interval(Tomorrow.End, Tomorrow.End + Duration.FromMinutes(30));
		var (replacement, replacementToken) = Hold(later, original.Id);

		Assert.Throws<InvalidOperationException>(() => original.ReplaceWith(replacement, Now));

		replacement.Confirm(replacementToken, Now);
		original.ReplaceWith(replacement, Now);

		Assert.Equal((BookingStatus.Cancelled, CancellationReason.Rescheduled), (original.Status, original.CancellationReason!.Value));
	}

	[Fact]
	public void A_booking_is_for_a_future_period()
	{
		var past = new Interval(Now - Duration.FromHours(1), Now);

		Assert.Throws<ArgumentException>(() => Hold(past));
		Assert.Throws<ArgumentException>(() => Hold(new Interval(Tomorrow.Start, Tomorrow.Start)));
	}

	[Theory]
	[InlineData("ana@ejemplo.com", true)]
	[InlineData("  Ana.Perez@Ejemplo.COM ", true)]
	[InlineData("ana@ejemplo", false)]
	[InlineData("ana ejemplo@x.com", false)]
	[InlineData("@ejemplo.com", false)]
	[InlineData("ana@@ejemplo.com", false)]
	public void A_customer_needs_a_plausible_email(string email, bool valid)
	{
		var business = BusinessId.New();
		if (valid)
		{
			Assert.Equal(Customer.NormalizeEmail(email), Customer.Create(business, "Ana", email, null).Email);
		}
		else
		{
			Assert.Throws<ArgumentException>(() => Customer.Create(business, "Ana", email, null));
		}
	}
}
