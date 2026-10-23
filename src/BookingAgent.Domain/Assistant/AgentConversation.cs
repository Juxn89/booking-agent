namespace BookingAgent.Domain.Assistant;

using NodaTime;

/// <summary>
/// A chat with the assistant: its history and what it has cost. The history is kept on the server, so a client can
/// never put words in the assistant's mouth or forge a tool's answer; it is opaque here (the application decides its
/// format).
/// </summary>
public sealed class AgentConversation
{
	private AgentConversation(Guid id, BusinessId businessId, Instant startedAt)
	{
		Id = id;
		BusinessId = businessId;
		StartedAt = startedAt;
		LastActivityAt = startedAt;
		History = "[]";
	}

	/// <summary>The id the widget sends back with each message.</summary>
	public Guid Id { get; private set; }

	/// <summary>The business it is with.</summary>
	public BusinessId BusinessId { get; private set; }

	/// <summary>When it started.</summary>
	public Instant StartedAt { get; private set; }

	/// <summary>The last message.</summary>
	public Instant LastActivityAt { get; private set; }

	/// <summary>How many messages the customer has sent.</summary>
	public int Turns { get; private set; }

	/// <summary>The messages so far, serialised by the application.</summary>
	public string History { get; private set; }

	/// <summary>Input tokens, every model call included.</summary>
	public long InputTokens { get; private set; }

	/// <summary>Output tokens, thinking included.</summary>
	public long OutputTokens { get; private set; }

	/// <summary>What the model calls cost, in USD.</summary>
	public decimal CostUsd { get; private set; }

	/// <summary>Starts a conversation.</summary>
	/// <param name="businessId">The business.</param>
	/// <param name="now">The current instant.</param>
	/// <returns>The conversation.</returns>
	public static AgentConversation Start(BusinessId businessId, Instant now) => new(Guid.CreateVersion7(), businessId, now);

	/// <summary>Records a turn: the customer's message, the assistant's work and what it cost.</summary>
	/// <param name="history">The history after the turn.</param>
	/// <param name="inputTokens">Input tokens of the turn.</param>
	/// <param name="outputTokens">Output tokens of the turn.</param>
	/// <param name="costUsd">What the turn cost.</param>
	/// <param name="now">The current instant.</param>
	public void RecordTurn(string history, long inputTokens, long outputTokens, decimal costUsd, Instant now)
	{
		ArgumentException.ThrowIfNullOrEmpty(history);
		History = history;
		Turns++;
		InputTokens += inputTokens;
		OutputTokens += outputTokens;
		CostUsd += costUsd;
		LastActivityAt = now;
	}
}

/// <summary>What one step of the assistant's loop was.</summary>
public enum AgentStepKind
{
	/// <summary>A call to the model.</summary>
	Model = 1,

	/// <summary>A tool the model asked for.</summary>
	Tool = 2,
}

/// <summary>
/// One step behind a reply: a model call with its tokens and cost, or a tool call with its input and output. The
/// panel shows these as the "why" of a booking, and the evals read them to check that every time offered came from
/// the availability.
/// </summary>
public sealed class AgentStep
{
	private AgentStep(Guid conversationId, int turn, int ordinal, AgentStepKind kind, Instant at)
	{
		Id = Guid.CreateVersion7();
		ConversationId = conversationId;
		Turn = turn;
		Ordinal = ordinal;
		Kind = kind;
		At = at;
	}

	/// <summary>The id.</summary>
	public Guid Id { get; private set; }

	/// <summary>The conversation.</summary>
	public Guid ConversationId { get; private set; }

	/// <summary>The customer's message it answered, from 1.</summary>
	public int Turn { get; private set; }

	/// <summary>Its place in the turn, from 1.</summary>
	public int Ordinal { get; private set; }

	/// <summary>Model or tool.</summary>
	public AgentStepKind Kind { get; private set; }

	/// <summary>When it started.</summary>
	public Instant At { get; private set; }

	/// <summary>The tool, for a tool step.</summary>
	public string? ToolName { get; private set; }

	/// <summary>The tool's input, JSON, with emails masked.</summary>
	public string? Input { get; private set; }

	/// <summary>The tool's output, JSON, with emails masked.</summary>
	public string? Output { get; private set; }

	/// <summary>How long it took.</summary>
	public int LatencyMs { get; private set; }

	/// <summary>Input tokens, for a model step.</summary>
	public long InputTokens { get; private set; }

	/// <summary>Output tokens, for a model step.</summary>
	public long OutputTokens { get; private set; }

	/// <summary>What it cost, for a model step.</summary>
	public decimal CostUsd { get; private set; }

	/// <summary>Records a model call.</summary>
	/// <param name="conversationId">The conversation.</param>
	/// <param name="turn">The turn.</param>
	/// <param name="ordinal">Its place in the turn.</param>
	/// <param name="at">When it started.</param>
	/// <param name="latency">How long it took.</param>
	/// <param name="inputTokens">Input tokens.</param>
	/// <param name="outputTokens">Output tokens.</param>
	/// <param name="costUsd">What it cost.</param>
	/// <returns>The step.</returns>
	public static AgentStep ModelCall(Guid conversationId, int turn, int ordinal, Instant at, Duration latency, long inputTokens, long outputTokens, decimal costUsd) =>
		new(conversationId, turn, ordinal, AgentStepKind.Model, at)
		{
			LatencyMs = Milliseconds(latency),
			InputTokens = inputTokens,
			OutputTokens = outputTokens,
			CostUsd = costUsd,
		};

	/// <summary>Records a tool call.</summary>
	/// <param name="conversationId">The conversation.</param>
	/// <param name="turn">The turn.</param>
	/// <param name="ordinal">Its place in the turn.</param>
	/// <param name="at">When it started.</param>
	/// <param name="latency">How long it took.</param>
	/// <param name="toolName">The tool.</param>
	/// <param name="input">Its input, JSON, already masked.</param>
	/// <param name="output">Its output, JSON, already masked.</param>
	/// <returns>The step.</returns>
	public static AgentStep ToolCall(Guid conversationId, int turn, int ordinal, Instant at, Duration latency, string toolName, string input, string output) =>
		new(conversationId, turn, ordinal, AgentStepKind.Tool, at)
		{
			LatencyMs = Milliseconds(latency),
			ToolName = toolName,
			Input = input,
			Output = output,
		};

	private static int Milliseconds(Duration latency) => (int)Math.Clamp(latency.TotalMilliseconds, 0, int.MaxValue);
}
