namespace BookingAgent.Domain.Catalog;

using BookingAgent.Domain.Scheduling;
using NodaTime;

/// <summary>A business and the rules its calendar follows.</summary>
public sealed class Business
{
	private Business(BusinessId id, string name, string timeZoneId, Duration slotGranularity, Duration minimumNotice)
	{
		Id = id;
		Name = name;
		TimeZoneId = timeZoneId;
		SlotGranularity = slotGranularity;
		MinimumNotice = minimumNotice;
	}

	/// <summary>The id.</summary>
	public BusinessId Id { get; private set; }

	/// <summary>The name customers see.</summary>
	public string Name { get; private set; }

	/// <summary>The IANA time zone opening hours are written in, e.g. <c>America/Managua</c>.</summary>
	public string TimeZoneId { get; private set; }

	/// <summary>The step between possible start times.</summary>
	public Duration SlotGranularity { get; private set; }

	/// <summary>How far ahead of now the earliest bookable start is.</summary>
	public Duration MinimumNotice { get; private set; }

	/// <summary>The business's clock.</summary>
	public DateTimeZone Zone => DateTimeZoneProviders.Tzdb[TimeZoneId];

	/// <summary>How slots are laid out.</summary>
	public SlotRules Rules => new(SlotGranularity, MinimumNotice);

	/// <summary>Registers a business.</summary>
	/// <param name="id">Its id.</param>
	/// <param name="name">Its name.</param>
	/// <param name="timeZoneId">An IANA zone id.</param>
	/// <param name="slotGranularity">The step between start times, positive.</param>
	/// <param name="minimumNotice">The notice, zero or more.</param>
	/// <returns>The business.</returns>
	/// <exception cref="ArgumentException">An unknown zone or a bad duration.</exception>
	public static Business Create(BusinessId id, string name, string timeZoneId, Duration slotGranularity, Duration minimumNotice)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is null)
		{
			throw new ArgumentException($"'{timeZoneId}' is not a time zone.", nameof(timeZoneId));
		}

		if (slotGranularity <= Duration.Zero || minimumNotice < Duration.Zero)
		{
			throw new ArgumentException("The granularity must be positive and the notice not negative.", nameof(slotGranularity));
		}

		return new Business(id, name.Trim(), timeZoneId, slotGranularity, minimumNotice);
	}
}

/// <summary>A service: what is booked, and how long it takes.</summary>
public sealed class Service
{
	private Service(ServiceId id, BusinessId businessId, string name, Duration duration, decimal price)
	{
		Id = id;
		BusinessId = businessId;
		Name = name;
		Duration = duration;
		Price = price;
	}

	/// <summary>The id.</summary>
	public ServiceId Id { get; private set; }

	/// <summary>The business that offers it.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>The name customers see.</summary>
	public string Name { get; private set; }

	/// <summary>How long an appointment for it lasts.</summary>
	public Duration Duration { get; private set; }

	/// <summary>Its price, in the business's currency.</summary>
	public decimal Price { get; private set; }

	/// <summary>Registers a service.</summary>
	/// <param name="id">Its id.</param>
	/// <param name="businessId">The business.</param>
	/// <param name="name">Its name.</param>
	/// <param name="duration">How long it takes, positive.</param>
	/// <param name="price">Its price, not negative.</param>
	/// <returns>The service.</returns>
	public static Service Create(ServiceId id, BusinessId businessId, string name, Duration duration, decimal price)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentOutOfRangeException.ThrowIfNegative(price);
		if (duration <= Duration.Zero)
		{
			throw new ArgumentException("A service lasts some time.", nameof(duration));
		}

		return new Service(id, businessId, name.Trim(), duration, price);
	}
}

/// <summary>A professional, the services they offer and the hours they work.</summary>
public sealed class Professional
{
	private readonly List<WorkingHours> _hours = [];
	private readonly List<ServiceId> _services = [];

	private Professional(ProfessionalId id, BusinessId businessId, string name, string specialty)
	{
		Id = id;
		BusinessId = businessId;
		Name = name;
		Specialty = specialty;
	}

	/// <summary>The id.</summary>
	public ProfessionalId Id { get; private set; }

	/// <summary>The business they work for.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>The name customers see, e.g. "Dra. López".</summary>
	public string Name { get; private set; }

	/// <summary>What they do, e.g. "Odontología".</summary>
	public string Specialty { get; private set; }

	/// <summary>Their weekly hours.</summary>
	public IReadOnlyList<WorkingHours> Hours => _hours;

