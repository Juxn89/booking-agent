namespace BookingAgent.Application.Features.Assistant;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Assistant.Tools;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Domain;
using BookingAgent.Domain.Assistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NodaTime;

/// <summary>Where conversations and their steps are kept.</summary>
public interface IConversationStore
{
	/// <summary>A conversation, not tracked: the tools save bookings in between, and must not save it by accident.</summary>
	/// <param name="id">Its id.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The conversation, or <see langword="null"/>.</returns>
	Task<AgentConversation?> FindAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>What all conversations have spent since an instant.</summary>
	/// <param name="since">The instant.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>USD.</returns>
	Task<decimal> SpentSinceAsync(Instant since, CancellationToken cancellationToken);

	/// <summary>Saves a turn: the conversation (new or changed) and its steps, in one transaction.</summary>
	/// <param name="conversation">The conversation.</param>
	/// <param name="isNew">Whether it was started in this turn.</param>
	/// <param name="steps">The turn's steps.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	/// <exception cref="ConversationBusyException">Another message of the conversation was saved first.</exception>
	Task SaveTurnAsync(AgentConversation conversation, bool isNew, IReadOnlyList<AgentStep> steps, CancellationToken cancellationToken);
}

/// <summary>A message to the assistant.</summary>
/// <param name="ConversationId">The conversation, or <see langword="null"/> to start one.</param>
/// <param name="Message">What the customer wrote.</param>
public sealed record AssistantRequest(Guid? ConversationId, string? Message);

/// <summary>The assistant's reply.</summary>
/// <param name="ConversationId">The conversation, to send with the next message.</param>
/// <param name="Reply">What the assistant says.</param>
/// <param name="Holds">Slots it held in this reply, for the widget to show as cards with a Confirmar button.</param>
public sealed record AssistantReply(Guid ConversationId, string Reply, IReadOnlyList<HoldCard> Holds);

/// <summary>The assistant error codes (docs/errors.md).</summary>
public static class AssistantErrors
{
	/// <summary>The message is empty or too long.</summary>
	public const string InvalidMessage = "assistant.invalid_message";

	/// <summary>No such conversation with this business.</summary>
	public const string ConversationNotFound = "assistant.conversation_not_found";

	/// <summary>The conversation reached its message or spending limit.</summary>
	public const string ConversationLimit = "assistant.conversation_limit";

	/// <summary>Another message of the conversation is being answered.</summary>
	public const string Busy = "assistant.busy";

	/// <summary>No model, the provider failed, or the day's budget is spent.</summary>
	public const string Unavailable = "assistant.unavailable";
}

