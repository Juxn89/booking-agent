namespace BookingAgent.Application.Features.Assistant.Tools;

using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Application.Features.Notifications;
using NodaTime;
using NodaTime.Text;

/// <summary>What the tools about a customer's own bookings answer when nobody is signed in.</summary>
internal static class SignIn
{
	public const string Required =
		"The customer is not signed in, so their bookings cannot be seen or changed. Tell them to sign in with the code " +
		"we email them, using the form below your message, and then to ask again. Do not ask for the code yourself.";
}

/// <summary><c>find_my_bookings</c>: the signed-in customer's upcoming appointments.</summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
/// <param name="useCase">"Mis citas", as the web page lists them.</param>
public sealed class FindMyBookingsTool(AgentTurn turn, IClock clock, ListMyBookings useCase) : AssistantTool<NoInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "find_my_bookings";

	/// <inheritdoc />
	public override string Description =>
		"Lists the signed-in customer's upcoming confirmed appointments, with the ids to move or cancel them. " +
		"If the customer is not signed in, it says so and the chat shows them a sign-in form.";

	/// <inheritdoc />
	protected override string Schema => """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

	/// <inheritdoc />
	protected override async Task<object> RunAsync(NoInput input, CancellationToken cancellationToken)
	{
		if (Turn.Customer is not { } customer)
		{
			Turn.RequestSignIn();
			return Problem(SignIn.Required);
		}

		var bookings = await useCase.HandleAsync(customer, cancellationToken).ConfigureAwait(false);
		return new
		{
			Bookings = bookings.Select(static b => new
			{
				BookingId = b.Id,
				Service = b.ServiceName,
				b.ServiceId,
				Professional = b.ProfessionalName,
				b.ProfessionalId,
				When = NotificationTemplates.When(b.LocalStart),
				Date = LocalDatePattern.Iso.Format(b.LocalStart.Date),
				b.CanChange,
			}),
			Note = bookings.Count == 0 ? "The customer has no upcoming appointments." : null,
		};
	}
}

/// <summary>The input of <c>propose_cancel</c>.</summary>
/// <param name="BookingId">The booking, from find_my_bookings.</param>
public sealed record ProposeCancelInput(string? BookingId) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() => Guid.TryParse(BookingId, out _) ? null : "booking_id must be an id from find_my_bookings.";
}

/// <summary>
/// <c>propose_cancel</c>: shows the customer a card to cancel one of their appointments. It cancels nothing: the
/// customer presses the card's button, signed in.
/// </summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
/// <param name="useCase">The customer's bookings.</param>
public sealed class ProposeCancelTool(AgentTurn turn, IClock clock, ListMyBookings useCase) : AssistantTool<ProposeCancelInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "propose_cancel";

	/// <inheritdoc />
	public override string Description =>
		"Shows the signed-in customer a card to cancel one of their appointments, once they have said which. " +
		"It does not cancel it: the customer does, with the card's Cancelar cita button.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"booking_id":{"type":"string","description":"From find_my_bookings."}},"required":["booking_id"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override async Task<object> RunAsync(ProposeCancelInput input, CancellationToken cancellationToken)
	{
		if (Turn.Customer is not { } customer)
		{
			Turn.RequestSignIn();
			return Problem(SignIn.Required);
		}

		var id = Guid.Parse(input.BookingId!);
		var booking = (await useCase.HandleAsync(customer, cancellationToken).ConfigureAwait(false)).FirstOrDefault(b => b.Id == id);
		if (booking is null)
		{
			return Problem("That is not one of the customer's upcoming appointments. Call find_my_bookings.");
		}

		if (!booking.CanChange)
		{
			return Problem("It is too close to the appointment to cancel it here; the customer should call the business.");
		}

		Turn.ProposeCancel(new CancelCard(booking.Id, booking.ServiceName, booking.ProfessionalName, booking.LocalStart));
		return new
		{
			Summary = $"{booking.ServiceName} con {booking.ProfessionalName}, {NotificationTemplates.When(booking.LocalStart)}",
			NextStep = "Tell the customer to press Cancelar cita on the card below if they want to cancel it. You cannot cancel it yourself.",
		};
	}
}

/// <summary>The input of <c>propose_reschedule</c>.</summary>
/// <param name="BookingId">The booking, from find_my_bookings.</param>
/// <param name="Start">The new start, as check_availability returned it.</param>
public sealed record ProposeRescheduleInput(string? BookingId, string? Start) : IToolInput
{
	/// <inheritdoc />
	public string? Problem() =>
		!Guid.TryParse(BookingId, out _) ? "booking_id must be an id from find_my_bookings."
		: !InstantPattern.ExtendedIso.Parse(Start ?? string.Empty).Success ? "start must be a start exactly as check_availability returned it."
		: null;
}

/// <summary>
/// <c>propose_reschedule</c>: holds a new time for one of the customer's appointments, with the same professional and
/// service. Nothing moves until the customer confirms the card; until then the original stands.
/// </summary>
/// <param name="turn">The turn.</param>
/// <param name="clock">The clock.</param>
/// <param name="useCase">The move, as the "Mis citas" page asks for it.</param>
/// <param name="mine">The customer's bookings, for the names on the card.</param>
public sealed class ProposeRescheduleTool(AgentTurn turn, IClock clock, RescheduleBooking useCase, ListMyBookings mine) : AssistantTool<ProposeRescheduleInput>(turn, clock)
{
	/// <inheritdoc />
	public override string Name => "propose_reschedule";

	/// <inheritdoc />
	public override string Description =>
		"Holds a new time for one of the signed-in customer's appointments (same professional and service), once they " +
		"have chosen it from check_availability. The move happens only when the customer presses Confirmar on the card.";

	/// <inheritdoc />
	protected override string Schema => """
		{"type":"object","properties":{"booking_id":{"type":"string","description":"From find_my_bookings."},"start":{"type":"string","description":"Exactly as check_availability returned it."}},"required":["booking_id","start"],"additionalProperties":false}
		""";

	/// <inheritdoc />
	protected override async Task<object> RunAsync(ProposeRescheduleInput input, CancellationToken cancellationToken)
	{
		if (Turn.Customer is not { } customer)
		{
			Turn.RequestSignIn();
			return Problem(SignIn.Required);
		}

		var id = Guid.Parse(input.BookingId!);
		var original = (await mine.HandleAsync(customer, cancellationToken).ConfigureAwait(false)).FirstOrDefault(b => b.Id == id);
		if (original is null)
		{
			return Problem("That is not one of the customer's upcoming appointments. Call find_my_bookings.");
		}

		var result = await useCase.HandleAsync(id, InstantPattern.ExtendedIso.Parse(input.Start!).Value, null, customer.CustomerId, cancellationToken).ConfigureAwait(false);
		if (!result.IsSuccess)
		{
			var error = result.Error!;
			return error.Code switch
			{
				BookingErrors.TooLate => Problem("It is too close to the appointment to move it here; the customer should call the business."),
				BookingErrors.SlotUnavailable or BookingErrors.SlotTaken => new
				{
					Error = "That time is not free. Tell the customer and offer these nearest free times instead.",
					Alternatives = ToolAnswers.Alternatives(error.Details, Turn.Business.Zone),
				},
				_ => Problem("That appointment cannot be moved. Call find_my_bookings to see the customer's appointments."),
			};
		}

		var hold = result.Value;
		Turn.Held(new HoldCard(hold.Booking.Id, hold.ConfirmationToken, original.ServiceName, original.ProfessionalName, hold.Booking.LocalStart, hold.Booking.HoldExpiresAt!.Value, original.Id));
		return new
		{
			HoldId = hold.Booking.Id,
			Summary = $"{original.ServiceName} con {original.ProfessionalName}: de {NotificationTemplates.When(original.LocalStart)} a {NotificationTemplates.When(hold.Booking.LocalStart)}",
			NextStep = "Tell the customer the new time is held and that the change happens when they press Confirmar on the card below; until then the original appointment stands. You cannot confirm it.",
		};
	}
}
