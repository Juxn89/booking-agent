namespace BookingAgent.Application.Features.Assistant.Tools;

using System.Globalization;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Domain.Scheduling;
using Microsoft.Extensions.AI;
using NodaTime;
using NodaTime.Text;

/// <summary>
/// The assistant's tools. Each one reads or proposes through the same use cases as the form; none confirms,
/// cancels or moves anything (an architecture test keeps <see cref="ConfirmHold"/> out of this namespace).
/// Finding, cancelling and moving a customer's own bookings come with the customer's sign-in code (day 7).
/// </summary>
/// <param name="services">Lists the services.</param>
/// <param name="professionals">Lists the professionals.</param>
/// <param name="dates">Resolves relative dates.</param>
/// <param name="availability">Finds free slots.</param>
/// <param name="hold">Holds a slot.</param>
public sealed class BookingTools(
	ListServicesTool services,
	ListProfessionalsTool professionals,
	ResolveDateTool dates,
	CheckAvailabilityTool availability,
	HoldSlotTool hold)
{
	/// <summary>Every tool, in the order the prompt introduces them.</summary>
	public IList<AITool> All { get; } = [services, professionals, dates, availability, hold];
}

/// <summary>No input.</summary>
public sealed record NoInput : IToolInput
{
	/// <inheritdoc />
	public string? Problem() => null;
}