/// <summary>
/// One reply of the assistant. The history lives on the server; the model works through the tools in a loop that is
/// capped in calls (<see cref="AssistantOptions.MaxModelCallsPerTurn"/>) and in money, per conversation and per day.
/// Every model call and tool call is recorded with its cost. Holds go back to the caller with their tokens, for the
/// card; the model only ever sees that a slot was held.
/// </summary>
/// <param name="chat">The model with the tool loop around it.</param>
/// <param name="turn">This request's turn.</param>
/// <param name="tools">The tools.</param>
/// <param name="conversations">The conversations.</param>
/// <param name="catalog">The business's catalog.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">Limits and prices.</param>
public sealed class AgentRunner(
	IChatClient chat,
	AgentTurn turn,
	BookingTools tools,
	IConversationStore conversations,
	ICatalogReader catalog,
	IClock clock,
	IOptions<AssistantOptions> options)
{
	/// <summary>Said when the conversation's money runs out mid-reply, instead of a reply.</summary>
	public const string OutOfBudgetReply =
		"Lo siento, no puedo seguir con esta conversación. Puedes reservar desde el formulario de la página o escribirnos más tarde.";

	/// <summary>Said when the loop ends without words, e.g. at its cap of model calls.</summary>
	public const string LostReply = "Disculpa, me enredé un poco. ¿Me cuentas de nuevo qué necesitas?";

	private static readonly string Template = ReadPrompt("booking-agent.md");

	/// <summary>Answers a message.</summary>
	/// <param name="businessId">The business the chat is on.</param>
	/// <param name="request">The message.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The reply, or why there is none.</returns>
	public async Task<Result<AssistantReply>> HandleAsync(Guid businessId, AssistantRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		var settings = options.Value;
		var message = request.Message?.Trim() ?? string.Empty;
		if (message.Length == 0 || message.Length > settings.MaxMessageLength)
		{
			return Error.Validation(AssistantErrors.InvalidMessage, $"Write a message of 1 to {settings.MaxMessageLength} characters.");
		}

		var data = await catalog.LoadAsync(new BusinessId(businessId), cancellationToken).ConfigureAwait(false);
		if (data is null)
		{
			return Error.NotFound(CatalogErrors.NotFound, $"There is no business {businessId}.");
		}

		var now = clock.GetCurrentInstant();
		var conversation = request.ConversationId is { } id ? await conversations.FindAsync(id, cancellationToken).ConfigureAwait(false) : null;
		if (request.ConversationId is not null && (conversation is null || conversation.BusinessId != data.Business.Id))
		{
			return Error.NotFound(AssistantErrors.ConversationNotFound, "There is no such conversation; start a new one.");
		}

		var isNew = conversation is null;
		conversation ??= AgentConversation.Start(data.Business.Id, now);
		if (conversation.Turns >= settings.MaxTurnsPerConversation || conversation.CostUsd >= settings.MaxCostPerConversationUsd)
		{
			return Error.Conflict(AssistantErrors.ConversationLimit, "This conversation reached its limit; start a new one or use the booking form.");
		}

		var startOfDay = now.InUtc().Date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
		if (await conversations.SpentSinceAsync(startOfDay, cancellationToken).ConfigureAwait(false) >= settings.DailyBudgetUsd)
		{
			return Error.Unavailable(AssistantErrors.Unavailable, "The assistant is resting until tomorrow; the booking form works as usual.");
		}

		var zone = data.Business.Zone;
		turn.Begin(conversation.Id, conversation.Turns + 1, new AssistantBusiness(data, zone, now.InZone(zone).Date), settings.MaxCostPerConversationUsd - conversation.CostUsd);

		var history = JsonSerializer.Deserialize<List<ChatMessage>>(conversation.History, AIJsonUtilities.DefaultOptions) ?? [];
		var user = new ChatMessage(ChatRole.User, message);
		List<ChatMessage> produced;
		string reply;
		try
		{
			var response = await chat.GetResponseAsync(
				[new ChatMessage(ChatRole.System, SystemPrompt(turn.Business)), .. history, user],
				new ChatOptions { Tools = tools.All },
				cancellationToken).ConfigureAwait(false);
			produced = [.. response.Messages];
			reply = response.Text.Trim();
		}
		catch (AssistantBudgetExceededException)
		{
			reply = OutOfBudgetReply;
			produced = [new ChatMessage(ChatRole.Assistant, reply)];
		}
		catch (AssistantUnavailableException)
		{
			return Error.Unavailable(AssistantErrors.Unavailable, "The assistant cannot answer right now; try again in a moment, or use the booking form.");
		}

		if (reply.Length == 0)
		{
			reply = LostReply;
			produced.Add(new ChatMessage(ChatRole.Assistant, reply));
		}

		// Thinking is only needed within a reply's tool loop; earlier replies' thinking is not sent back. A tool call
		// left unanswered (the loop hit its cap) would make the next request invalid, so it goes too.
		var answered = produced.SelectMany(static m => m.Contents).OfType<FunctionResultContent>().Select(static r => r.CallId).ToHashSet(StringComparer.Ordinal);
		var kept = produced
			.Select(m => new ChatMessage(m.Role, [.. m.Contents.Where(c => c is not TextReasoningContent && (c is not FunctionCallContent call || answered.Contains(call.CallId)))]))
			.Where(static m => m.Contents.Count > 0);
		conversation.RecordTurn(
			JsonSerializer.Serialize<List<ChatMessage>>([.. history, user, .. kept], AIJsonUtilities.DefaultOptions),
			turn.InputTokens,
			turn.OutputTokens,
			turn.CostUsd,
			clock.GetCurrentInstant());
		try
		{
			await conversations.SaveTurnAsync(conversation, isNew, turn.Steps, cancellationToken).ConfigureAwait(false);
		}
		catch (ConversationBusyException)
		{
			return Error.Conflict(AssistantErrors.Busy, "Another message of this conversation was answered at the same time; send yours again.");
		}

		return new AssistantReply(conversation.Id, reply, turn.Holds);
	}

	private static string SystemPrompt(AssistantBusiness business)
	{
		var catalog = business.Catalog;
		var names = catalog.Services.ToDictionary(static s => s.Id, static s => s.Name);
		return Template
			.Replace("{business}", catalog.Business.Name, StringComparison.Ordinal)
			.Replace("{today}", Notifications.NotificationTemplates.When(business.Today.AtMidnight()).Split(" a las ")[0] + $" ({business.Today:yyyy-MM-dd})", StringComparison.Ordinal)
			.Replace("{zone}", business.Zone.Id, StringComparison.Ordinal)
			.Replace(
				"{services}",
				string.Join('\n', catalog.Services.Select(static s => string.Create(CultureInfo.InvariantCulture, $"- {s.Id.Value} · {s.Name} · {s.Duration.TotalMinutes:0} min · USD {s.Price:0.00}"))),
				StringComparison.Ordinal)
			.Replace(
				"{professionals}",
				string.Join('\n', catalog.Professionals.Select(p => $"- {p.Id.Value} · {p.Name} · {p.Specialty} · {string.Join(", ", p.Services.Select(s => names.GetValueOrDefault(s)))}")),
				StringComparison.Ordinal);
	}

	private static string ReadPrompt(string name)
	{
		var assembly = typeof(AgentRunner).Assembly;
		var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".Prompts.{name}", StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(resource)!;
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
