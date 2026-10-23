namespace BookingAgent.Application.Common;

/// <summary>How the API reports a failure. Maps 1:1 to an HTTP status in the endpoint layer.</summary>
public enum ErrorKind
{
	/// <summary>The request is wrong: 400.</summary>
	Validation = 0,

	/// <summary>What was asked for does not exist: 404.</summary>
	NotFound = 1,

	/// <summary>The request conflicts with the current state, e.g. confirming a hold that has expired: 409.</summary>
	Conflict = 2,

	/// <summary>The service cannot answer now, e.g. no model is configured or the day's budget is spent: 503.</summary>
	Unavailable = 3,

	/// <summary>The caller is sending too much, too fast: 429.</summary>
	TooManyRequests = 4,
}

/// <summary>An expected failure: something the caller can act on. Bugs are exceptions, not errors.</summary>
/// <param name="Code">Stable, dotted, e.g. <c>availability.unknown_service</c>.</param>
/// <param name="Message">For a person.</param>
/// <param name="Kind">How it maps to HTTP.</param>
/// <param name="Details">What the caller needs to act on it, e.g. the slots nearest to one that was taken.</param>
public sealed record Error(string Code, string Message, ErrorKind Kind, IReadOnlyDictionary<string, string>? Details = null)
{
	/// <summary>A 400.</summary>
	/// <param name="code">The code.</param>
	/// <param name="message">The message.</param>
	/// <returns>The error.</returns>
	public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

	/// <summary>A 404.</summary>
	/// <param name="code">The code.</param>
	/// <param name="message">The message.</param>
	/// <returns>The error.</returns>
	public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

	/// <summary>A 409.</summary>
	/// <param name="code">The code.</param>
	/// <param name="message">The message.</param>
	/// <param name="details">What the caller needs to act on it.</param>
	/// <returns>The error.</returns>
	public static Error Conflict(string code, string message, IReadOnlyDictionary<string, string>? details = null) =>
		new(code, message, ErrorKind.Conflict, details);

	/// <summary>A 503.</summary>
	/// <param name="code">The code.</param>
	/// <param name="message">The message.</param>
	/// <returns>The error.</returns>
	public static Error Unavailable(string code, string message) => new(code, message, ErrorKind.Unavailable);

	/// <summary>A 429.</summary>
	/// <param name="code">The code.</param>
	/// <param name="message">The message.</param>
	/// <returns>The error.</returns>
	public static Error TooManyRequests(string code, string message) => new(code, message, ErrorKind.TooManyRequests);
}

/// <summary>A value or an <see cref="Error"/>.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class Result<T>
{
	private readonly T? _value;

	private Result(T? value, Error? error)
	{
		_value = value;
		Error = error;
	}

	/// <summary>The failure, when there is one.</summary>
	public Error? Error { get; }

	/// <summary>No error.</summary>
	public bool IsSuccess => Error is null;

	/// <summary>The value.</summary>
	/// <exception cref="InvalidOperationException">The result is a failure.</exception>
	public T Value => IsSuccess
		? _value!
		: throw new InvalidOperationException($"Result is a failure ({Error!.Code}); there is no value.");

	/// <summary>A success.</summary>
	/// <param name="value">The value.</param>
	/// <returns>The result.</returns>
	public static Result<T> Success(T value) => new(value, null);

	/// <summary>A failure.</summary>
	/// <param name="error">The error.</param>
	/// <returns>The result.</returns>
	public static Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

#pragma warning disable CA2225 // Success/Failure are the named alternates.
	/// <summary>Wraps a value.</summary>
	/// <param name="value">The value.</param>
	public static implicit operator Result<T>(T value) => Success(value);

	/// <summary>Wraps an error.</summary>
	/// <param name="error">The error.</param>
	public static implicit operator Result<T>(Error error) => Failure(error);
#pragma warning restore CA2225
}
