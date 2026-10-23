namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Features.Assistant;
using BookingAgent.Application.Features.Customers;

/// <summary>The chat: one message in, the assistant's reply and any held slots out.</summary>
internal static class AssistantEndpoints
{
	/// <summary>Maps the route.</summary>
	/// <param name="app">The app.</param>
	/// <returns>The app.</returns>
	public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/v1/businesses/{businessId:guid}/assistant/messages", MessageAsync)
			.WithTags("Assistant")
			.RequireRateLimiting(RateLimits.Assistant);
		return app;
	}

	// POST {"conversationId": null, "message": "Quiero una limpieza el martes"} → {conversationId, reply, holds, ...}.
	// A signed-in customer (the session cookie) lets the assistant see and propose changes to their appointments.
	private static async Task<IResult> MessageAsync(
		Guid businessId,
		AssistantRequest request,
		AgentRunner runner,
		CustomerSessions sessions,
		HttpContext http,
		CancellationToken cancellationToken)
	{
		var customer = await CustomerEndpoints.CurrentAsync(http, businessId, sessions);
		return (await runner.HandleAsync(businessId, request, customer, cancellationToken)).ToHttp(Results.Ok);
	}
}
