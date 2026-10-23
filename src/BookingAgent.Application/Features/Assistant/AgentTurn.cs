namespace BookingAgent.Application.Features.Assistant;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Domain.Assistant;
using NodaTime;

/// <summary>The assistant's model, limits and prices, bound from the <c>Assistant</c> section.</summary>
public sealed class AssistantOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Assistant";

	/// <summary>Claude Haiku 5.5 for every call (AGENTS.md).</summary>
	[Required]
	public string Model { get; set; } = "claude-haiku-5-5";

	/// <summary>
	/// Model calls in one reply, tools included: finding a day, the slots and holding one takes four; past eight the
	/// assistant is going round in circles, and the loop answers without tools.
	/// </summary>
	[Range(1, 20)]
	public int MaxModelCallsPerTurn { get; set; } = 8;

	/// <summary>Messages a customer may send in one conversation: a booking takes about six.</summary>
	[Range(1, 200)]
	public int MaxTurnsPerConversation { get; set; } = 30;

	/// <summary>The longest message accepted, in characters: a few sentences, not a pasted document.</summary>
	[Range(10, 10_000)]
	public int MaxMessageLength { get; set; } = 1000;

	/// <summary>
	/// What one conversation may spend. A booking costs about USD 0.003 with Haiku (plan §5); five cents is room for
	/// a long, indecisive one, and a stop for a loop or abuse.
	/// </summary>
	[Range(0.0001, 10)]
	public decimal MaxCostPerConversationUsd { get; set; } = 0.05m;

	/// <summary>What all conversations together may spend per UTC day; past it the assistant pauses until tomorrow.</summary>
	[Range(0.01, 1000)]
	public decimal DailyBudgetUsd { get; set; } = 1.00m;

	/// <summary>USD per million input tokens: Claude Haiku 5.5 (models reference, 23 Oct 2026).</summary>
	[Range(0, 100)]
	public decimal InputUsdPerMillionTokens { get; set; } = 0.10m;

	/// <summary>USD per million output tokens, thinking included, same source and date.</summary>
	[Range(0, 100)]
	public decimal OutputUsdPerMillionTokens { get; set; } = 0.50m;

	/// <summary>What a call with these token counts costs.</summary>
	/// <param name="inputTokens">Input tokens.</param>
	/// <param name="outputTokens">Output tokens.</param>
	/// <returns>USD.</returns>
	public decimal Cost(long inputTokens, long outputTokens) =>
		((inputTokens * InputUsdPerMillionTokens) + (outputTokens * OutputUsdPerMillionTokens)) / 1_000_000m;
}

/// <summary>The business the assistant speaks for, as of this turn.</summary>
/// <param name="Catalog">Its services and professionals.</param>
/// <param name="Zone">Its clock.</param>
/// <param name="Today">Today on its clock.</param>
public sealed record AssistantBusiness(CatalogData Catalog, DateTimeZone Zone, LocalDate Today);

/// <summary>A slot the assistant held, for the widget to show as a card with a Confirmar button.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="ConfirmationToken">What confirms it: for the widget only, never shown to the model.</param>
/// <param name="ServiceName">The service.</param>
/// <param name="ProfessionalName">The professional.</param>
/// <param name="LocalStart">When, on the business's clock.</param>
/// <param name="ExpiresAt">Until when it is held.</param>
public sealed record HoldCard(Guid HoldId, string ConfirmationToken, string ServiceName, string ProfessionalName, LocalDateTime LocalStart, Instant ExpiresAt);

/// <summary>
/// What happens during one reply of the assistant: the model calls and tool calls (with their cost) and the holds
/// made. One per request scope; the tools and the metering client write to it,
/// the runner reads it.
/// </summary>
public sealed class AgentTurn
{
	private readonly List<AgentStep> _steps = [];
	private readonly List<HoldCard> _holds = [];
	private AssistantBusiness? _business;

	/// <summary>The conversation.</summary>
	public Guid ConversationId { get; private set; }

