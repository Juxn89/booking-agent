namespace BookingAgent.Domain;

/// <summary>A business: one clinic, salon or workshop, with its own clock.</summary>
/// <param name="Value">The id.</param>
public readonly record struct BusinessId(Guid Value)
{
	/// <summary>A new id, time-ordered so inserts stay at the end of the index.</summary>
	/// <returns>The id.</returns>
	public static BusinessId New() => new(Guid.CreateVersion7());
}

/// <summary>A professional who takes appointments.</summary>
/// <param name="Value">The id.</param>
public readonly record struct ProfessionalId(Guid Value)
{
	/// <summary>A new id.</summary>
	/// <returns>The id.</returns>
	public static ProfessionalId New() => new(Guid.CreateVersion7());
}

/// <summary>A service the business offers.</summary>
/// <param name="Value">The id.</param>
public readonly record struct ServiceId(Guid Value)
{
	/// <summary>A new id.</summary>
	/// <returns>The id.</returns>
	public static ServiceId New() => new(Guid.CreateVersion7());
}

/// <summary>A customer of the business.</summary>
/// <param name="Value">The id.</param>
public readonly record struct CustomerId(Guid Value)
{
	/// <summary>A new id.</summary>
	/// <returns>The id.</returns>
	public static CustomerId New() => new(Guid.CreateVersion7());
}

/// <summary>A booking, held or confirmed.</summary>
/// <param name="Value">The id.</param>
public readonly record struct BookingId(Guid Value)
{
	/// <summary>A new id.</summary>
	/// <returns>The id.</returns>
	public static BookingId New() => new(Guid.CreateVersion7());
}
