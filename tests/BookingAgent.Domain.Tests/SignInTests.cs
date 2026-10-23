namespace BookingAgent.Domain.Tests;

using BookingAgent.Domain;
using BookingAgent.Domain.Customers;
using NodaTime;

public sealed class SignInTests
{
	private static readonly Instant Now = Instant.FromUtc(2026, 11, 2, 13, 0);
	private static readonly Duration TenMinutes = Duration.FromMinutes(10);

	private static (SignInCode Code, string Digits) Issue() => SignInCode.Issue(BusinessId.New(), CustomerId.New(), Now, TenMinutes);

	private static string Other(string digits) => digits == "000000" ? "000001" : "000000";

	[Fact]
	public void A_code_is_six_digits_stored_only_as_a_hash_and_works_once()
	{
		var (code, digits) = Issue();

		Assert.Matches("^[0-9]{6}$", digits);
		Assert.DoesNotContain(digits, code.CodeHash, StringComparison.Ordinal);
		Assert.Equal(CodeCheck.Accepted, code.Check($" {digits} ", Now, maxAttempts: 5));
		Assert.Equal(CodeCheck.Spent, code.Check(digits, Now, maxAttempts: 5));
	}

	[Fact]
	public void A_code_runs_out_with_time_and_with_wrong_attempts()
	{
		var (late, lateDigits) = Issue();
		var (guessed, guessedDigits) = Issue();

		var expired = late.Check(lateDigits, Now + TenMinutes, maxAttempts: 5);
		var attempts = Enumerable.Range(0, 3).Select(_ => guessed.Check(Other(guessedDigits), Now, maxAttempts: 3)).ToList();

		Assert.Equal(CodeCheck.Spent, expired);
		Assert.Equal([CodeCheck.Wrong, CodeCheck.Wrong, CodeCheck.Spent], attempts);
		Assert.Equal(CodeCheck.Spent, guessed.Check(guessedDigits, Now, maxAttempts: 3));
	}

	[Fact]
	public void A_session_is_found_by_its_token_hash_and_ends_on_sign_out_or_in_time()
	{
		var (session, token) = CustomerSession.Start(BusinessId.New(), CustomerId.New(), Now, Duration.FromHours(2));

		Assert.Equal(session.TokenHash, CustomerSession.HashToken(token));
		Assert.NotEqual(token, session.TokenHash);
		Assert.True(session.IsActive(Now + Duration.FromMinutes(119)));
		Assert.False(session.IsActive(Now + Duration.FromHours(2)));
		session.End(Now);
		Assert.False(session.IsActive(Now));
	}
}
