namespace BookingAgent.Domain.Customers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

/// <summary>What checking a sign-in code did.</summary>
public enum CodeCheck
{
	/// <summary>The right code, in time: the customer is who they say.</summary>
	Accepted = 1,

	/// <summary>Not the code; one attempt fewer.</summary>
	Wrong = 2,

	/// <summary>Expired, used, or out of attempts: a new code is needed.</summary>
	Spent = 3,
}

/// <summary>
/// A six-digit code emailed to a customer to prove they own the address their bookings are under. It lasts a few
/// minutes, allows a few attempts and works once; only its hash is stored, salted with the code's id.
/// </summary>
public sealed class SignInCode
{
	/// <summary>Digits in a code.</summary>
	public const int Digits = 6;

	private const int Range = 1_000_000;

	private SignInCode(Guid id, BusinessId businessId, CustomerId customerId, string codeHash, Instant createdAt, Instant expiresAt)
	{
		Id = id;
		BusinessId = businessId;
		CustomerId = customerId;
		CodeHash = codeHash;
		CreatedAt = createdAt;
		ExpiresAt = expiresAt;
	}

	/// <summary>The id.</summary>
	public Guid Id { get; private set; }

	/// <summary>The business.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>Who asked for it.</summary>
	public CustomerId CustomerId { get; private set; }

	/// <summary>SHA-256 of the id and the code, hex.</summary>
	public string CodeHash { get; private set; }

	/// <summary>When it was sent.</summary>
	public Instant CreatedAt { get; private set; }

	/// <summary>When it stops working.</summary>
	public Instant ExpiresAt { get; private set; }

	/// <summary>Wrong attempts so far.</summary>
	public int FailedAttempts { get; private set; }

	/// <summary>When it was accepted.</summary>
	public Instant? UsedAt { get; private set; }

	/// <summary>Issues a code.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="customerId">The customer.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="lifetime">How long it works.</param>
	/// <returns>The code, and its digits: sent once by email, never stored.</returns>
	public static (SignInCode Code, string Digits) Issue(BusinessId businessId, CustomerId customerId, Instant now, Duration lifetime)
	{
		var id = Guid.CreateVersion7();
		var digits = RandomNumberGenerator.GetInt32(Range).ToString("D6", CultureInfo.InvariantCulture);
		return (new SignInCode(id, businessId, customerId, Hash(id, digits), now, now + lifetime), digits);
	}

	/// <summary>Checks what the customer typed.</summary>
	/// <param name="digits">The code typed.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="maxAttempts">Wrong attempts allowed.</param>
	/// <returns>What happened.</returns>
	public CodeCheck Check(string digits, Instant now, int maxAttempts)
	{
		if (UsedAt is not null || now >= ExpiresAt || FailedAttempts >= maxAttempts)
		{
			return CodeCheck.Spent;
		}

		// Constant time: how much of a wrong code matched must not leak through the response time.
		var typed = Encoding.ASCII.GetBytes(Hash(Id, (digits ?? string.Empty).Trim()));
		if (!CryptographicOperations.FixedTimeEquals(typed, Encoding.ASCII.GetBytes(CodeHash)))
		{
			FailedAttempts++;
			return FailedAttempts >= maxAttempts ? CodeCheck.Spent : CodeCheck.Wrong;
		}

		UsedAt = now;
		return CodeCheck.Accepted;
	}

	private static string Hash(Guid id, string digits) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{id:N}:{digits}")));
}

/// <summary>
/// A signed-in customer's session: a random token in an HttpOnly cookie, stored as its hash. It ends at a fixed time
/// or when the customer signs out.
/// </summary>
public sealed class CustomerSession
{
	/// <summary>Random bytes in a session token: 256 bits.</summary>
	public const int TokenBytes = 32;

	private CustomerSession(Guid id, BusinessId businessId, CustomerId customerId, string tokenHash, Instant createdAt, Instant expiresAt)
	{
		Id = id;
		BusinessId = businessId;
		CustomerId = customerId;
		TokenHash = tokenHash;
		CreatedAt = createdAt;
		ExpiresAt = expiresAt;
	}

	/// <summary>The id.</summary>
	public Guid Id { get; private set; }

	/// <summary>The business.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>Who is signed in.</summary>
	public CustomerId CustomerId { get; private set; }

	/// <summary>SHA-256 of the token, hex.</summary>
	public string TokenHash { get; private set; }

	/// <summary>When it started.</summary>
	public Instant CreatedAt { get; private set; }

	/// <summary>When it ends.</summary>
	public Instant ExpiresAt { get; private set; }

	/// <summary>When the customer signed out.</summary>
	public Instant? EndedAt { get; private set; }

	/// <summary>Starts a session.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="customerId">The customer.</param>
	/// <param name="now">The current instant.</param>
	/// <param name="lifetime">How long it lasts.</param>
	/// <returns>The session, and its token: set once in the cookie, never stored.</returns>
	public static (CustomerSession Session, string Token) Start(BusinessId businessId, CustomerId customerId, Instant now, Duration lifetime)
	{
		var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return (new CustomerSession(Guid.CreateVersion7(), businessId, customerId, HashToken(token), now, now + lifetime), token);
	}

	/// <summary>The stored form of a token, to look a session up by.</summary>
	/// <param name="token">The token from the cookie.</param>
	/// <returns>Its hash.</returns>
	public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

	/// <summary>Whether it is still good.</summary>
	/// <param name="now">The current instant.</param>
	/// <returns><see langword="true"/> while it lasts and has not been ended.</returns>
	public bool IsActive(Instant now) => EndedAt is null && now < ExpiresAt;

	/// <summary>Ends it.</summary>
	/// <param name="now">The current instant.</param>
	public void End(Instant now) => EndedAt ??= now;
}
