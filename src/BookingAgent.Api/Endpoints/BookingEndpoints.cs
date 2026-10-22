namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Application.Features.Bookings;
using NodaTime;
using NodaTime.Text;

/// <summary>
/// Availability, holds and confirmations: what the form, the chat widget's confirmation card and the assistant's
/// tools are built on. Cancelling and moving a booking wait for the customer's sign-in code (day 7).
/// </summary>
internal static class BookingEndpoints
{
	/// <summary>A date in the query string is a plain ISO date: 2026-10-27.</summary>
	internal const string BadDate = "availability.bad_date";

	/// <summary>Maps the routes.</summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/v1/professionals/{professionalId:guid}/availability", AvailabilityAsync).WithTags("Availability");

		var holds = app.MapGroup("/v1/holds").WithTags("Bookings");
		holds.MapPost("/", HoldAsync);
		holds.MapPost("/{id:guid}/confirm", ConfirmAsync);
		return app;
	}

	// GET /v1/professionals/{id}/availability?serviceId={guid}&from=2026-10-27&to=2026-10-31 (to defaults to from).
	private static async Task<IResult> AvailabilityAsync(
		Guid professionalId,
		Guid serviceId,
		string? from,
		string? to,
		FindAvailableSlots useCase,
		CancellationToken cancellationToken)
	{
		var first = LocalDatePattern.Iso.Parse(from ?? string.Empty);
		var last = to is null ? first : LocalDatePattern.Iso.Parse(to);
		if (!first.Success || !last.Success)
		{
			return Error.Validation(BadDate, "Give from (and optionally to) as dates like 2026-10-27.").ToProblem();
		}

		var result = await useCase.HandleAsync(new AvailabilityQuery(professionalId, serviceId, first.Value, last.Value), cancellationToken);
		return result.ToHttp(slots => Results.Ok(slots.Select(static s => new SlotResponse(s.Period.Start, s.Period.End, s.LocalStart))));
	}

	private static async Task<IResult> HoldAsync(HoldRequest request, HoldSlot useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(request, cancellationToken)).ToHttp(hold => Results.Created($"/v1/holds/{hold.Booking.Id}", hold));

	private static async Task<IResult> ConfirmAsync(Guid id, ConfirmRequest? request, ConfirmHold useCase, CancellationToken cancellationToken) =>
		(await useCase.HandleAsync(id, request?.Token, cancellationToken)).ToHttp(Results.Ok);

	/// <summary>A free slot.</summary>
	/// <param name="Start">When it starts.</param>
	/// <param name="End">When it ends.</param>
	/// <param name="LocalStart">Its start on the business's clock.</param>
	internal sealed record SlotResponse(Instant Start, Instant End, LocalDateTime LocalStart);

	/// <summary>A confirmation.</summary>
	/// <param name="Token">The token the hold came with.</param>
	internal sealed record ConfirmRequest(string? Token);
}
