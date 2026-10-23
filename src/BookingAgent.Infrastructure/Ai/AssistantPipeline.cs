namespace BookingAgent.Infrastructure.Ai;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using BookingAgent.Application.Features.Assistant;
using Microsoft.Extensions.AI;
using NodaTime;

/// <summary>The Anthropic API, bound from the <c>Anthropic</c> section. The key comes from the environment, never the repo.</summary>
public sealed class AnthropicOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Anthropic";

	/// <summary>The API key. Without one the assistant answers 503 and the rest of the app works.</summary>
	public string? ApiKey { get; set; }

	/// <summary>
	/// Haiku 5.5 thinks by default; choosing a service and a time needs little of it, so <c>low</c>. The evals measure
	/// whether that is enough.
	/// </summary>
	[Required]
	public string Effort { get; set; } = "low";

	/// <summary>A reply is a few sentences or a tool call, plus thinking, which counts toward this cap.</summary>
	[Range(256, 32_000)]
	public int MaxOutputTokens { get; set; } = 2048;

	/// <summary>One model call should take seconds; past this the customer is better told to try again.</summary>
	[Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>The SDK's retries of connection errors, 429s and 5xx. The paid evals set 0: a retry can be billed twice.</summary>
	[Range(0, 10)]
	public int MaxRetries { get; set; } = 2;
}

/// <summary>How the assistant's model is put together: the provider, then the tool loop and the meter per request.</summary>
public static class AssistantPipeline
{
	/// <summary>The DI key of the bare model; tests replace it with a scripted one.</summary>
	public const string ModelKey = "assistant-model";

	/// <summary>Activity source of the model calls, so they appear in the request's trace.</summary>
	public const string TelemetrySource = "BookingAgent.Assistant";

	/// <summary>The model: Claude through the official SDK, or a stand-in that answers "unavailable" without a key.</summary>
	/// <param name="anthropic">The API settings.</param>
	/// <param name="assistant">The model id.</param>
	/// <param name="httpClient">For tests that capture the request; the SDK's own otherwise.</param>
	/// <returns>The model.</returns>
	public static IChatClient CreateModel(AnthropicOptions anthropic, AssistantOptions assistant, HttpClient? httpClient = null)
	{
		ArgumentNullException.ThrowIfNull(anthropic);
		ArgumentNullException.ThrowIfNull(assistant);
		if (string.IsNullOrWhiteSpace(anthropic.ApiKey))
		{
			return new UnconfiguredChatClient();
		}

		IAnthropicClient client = new AnthropicClient { ApiKey = anthropic.ApiKey, Timeout = anthropic.Timeout };
		client = client.WithOptions(o => o with { MaxRetries = anthropic.MaxRetries });
		if (httpClient is not null)
		{
			client = client.WithOptions(o => o with { HttpClient = httpClient });
		}

		return new ChatClientBuilder(client.AsIChatClient(assistant.Model))
			.UseOpenTelemetry(sourceName: TelemetrySource)
			.Use(inner => new AnthropicRequestShaping(inner, anthropic, assistant.Model))
			.Build();
	}

	/// <summary>
	/// The model with the tool loop around it, for one request: the loop calls the tools the model asks for, at most
	/// <see cref="AssistantOptions.MaxModelCallsPerTurn"/> times; the meter, inside it, records every model call and
	/// stops the loop when the conversation's money runs out.
	/// </summary>
	/// <param name="model">The model.</param>
	/// <param name="turn">The request's turn.</param>
	/// <param name="assistant">Limits and prices.</param>
	/// <param name="clock">The clock.</param>
	/// <returns>The client the runner talks to.</returns>
	public static IChatClient Build(IChatClient model, AgentTurn turn, AssistantOptions assistant, IClock clock)
	{
		ArgumentNullException.ThrowIfNull(assistant);
		return new ChatClientBuilder(model)
			.UseFunctionInvocation(configure: loop =>
			{
				loop.MaximumIterationsPerRequest = assistant.MaxModelCallsPerTurn;
				loop.AllowConcurrentInvocation = false;
				loop.IncludeDetailedErrors = false;
			})
			.Use(inner => new MeteredChatClient(inner, turn, assistant, clock))
			.Build();
	}
}

/// <summary>
/// Shapes each request for the Anthropic API: the tools are declared <c>strict</c> (the API then guarantees the
/// input matches the schema), with effort as configured; and any provider failure becomes
/// <see cref="AssistantUnavailableException"/>, so the runner answers 503 instead of a stack trace.
/// </summary>
/// <param name="inner">The SDK's client.</param>
/// <param name="anthropic">Effort and output cap.</param>
/// <param name="model">The model id.</param>
internal sealed class AnthropicRequestShaping(IChatClient inner, AnthropicOptions anthropic, string model) : DelegatingChatClient(inner)
{
	public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		try
		{
			return await base.GetResponseAsync(messages, Shape(options), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
#pragma warning disable CA1031 // Whatever the provider throws (HTTP, 4xx/5xx, timeout), the customer sees "try again".
		catch (Exception exception)
#pragma warning restore CA1031
		{
			throw new AssistantUnavailableException("The model call failed.", exception);
		}
	}

	public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("The assistant answers whole replies.");

	// The SDK's adapter sends ChatOptions.Tools without "strict", but merges the tools of a raw request: so the tools
	// move there, declared strict. The tool loop above still sees them in the original options.
	private ChatOptions Shape(ChatOptions? options)
	{
		var shaped = options?.Clone() ?? new ChatOptions();
		var tools = (shaped.Tools ?? []).OfType<AIFunctionDeclaration>()
			.Select(static f => new ToolUnion(new Tool
			{
				Name = f.Name,
				Description = f.Description,
				InputSchema = InputSchema.FromRawUnchecked(f.JsonSchema.Deserialize<Dictionary<string, JsonElement>>()!),
				Strict = true,
			}))
			.ToList();
		shaped.Tools = null;
		shaped.MaxOutputTokens = anthropic.MaxOutputTokens;
		shaped.RawRepresentationFactory = _ => new MessageCreateParams
		{
			Model = model,
			MaxTokens = anthropic.MaxOutputTokens,
			Messages = [],
			OutputConfig = new OutputConfig { Effort = anthropic.Effort },
			Tools = tools.Count == 0 ? null : tools,
		};
		return shaped;
	}
}

/// <summary>Records every model call on the turn, with its tokens and cost, and refuses calls once the money is spent.</summary>
/// <param name="inner">The model.</param>
/// <param name="turn">The turn.</param>
/// <param name="assistant">Prices.</param>
/// <param name="clock">The clock.</param>
internal sealed class MeteredChatClient(IChatClient inner, AgentTurn turn, AssistantOptions assistant, IClock clock) : DelegatingChatClient(inner)
{
	public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		if (turn.OverBudget)
		{
			throw new AssistantBudgetExceededException($"The conversation's budget of USD {assistant.MaxCostPerConversationUsd} is spent.");
		}

		var started = clock.GetCurrentInstant();
		var stopwatch = Stopwatch.StartNew();
		var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
		var input = response.Usage?.InputTokenCount ?? 0;
		var output = response.Usage?.OutputTokenCount ?? 0;
		turn.RecordModelCall(started, Duration.FromTimeSpan(stopwatch.Elapsed), input, output, assistant.Cost(input, output));
		return response;
	}

	public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("The assistant answers whole replies.");
}

/// <summary>Stands in when no API key is configured: the app starts, and the assistant answers 503.</summary>
internal sealed class UnconfiguredChatClient : IChatClient
{
	private const string Message = "No Anthropic API key is configured (Anthropic:ApiKey).";

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}
