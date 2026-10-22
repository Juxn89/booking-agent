namespace BookingAgent.Application.Common;

/// <summary>
/// The database refused a booking because its period overlaps one that another request saved first: the exclusion
/// constraint is the last word on double booking, whatever the checks before it saw.
/// </summary>
public sealed class SlotTakenException : Exception
{
	/// <summary>Creates the exception.</summary>
	public SlotTakenException()
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	public SlotTakenException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	/// <param name="innerException">The database error.</param>
	public SlotTakenException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

/// <summary>A booking changed between being loaded and being saved, e.g. a hold expired while it was confirmed.</summary>
public sealed class BookingChangedException : Exception
{
	/// <summary>Creates the exception.</summary>
	public BookingChangedException()
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	public BookingChangedException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	/// <param name="innerException">The database error.</param>
	public BookingChangedException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
