namespace BookingAgent.Application.Features.Assistant.Tools;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using NodaTime;

/// <summary>What a tool's input must satisfy beyond its JSON Schema.</summary>
public interface IToolInput
{
	/// <summary>Why the input cannot be used, phrased for the model, or <see langword="null"/> when it can.</summary>
	/// <returns>The problem.</returns>
	string? Problem();
}

/// <summary>
/// A tool the assistant can call: a thin adapter over a use case, with a hand-written JSON Schema (every property
/// required, nothing extra) that the provider enforces strictly. The input is checked again here, since a schema
/// is a promise about the model, not about the request. Expected failures are answers the model can act on
/// (<c>{"error": "..."}</c>), never exceptions; every call is recorded on the turn.
/// </summary>
/// <typeparam name="TInput">The input.</typeparam>
/// <param name="turn">The turn being recorded.</param>
/// <param name="clock">The clock, for the trace.</param>
public abstract class AssistantTool<TInput>(AgentTurn turn, IClock clock) : AIFunction
	where TInput : class, IToolInput
{
	/// <summary>snake_case both ways, and an unknown property is an error rather than silently dropped.</summary>
	protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>The turn.</summary>
	protected AgentTurn Turn => turn;

	/// <summary>The clock.</summary>
	protected IClock Clock => clock;

	/// <summary>The schema of the input, as JSON text.</summary>
	protected abstract string Schema { get; }

	/// <inheritdoc />
	public override JsonElement JsonSchema => JsonDocument.Parse(Schema).RootElement;

	/// <summary>Runs the tool.</summary>
	/// <param name="input">The checked input.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The answer for the model.</returns>
	protected abstract Task<object> RunAsync(TInput input, CancellationToken cancellationToken);

	/// <summary>An answer that tells the model what went wrong and what to do instead.</summary>
	/// <param name="message">The message, for the model.</param>
	/// <returns>The answer.</returns>
	protected static object Problem(string message) => new ToolError(message);

	/// <inheritdoc />
	protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		var started = clock.GetCurrentInstant();
		var stopwatch = Stopwatch.StartNew();
		var input = JsonSerializer.Serialize(arguments.ToDictionary(static a => a.Key, static a => a.Value), Json);

		object answer;
		TInput? parsed = null;
		try
		{
			parsed = JsonSerializer.Deserialize<TInput>(input, Json);
		}
		catch (JsonException exception)
		{
			answer = Problem($"The input does not match the tool's schema: {exception.Message}");
			return Record(started, stopwatch, input, answer);
		}

		if (parsed is null)
		{
			answer = Problem("The input is empty.");
		}
		else if (parsed.Problem() is { } problem)
		{
			answer = Problem(problem);
		}
		else
		{
			answer = await RunAsync(parsed, cancellationToken).ConfigureAwait(false);
		}

		return Record(started, stopwatch, input, answer);
	}

	private JsonElement Record(Instant started, Stopwatch stopwatch, string input, object answer)
	{
		var output = JsonSerializer.SerializeToElement(answer, answer.GetType(), Json);
		turn.RecordToolCall(started, Duration.FromTimeSpan(stopwatch.Elapsed), Name, input, output.GetRawText());
		return output;
	}

	/// <summary>An expected failure, for the model.</summary>
	/// <param name="Error">What went wrong and what to do.</param>
	protected sealed record ToolError(string Error);
}