/// <summary><c>list_services</c>: what the business offers, how long it takes and what it costs.</summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
public sealed class ListServicesTool(AgentTurn turn, IClock clock) : AssistantTool<NoInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "list_services";

	/// <inheritdoc />
	public override string Description => "Lists the services the business offers, with their id, length in minutes and price.";

	/// <inheritdoc />
	protected override string Schema => """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

	/// <inheritdoc />
	protected override Task<object> RunAsync(NoInput input, CancellationToken cancellationToken) =>
		Task.FromResult<object>(new
		{
			Services = Turn.Business.Catalog.Services.Select(static s => new { Id = s.Id.Value, s.Name, DurationMinutes = (int)s.Duration.TotalMinutes, s.Price }),
		});
}

/// <summary>The input of <c>list_professionals</c>.</summary>
/// <param name="ServiceId">Only those who offer this service, or everyone when null.</param>
public sealed record ListProfessionalsInput(string? ServiceId) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() =>
		ServiceId is null || Guid.TryParse(ServiceId, out _) ? null : "service_id must be a service id from list_services, or null.";
}

/// <summary><c>list_professionals</c>: who works there and which services each one offers.</summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
public sealed class ListProfessionalsTool(AgentTurn turn, IClock clock) : AssistantTool<ListProfessionalsInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "list_professionals";

	/// <inheritdoc />
	public override string Description =>
		"Lists the professionals, with their id, specialty and the services they offer. Pass a service_id to get only those who offer it.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"service_id":{"type":["string","null"],"description":"A service id from list_services, or null for everyone."}},"required":["service_id"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override Task<object> RunAsync(ListProfessionalsInput input, CancellationToken cancellationToken)
	{
		var catalog = Turn.Business.Catalog;
		var wanted = input.ServiceId is null ? (Guid?)null : Guid.Parse(input.ServiceId);
		var names = catalog.Services.ToDictionary(static s => s.Id.Value, static s => s.Name);
		return Task.FromResult<object>(new
		{
			Professionals = catalog.Professionals
				.Where(p => wanted is null || p.Services.Any(s => s.Value == wanted))
				.Select(p => new { Id = p.Id.Value, p.Name, p.Specialty, Services = p.Services.Select(s => new { Id = s.Value, Name = names.GetValueOrDefault(s.Value) }) }),
		});
	}
}

/// <summary>The input of <c>resolve_date</c>.</summary>
/// <param name="Expression">What the customer wrote for the day.</param>
public sealed record ResolveDateInput(string? Expression) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() => string.IsNullOrWhiteSpace(Expression) ? "expression is required: the words the customer used for the day." : null;
}

/// <summary><c>resolve_date</c>: "mañana", "el martes que viene", "27 de octubre" as a date on the business's calendar.</summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
public sealed class ResolveDateTool(AgentTurn turn, IClock clock) : AssistantTool<ResolveDateInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "resolve_date";

	/// <inheritdoc />
	public override string Description =>
		"Turns the customer's words for a day (\"mañana\", \"el martes que viene\", \"27 de octubre\") into a date on the business's calendar. " +
		"Always use it instead of working dates out yourself. Pass only the day, without the time.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"expression":{"type":"string","description":"The customer's words for the day, in Spanish, e.g. \"el martes que viene\"."}},"required":["expression"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override Task<object> RunAsync(ResolveDateInput input, CancellationToken cancellationToken)
	{
		var today = Turn.Business.Today;
		if (DateExpression.Resolve(input.Expression, today) is not { } date)
		{
			return Task.FromResult(Problem(
				$"\"{input.Expression}\" is not one day. Ask the customer for a specific day, such as \"el martes 27\" or \"mañana\"."));
		}

		if (date < today)
		{
			return Task.FromResult(Problem($"{Describe(date)} has already passed. Ask the customer for a day from today on."));
		}

		return Task.FromResult<object>(new { Date = LocalDatePattern.Iso.Format(date), Label = Describe(date), DaysFromToday = Period.Between(today, date, PeriodUnits.Days).Days });
	}

	private static string Describe(LocalDate date)
	{
		var when = NotificationTemplates.When(date.AtMidnight());
		return when[..when.IndexOf(" a las ", StringComparison.Ordinal)];
	}
}

/// <summary>The input of <c>check_availability</c>.</summary>
/// <param name="ProfessionalId">The professional.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="Date">The day, ISO.</param>
public sealed record CheckAvailabilityInput(string? ProfessionalId, string? ServiceId, string? Date) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() =>
		!Guid.TryParse(ProfessionalId, out _) ? "professional_id must be an id from list_professionals."
		: !Guid.TryParse(ServiceId, out _) ? "service_id must be an id from list_services."
		: !LocalDatePattern.Iso.Parse(Date ?? string.Empty).Success ? "date must be a date like 2026-10-27; use resolve_date for the customer's words."
		: null;
}

/// <summary><c>check_availability</c>: the free start times of a service with a professional on one day.</summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
/// <param name="useCase">The availability, as the form sees it.</param>
public sealed class CheckAvailabilityTool(AgentTurn turn, IClock clock, FindAvailableSlots useCase) : AssistantTool<CheckAvailabilityInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "check_availability";

	/// <inheritdoc />
	public override string Description =>
		"Lists the free start times of a service with a professional on one day, on the business's clock. " +
		"Only offer times this returned; each comes with the start to pass to hold_slot.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"professional_id":{"type":"string","description":"From list_professionals."},"service_id":{"type":"string","description":"From list_services."},"date":{"type":"string","description":"The day, as resolve_date returned it, e.g. 2026-10-27."}},"required":["professional_id","service_id","date"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override async Task<object> RunAsync(CheckAvailabilityInput input, CancellationToken cancellationToken)
	{
		var date = LocalDatePattern.Iso.Parse(input.Date!).Value;
		if (date < Turn.Business.Today)
		{
			return Problem("That day has passed. Ask for a day from today on.");
		}

		var result = await useCase.HandleAsync(new AvailabilityQuery(Guid.Parse(input.ProfessionalId!), Guid.Parse(input.ServiceId!), date, date), cancellationToken).ConfigureAwait(false);
		if (!result.IsSuccess)
		{
			return Problem("That professional does not offer that service. Call list_professionals with the service_id to see who does.");
		}

		var slots = result.Value.Select(static s => new { Time = s.LocalStart.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture), Start = InstantPattern.ExtendedIso.Format(s.Period.Start) }).ToList();
		var note = slots.Count == 0 ? "No free times that day. Offer another day, or another professional who offers the service." : null;
		return new { input.Date, Slots = slots, Note = note };
	}
}

/// <summary>The input of <c>hold_slot</c>.</summary>
/// <param name="ProfessionalId">The professional.</param>
/// <param name="ServiceId">The service.</param>
/// <param name="Start">The start, as check_availability returned it.</param>
/// <param name="CustomerName">The customer's name.</param>
/// <param name="CustomerEmail">The customer's email.</param>
/// <param name="CustomerPhone">The customer's phone, or null.</param>
public sealed record HoldSlotInput(string? ProfessionalId, string? ServiceId, string? Start, string? CustomerName, string? CustomerEmail, string? CustomerPhone) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() =>
		!Guid.TryParse(ProfessionalId, out _) ? "professional_id must be an id from list_professionals."
		: !Guid.TryParse(ServiceId, out _) ? "service_id must be an id from list_services."
		: !InstantPattern.ExtendedIso.Parse(Start ?? string.Empty).Success ? "start must be a start exactly as check_availability returned it."
		: string.IsNullOrWhiteSpace(CustomerName) || string.IsNullOrWhiteSpace(CustomerEmail) ? "Ask the customer for their name and email before holding a slot."
		: null;
}

/// <summary>
/// <c>hold_slot</c>: holds a slot for a few minutes. The confirmation token goes to the widget's card, never to the
/// model; the customer confirms with the card's button.
/// </summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
/// <param name="useCase">The form's hold.</param>
public sealed class HoldSlotTool(AgentTurn turn, IClock clock, HoldSlot useCase) : AssistantTool<HoldSlotInput>(turn, clock)
{
	/// <summary>Holds in one reply: two appointments at once is plausible, a dozen is a loop.</summary>
	public const int MaxHoldsPerTurn = 3;

	/// <inheritdoc />
	public override string Name => "hold_slot";

	/// <inheritdoc />
	public override string Description =>
		"Holds a free time for the customer for a few minutes, once they have chosen it and given their name and email. " +
		"It does not confirm the booking: the customer confirms it with the Confirmar button on the card this shows them.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"professional_id":{"type":"string"},"service_id":{"type":"string"},"start":{"type":"string","description":"Exactly as check_availability returned it."},"customer_name":{"type":"string"},"customer_email":{"type":"string"},"customer_phone":{"type":["string","null"]}},"required":["professional_id","service_id","start","customer_name","customer_email","customer_phone"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override async Task<object> RunAsync(HoldSlotInput input, CancellationToken cancellationToken)
	{
		if (Turn.Holds.Count >= MaxHoldsPerTurn)
		{
			return Problem($"At most {MaxHoldsPerTurn} slots can be held in one reply. Ask the customer to confirm the ones held first.");
		}

		var request = new HoldRequest(
			Guid.Parse(input.ProfessionalId!),
			Guid.Parse(input.ServiceId!),
			InstantPattern.ExtendedIso.Parse(input.Start!).Value,
			new CustomerDetails(input.CustomerName!, input.CustomerEmail!, input.CustomerPhone));
		var result = await useCase.HandleAsync(request, cancellationToken).ConfigureAwait(false);
		if (!result.IsSuccess)
		{
			var error = result.Error!;
			return error.Code switch
			{
				BookingErrors.InvalidCustomer => Problem("The email does not look valid. Ask the customer to check it."),
				BookingErrors.SlotUnavailable or BookingErrors.SlotTaken => new
				{
					Error = "That time is no longer free. Tell the customer and offer these nearest free times instead.",
					Alternatives = Alternatives(error.Details),
				},
				_ => Problem("That professional does not offer that service. Call list_professionals with the service_id to see who does."),
			};
		}

		var hold = result.Value;
		var catalog = Turn.Business.Catalog;
		var service = catalog.Services.Single(s => s.Id.Value == hold.Booking.ServiceId).Name;
		var professional = catalog.Professionals.Single(p => p.Id.Value == hold.Booking.ProfessionalId).Name;
		Turn.Held(new HoldCard(hold.Booking.Id, hold.ConfirmationToken, service, professional, hold.Booking.LocalStart, hold.Booking.HoldExpiresAt!.Value));

		return new
		{
			HoldId = hold.Booking.Id,
			Summary = $"{service} con {professional}, {NotificationTemplates.When(hold.Booking.LocalStart)}",
			HeldForMinutes = (int)Math.Round((hold.Booking.HoldExpiresAt!.Value - Clock.GetCurrentInstant()).TotalMinutes),
			NextStep = "Tell the customer the time is held for them and that they confirm it with the Confirmar button on the card below. You cannot confirm it.",
		};
	}

	private IEnumerable<object> Alternatives(IReadOnlyDictionary<string, string>? details) =>
		(details?.GetValueOrDefault(BookingErrors.AlternativesDetail) ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(static text => InstantPattern.General.Parse(text))
			.Where(static parsed => parsed.Success)
			.Select(parsed =>
			{
				var local = parsed.Value.InZone(Turn.Business.Zone).LocalDateTime;
				return (object)new { Day = LocalDatePattern.Iso.Format(local.Date), Time = local.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture), Start = InstantPattern.ExtendedIso.Format(parsed.Value) };
			})
			.ToList();
}