	/// <summary>The turn's number in the conversation, from 1.</summary>
	public int Number { get; private set; }

	/// <summary>The business.</summary>
	/// <exception cref="InvalidOperationException">The turn has not begun.</exception>
	public AssistantBusiness Business => _business ?? throw new InvalidOperationException("The turn has not begun.");

	/// <summary>What the turn may still spend before the conversation's cap.</summary>
	public decimal Budget { get; private set; }

	/// <summary>The steps, in order.</summary>
	public IReadOnlyList<AgentStep> Steps => _steps;

	/// <summary>The holds made.</summary>
	public IReadOnlyList<HoldCard> Holds => _holds;

	/// <summary>Input tokens so far.</summary>
	public long InputTokens { get; private set; }

	/// <summary>Output tokens so far.</summary>
	public long OutputTokens { get; private set; }

	/// <summary>What the turn has cost so far.</summary>
	public decimal CostUsd { get; private set; }

	/// <summary>Whether the turn has spent its budget: no more model calls.</summary>
	public bool OverBudget => CostUsd >= Budget;

	/// <summary>Starts the turn.</summary>
	/// <param name="conversationId">The conversation.</param>
	/// <param name="number">The turn's number.</param>
	/// <param name="business">The business.</param>
	/// <param name="budget">What it may spend.</param>
	public void Begin(Guid conversationId, int number, AssistantBusiness business, decimal budget)
	{
		ConversationId = conversationId;
		Number = number;
		_business = business;
		Budget = budget;
	}

	/// <summary>Records a model call.</summary>
	/// <param name="at">When it started.</param>
	/// <param name="latency">How long it took.</param>
	/// <param name="inputTokens">Input tokens.</param>
	/// <param name="outputTokens">Output tokens.</param>
	/// <param name="costUsd">What it cost.</param>
	public void RecordModelCall(Instant at, Duration latency, long inputTokens, long outputTokens, decimal costUsd)
	{
		InputTokens += inputTokens;
		OutputTokens += outputTokens;
		CostUsd += costUsd;
		_steps.Add(AgentStep.ModelCall(ConversationId, Number, _steps.Count + 1, at, latency, inputTokens, outputTokens, costUsd));
	}

	/// <summary>Records a tool call.</summary>
	/// <param name="at">When it started.</param>
	/// <param name="latency">How long it took.</param>
	/// <param name="tool">The tool.</param>
	/// <param name="input">Its input, JSON.</param>
	/// <param name="output">Its output, JSON.</param>
	public void RecordToolCall(Instant at, Duration latency, string tool, string input, string output) =>
		_steps.Add(AgentStep.ToolCall(ConversationId, Number, _steps.Count + 1, at, latency, tool, Pii.MaskEmails(input), Pii.MaskEmails(output)));

	/// <summary>Records a hold for the widget.</summary>
	/// <param name="card">The hold.</param>
	public void Held(HoldCard card) => _holds.Add(card);
}

/// <summary>The assistant cannot reach its model: none configured, or the provider failed.</summary>
public sealed class AssistantUnavailableException : Exception
{
	/// <summary>Creates the exception.</summary>
	public AssistantUnavailableException()
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	public AssistantUnavailableException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	/// <param name="innerException">The provider's error.</param>
	public AssistantUnavailableException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

/// <summary>The conversation's budget ran out in the middle of a reply; no more model calls are made.</summary>
public sealed class AssistantBudgetExceededException : Exception
{
	/// <summary>Creates the exception.</summary>
	public AssistantBudgetExceededException()
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	public AssistantBudgetExceededException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	/// <param name="innerException">The cause.</param>
	public AssistantBudgetExceededException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

/// <summary>Another message of the same conversation was saved first: two tabs, or a double send.</summary>
public sealed class ConversationBusyException : Exception
{
	/// <summary>Creates the exception.</summary>
	public ConversationBusyException()
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	public ConversationBusyException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">The message.</param>
	/// <param name="innerException">The database error.</param>
	public ConversationBusyException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
