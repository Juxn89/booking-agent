namespace BookingAgent.Application.Features.Customers;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Common;
using BookingAgent.Domain;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Customers;
using Microsoft.Extensions.Options;
using NodaTime;

/// <summary>How customers sign in with an emailed code, bound from the <c>SignIn</c> section.</summary>
public sealed class SignInOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "SignIn";

	/// <summary>How long a code works: long enough to open the email, short enough to be worthless when leaked.</summary>
	[Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
	public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Wrong attempts a code allows. Five guesses at a million combinations is a one-in-200,000 chance per code, and
	/// codes are sent at most once a <see cref="ResendCooldown"/>.
	/// </summary>
	[Range(1, 10)]
	public int MaxAttempts { get; set; } = 5;

	/// <summary>The least time between two codes for the same customer: no inbox gets flooded.</summary>
	[Range(typeof(TimeSpan), "00:00:10", "00:30:00")]
	public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>How long a session lasts: enough to look at, cancel or move a booking.</summary>
	[Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
	public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>A signed-in customer, as the endpoints and the assistant see them.</summary>
/// <param name="CustomerId">Who.</param>
/// <param name="BusinessId">Of which business.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email.</param>
/// <param name="SessionExpiresAt">When the session ends.</param>
public sealed record SignedInCustomer(CustomerId CustomerId, BusinessId BusinessId, string Name, string Email, Instant SessionExpiresAt);

/// <summary>A started session: the token goes into the cookie.</summary>
/// <param name="Token">The session token, returned once.</param>
/// <param name="Customer">Who signed in.</param>
public sealed record SignInResult(string Token, SignedInCustomer Customer);

/// <summary>Codes, sessions and the customers they belong to.</summary>
public interface ISignInStore
{
	/// <summary>The business's name, for the email; <see langword="null"/> when there is no such business.</summary>
	/// <param name="business">The business.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The name.</returns>
	Task<string?> BusinessNameAsync(BusinessId business, CancellationToken cancellationToken);

	/// <summary>A customer by email.</summary>
	/// <param name="business">The business.</param>
	/// <param name="normalizedEmail">The email as <see cref="Customer.NormalizeEmail"/> writes it.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The customer, or <see langword="null"/>.</returns>
	Task<Customer?> FindCustomerAsync(BusinessId business, string normalizedEmail, CancellationToken cancellationToken);

	/// <summary>A customer by id.</summary>
	/// <param name="customer">The id.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The customer, or <see langword="null"/>.</returns>
	Task<Customer?> CustomerAsync(CustomerId customer, CancellationToken cancellationToken);

	/// <summary>The customer's latest code, tracked.</summary>
	/// <param name="customer">The customer.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The code, or <see langword="null"/>.</returns>
	Task<SignInCode?> LatestCodeAsync(CustomerId customer, CancellationToken cancellationToken);

	/// <summary>A session by its token's hash, tracked.</summary>
	/// <param name="tokenHash">The hash.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The session, or <see langword="null"/>.</returns>
	Task<CustomerSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken);

	/// <summary>Stages a code.</summary>
	/// <param name="code">The code.</param>
	void Add(SignInCode code);

	/// <summary>Stages a session.</summary>
	/// <param name="session">The session.</param>
	void Add(CustomerSession session);

	/// <summary>Saves.</summary>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Delivers a sign-in code. Throws when it could not.</summary>
public interface ISignInCodeSender
{
	/// <summary>Sends it.</summary>
	/// <param name="customer">To whom.</param>
	/// <param name="businessName">From which business.</param>
	/// <param name="digits">The code.</param>
	/// <param name="lifetime">How long it works, to say so.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	Task SendAsync(Customer customer, string businessName, string digits, Duration lifetime, CancellationToken cancellationToken);
}

/// <summary>The sign-in error codes (docs/errors.md).</summary>
public static class SignInErrors
{
	/// <summary>The email is not a plausible address.</summary>
	public const string InvalidEmail = "signin.invalid_email";

	/// <summary>Not the code; it may be tried again.</summary>
	public const string InvalidCode = "signin.invalid_code";

	/// <summary>The code expired, was used or ran out of attempts.</summary>
	public const string CodeExpired = "signin.code_expired";

	/// <summary>Not signed in, or the session ended.</summary>
	public const string Required = "signin.required";

	/// <summary>The code could not be sent.</summary>
	public const string Unavailable = "signin.unavailable";
}

/// <summary>
/// Emails a sign-in code to a customer of the business. The answer is the same whether the email has bookings or
/// not, so it cannot be used to find out who is a customer; only existing customers get a code, at most one a
/// minute, so it cannot be used to flood an inbox either.
/// </summary>
/// <param name="store">Codes and customers.</param>
/// <param name="sender">The email.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">Lifetimes and limits.</param>
public sealed class RequestSignInCode(ISignInStore store, ISignInCodeSender sender, IClock clock, IOptions<SignInOptions> options)
{
	/// <summary>Sends the code when there is someone to send it to.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="email">The email typed.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>Success either way, or a bad email, or the email could not be sent.</returns>
	public async Task<Result<bool>> HandleAsync(Guid businessId, string? email, CancellationToken cancellationToken)
	{
		var normalized = Customer.NormalizeEmail(email ?? string.Empty);
		if (!Customer.IsEmail(normalized))
		{
			return Error.Validation(SignInErrors.InvalidEmail, "Give the email your bookings are under.");
		}

		var business = new BusinessId(businessId);
		var name = await store.BusinessNameAsync(business, cancellationToken).ConfigureAwait(false);
		var customer = name is null ? null : await store.FindCustomerAsync(business, normalized, cancellationToken).ConfigureAwait(false);
		if (customer is null)
		{
			return true;
		}

		var settings = options.Value;
		var now = clock.GetCurrentInstant();
		var latest = await store.LatestCodeAsync(customer.Id, cancellationToken).ConfigureAwait(false);
		if (latest is not null && now - latest.CreatedAt < Duration.FromTimeSpan(settings.ResendCooldown))
		{
			return true;
		}

		var lifetime = Duration.FromTimeSpan(settings.CodeLifetime);
		var (code, digits) = SignInCode.Issue(business, customer.Id, now, lifetime);
		store.Add(code);
		await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await sender.SendAsync(customer, name!, digits, lifetime, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return Error.Unavailable(SignInErrors.Unavailable, "The code could not be sent; try again in a minute.");
		}

		return true;
	}
}

/// <summary>Checks a code and, when it is right, starts a session.</summary>
/// <param name="store">Codes, sessions and customers.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">Lifetimes and limits.</param>
public sealed class VerifySignInCode(ISignInStore store, IClock clock, IOptions<SignInOptions> options)
{
	/// <summary>Checks it.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="email">The email the code was sent to.</param>
	/// <param name="digits">The code typed.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The session, or why not. An unknown email reads as a wrong code.</returns>
	public async Task<Result<SignInResult>> HandleAsync(Guid businessId, string? email, string? digits, CancellationToken cancellationToken)
	{
		var settings = options.Value;
		var customer = await store.FindCustomerAsync(new BusinessId(businessId), Customer.NormalizeEmail(email ?? string.Empty), cancellationToken).ConfigureAwait(false);
		var code = customer is null ? null : await store.LatestCodeAsync(customer.Id, cancellationToken).ConfigureAwait(false);
		if (customer is null || code is null)
		{
			return Error.Validation(SignInErrors.InvalidCode, "That is not the code we sent.");
		}

		var now = clock.GetCurrentInstant();
		var check = code.Check(digits ?? string.Empty, now, settings.MaxAttempts);
		if (check != CodeCheck.Accepted)
		{
			await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			return check == CodeCheck.Wrong
				? Error.Validation(SignInErrors.InvalidCode, "That is not the code we sent.")
				: Error.Validation(SignInErrors.CodeExpired, "That code no longer works; ask for a new one.");
		}

		var (session, token) = CustomerSession.Start(customer.BusinessId, customer.Id, now, Duration.FromTimeSpan(settings.SessionLifetime));
		store.Add(session);
		await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		return new SignInResult(token, new SignedInCustomer(customer.Id, customer.BusinessId, customer.Name, customer.Email, session.ExpiresAt));
	}
}

/// <summary>Who a session token belongs to; and signing out.</summary>
/// <param name="store">Sessions and customers.</param>
/// <param name="clock">The clock.</param>
public sealed class CustomerSessions(ISignInStore store, IClock clock)
{
	/// <summary>The customer behind a token, if the session is active and of this business.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="token">The token from the cookie.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The customer, or <see langword="null"/>.</returns>
	public async Task<SignedInCustomer?> ResolveAsync(Guid businessId, string? token, CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(token))
		{
			return null;
		}

		var session = await store.FindSessionAsync(CustomerSession.HashToken(token), cancellationToken).ConfigureAwait(false);
		if (session is null || !session.IsActive(clock.GetCurrentInstant()) || session.BusinessId != new BusinessId(businessId))
		{
			return null;
		}

		var customer = await store.CustomerAsync(session.CustomerId, cancellationToken).ConfigureAwait(false);
		return customer is null ? null : new SignedInCustomer(customer.Id, customer.BusinessId, customer.Name, customer.Email, session.ExpiresAt);
	}

	/// <summary>Ends the session of a token, if there is one.</summary>
	/// <param name="token">The token from the cookie.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	public async Task SignOutAsync(string? token, CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(token) || await store.FindSessionAsync(CustomerSession.HashToken(token), cancellationToken).ConfigureAwait(false) is not { } session)
		{
			return;
		}

		session.End(clock.GetCurrentInstant());
		await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}
}
