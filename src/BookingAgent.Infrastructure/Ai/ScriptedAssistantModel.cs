namespace BookingAgent.Infrastructure.Ai;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

/// <summary>
/// A script in place of the model, bound from the <c>AssistantScript</c> section. Only for the end-to-end tests: the
/// API refuses to start with it outside Development.
/// </summary>
public sealed class AssistantScriptOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "AssistantScript";

	/// <summary>The script's JSON file; unset in every real deployment.</summary>
	public string? File { get; set; }
}

/// <summary>
/// The assistant's model played from a script, so the end-to-end test runs the real loop, tools and storage with no
/// key and no cost. A turn is chosen by a word in the customer's message; its steps are tool calls and a reply, in
/// order. An argument written <c>{tool.path}</c> takes a value from that tool's latest result, so the script works
/// whatever day it runs: <c>{check_availability.slots[time=10:00].start}</c>, <c>{find_my_bookings.bookings[0].booking_id}</c>.
/// </summary>
internal sealed partial class ScriptedAssistantModel : IChatClient
{
	private readonly IReadOnlyList<ScriptTurn> _turns;

	public ScriptedAssistantModel(string file)
	{
		var script = JsonSerializer.Deserialize<Script>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))
			?? throw new InvalidDataException($"{file} is empty.");
		_turns = script.Turns;
	}

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		var history = messages.ToList();
		var lastUser = history.FindLastIndex(static m => m.Role == ChatRole.User);
		var said = lastUser < 0 ? string.Empty : history[lastUser].Text;
		var turn = _turns.FirstOrDefault(t => said.Contains(t.When, StringComparison.OrdinalIgnoreCase));
		var done = history.Skip(lastUser + 1).SelectMany(static m => m.Contents).OfType<FunctionCallContent>().Count();
		var step = turn is not null && done < turn.Steps.Count ? turn.Steps[done] : null;

		var message = step?.Call is { } tool
			? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"script_{Guid.NewGuid():N}", tool, Arguments(step.Arguments, history))])
			: new ChatMessage(ChatRole.Assistant, step?.Say ?? "(guion) No tengo una respuesta para eso.");
		return Task.FromResult(new ChatResponse(message) { Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 } });
	}

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("The assistant answers whole replies.");

	public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}

	private static Dictionary<string, object?> Arguments(Dictionary<string, JsonElement>? arguments, List<ChatMessage> history) =>
		(arguments ?? []).ToDictionary(
			static a => a.Key,
			a => a.Value.ValueKind == JsonValueKind.String && Reference().Match(a.Value.GetString()!) is { Success: true } reference
				? (object?)Resolve(reference.Groups["tool"].Value, reference.Groups["path"].Value, history)
				: a.Value.ValueKind == JsonValueKind.Null ? null : a.Value.ToString());

	// The value at a path in the latest result of a tool: properties, [index] and [field=value].
	private static string? Resolve(string tool, string path, List<ChatMessage> history)
	{
		var contents = history.SelectMany(static m => m.Contents).ToList();
		var call = contents.OfType<FunctionCallContent>().LastOrDefault(c => c.Name == tool)
			?? throw new InvalidOperationException($"The script refers to {tool}, which was not called.");
		var result = contents.OfType<FunctionResultContent>().Single(r => r.CallId == call.CallId).Result;
		var node = JsonNode.Parse(result is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(result));
		foreach (Match segment in Segment().Matches(path))
		{
			node = node?[segment.Groups["name"].Value];
			if (segment.Groups["index"].Success)
			{
				node = node?.AsArray()[int.Parse(segment.Groups["index"].Value, CultureInfo.InvariantCulture)];
			}
			else if (segment.Groups["field"].Success)
			{
				node = node?.AsArray().FirstOrDefault(item => item?[segment.Groups["field"].Value]?.ToString() == segment.Groups["value"].Value);
			}
		}

		return node?.ToString() ?? throw new InvalidOperationException($"The script's {{{tool}.{path}}} has no value.");
	}

	[GeneratedRegex(@"^\{(?<tool>[a-z_]+)\.(?<path>[^}]+)\}$")]
	private static partial Regex Reference();

	[GeneratedRegex(@"(?<name>[a-z_]+)(?:\[(?:(?<index>\d+)|(?<field>[a-z_]+)=(?<value>[^\]]+))\])?")]
	private static partial Regex Segment();

	private sealed record Script(IReadOnlyList<ScriptTurn> Turns);

	private sealed record ScriptTurn(string When, IReadOnlyList<ScriptStep> Steps);

	private sealed record ScriptStep(string? Call, Dictionary<string, JsonElement>? Arguments, string? Say);
}