	/// <summary>The services they offer.</summary>
	public IReadOnlyList<ServiceId> Services => _services;

	/// <summary>Their week as a schedule.</summary>
	public WeeklySchedule Schedule => new(_hours);

	/// <summary>Registers a professional.</summary>
	/// <param name="id">Their id.</param>
	/// <param name="businessId">The business.</param>
	/// <param name="name">Their name.</param>
	/// <param name="specialty">Their specialty.</param>
	/// <param name="hours">Their weekly hours, validated as a <see cref="WeeklySchedule"/>.</param>
	/// <param name="services">The services they offer.</param>
	/// <returns>The professional.</returns>
	public static Professional Create(
		ProfessionalId id,
		BusinessId businessId,
		string name,
		string specialty,
		IEnumerable<WorkingHours> hours,
		IEnumerable<ServiceId> services)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var professional = new Professional(id, businessId, name.Trim(), specialty?.Trim() ?? string.Empty);
		professional._hours.AddRange(hours);
		professional._services.AddRange(services.Distinct());
		_ = professional.Schedule;
		return professional;
	}

	/// <summary>Whether they offer a service.</summary>
	/// <param name="service">The service.</param>
	/// <returns><see langword="true"/> when they do.</returns>
	public bool Offers(ServiceId service) => _services.Contains(service);
}

/// <summary>A period a professional does not work: vacation, a course, a sick day.</summary>
public sealed class TimeOff
{
	private TimeOff(Guid id, ProfessionalId professionalId, Interval period, string reason)
	{
		Id = id;
		ProfessionalId = professionalId;
		Period = period;
		Reason = reason;
	}

	/// <summary>The id.</summary>
	public Guid Id { get; private set; }

	/// <summary>Whose time off it is.</summary>
	public ProfessionalId ProfessionalId { get; private set; }

	/// <summary>When.</summary>
	public Interval Period { get; private set; }

	/// <summary>Why, for the business only.</summary>
	public string Reason { get; private set; }

	/// <summary>Records time off.</summary>
	/// <param name="professionalId">Whose.</param>
	/// <param name="period">When.</param>
	/// <param name="reason">Why.</param>
	/// <returns>The time off.</returns>
	public static TimeOff Create(ProfessionalId professionalId, Interval period, string reason) =>
		new(Guid.CreateVersion7(), professionalId, period, reason?.Trim() ?? string.Empty);
}

/// <summary>Someone who books: identified by their email within a business.</summary>
public sealed class Customer
{
	/// <summary>Longest name or email kept.</summary>
	public const int MaxLength = 200;

	private Customer(CustomerId id, BusinessId businessId, string name, string email, string? phone)
	{
		Id = id;
		BusinessId = businessId;
		Name = name;
		Email = email;
		Phone = phone;
	}

	/// <summary>The id.</summary>
	public CustomerId Id { get; private set; }

	/// <summary>The business.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>Their name.</summary>
	public string Name { get; private set; }

	/// <summary>Their email, lower-cased: how they are found again.</summary>
	public string Email { get; private set; }

	/// <summary>Their phone, if they gave one.</summary>
	public string? Phone { get; private set; }

	/// <summary>Registers a customer.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="name">Their name.</param>
	/// <param name="email">Their email.</param>
	/// <param name="phone">Their phone, optional.</param>
	/// <returns>The customer.</returns>
	/// <exception cref="ArgumentException">A missing name, or an email without an @.</exception>
	public static Customer Create(BusinessId businessId, string name, string email, string? phone)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(email);
		var normalized = NormalizeEmail(email);
		if (!IsEmail(normalized) || name.Trim().Length > MaxLength || normalized.Length > MaxLength)
		{
			throw new ArgumentException("A customer needs a name and a valid email.", nameof(email));
		}

		return new Customer(CustomerId.New(), businessId, name.Trim(), normalized, string.IsNullOrWhiteSpace(phone) ? null : phone.Trim());
	}

	/// <summary>The form an email is stored and looked up in.</summary>
	/// <param name="email">The email as typed.</param>
	/// <returns>It trimmed and lower-cased.</returns>
	public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

	/// <summary>A plausible address: something, an @, a domain with a dot. Delivery is what really checks it.</summary>
	/// <param name="email">A normalized email.</param>
	/// <returns><see langword="true"/> when plausible.</returns>
	public static bool IsEmail(string email)
	{
		var at = email.IndexOf('@', StringComparison.Ordinal);
		return at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.') && !email.Contains(' ', StringComparison.Ordinal);
	}
}
