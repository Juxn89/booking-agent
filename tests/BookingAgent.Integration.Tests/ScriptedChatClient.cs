namespace BookingAgent.Integration.Tests;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
/// A model that follows a script: each call answers with the next step, which may look at the messages so far (to
/// pass on what a tool returned). The assistant's loop, tools, limits and storage run for real around it, with no
/// key and no cost.
/// </summary>
/// <param name="script">One step per model call.</param>
internal sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script) : IChatClient
{
	/// <summary>Tokens a scripted call reports, so the meter has something to price.</summary>
	public const long InputTokens = 1000;

	public const long OutputTokens = 50;

	private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _script = new(script);

	/// <summary>The messages each call received.</summary>
	public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

	public int Remaining => _script.Count;

	public static ChatResponse Call(string tool, Dictionary<string, object?> arguments) =>
		Respond(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", tool, arguments)]));

	public static ChatResponse Say(string text) => Respond(new ChatMessage(ChatRole.Assistant, text));

	/// <summary>What the last call of a tool returned, as the model saw it.</summary>
	public static JsonElement LastResult(IReadOnlyList<ChatMessage> messages, string tool)
	{
		var contents = messages.SelectMany(static m => m.Contents).ToList();
		var call = contents.OfType<FunctionCallContent>().Last(c => c.Name == tool);
		var result = contents.OfType<FunctionResultContent>().Single(r => r.CallId == call.CallId);
		return result.Result is JsonElement element ? element : JsonSerializer.SerializeToElement(result.Result);
	}

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		var snapshot = messages.ToList();
		Calls.Add(snapshot);
		if (!_script.TryDequeue(out var next))
		{
			throw new InvalidOperationException($"The script ran out at call {Calls.Count}.");
		}

		return Task.FromResult(next(snapshot));
	}

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}

	private static ChatResponse Respond(ChatMessage message) =>
		new(message) { Usage = new UsageDetails { InputTokenCount = InputTokens, OutputTokenCount = OutputTokens } };
}
