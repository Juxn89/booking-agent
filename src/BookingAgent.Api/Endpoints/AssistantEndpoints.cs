namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Features.Assistant;

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
			.RequireRateLimiting(AssistantRateLimit.Policy);
		return app;
	}

	// POST {"conversationId": null, "message": "Quiero una limpieza el martes"} → {conversationId, reply, holds}.
	private static async Task<IResult> MessageAsync(Guid businessId, AssistantRequest request, AgentRunner runner, CancellationToken cancellationToken) =>
		(await runner.HandleAsync(businessId, request, cancellationToken)).ToHttp(Results.Ok);
}
