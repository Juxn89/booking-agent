namespace BookingAgent.Evals;

using System.Globalization;
using BookingAgent.Application.Features.Assistant;
using Microsoft.Extensions.AI;

/// <summary>A paid call was refused before it was sent.</summary>
/// <param name="message">Why.</param>
internal sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>
/// Counts and prices every model call of a paid run, and refuses the next one before it is sent when, in the worst
/// case (its input so far plus the whole output cap), it could take the run past its call or cost limit.
/// </summary>
/// <param name="inner">The model.</param>
/// <param name="maxCalls">Calls allowed in this run.</param>
/// <param name="maxCostUsd">Spend allowed in this run, already capped by what the ledger says is left.</param>
/// <param name="prices">The model's prices.</param>
/// <param name="maxOutputTokens">The output cap of one call.</param>
internal sealed class SpendingGuard(IChatClient inner, int maxCalls, decimal maxCostUsd, AssistantOptions prices, int maxOutputTokens) : DelegatingChatClient(inner)
{
	// A reply's input is the prompt, the tools and the conversation so far: under 8,000 tokens in the smoke runs.
	private const long WorstCaseInputTokens = 8_000;

	public int Calls { get; private set; }

	public long InputTokens { get; private set; }

	public long OutputTokens { get; private set; }

	public decimal CostUsd { get; private set; }

	public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		if (Calls + 1 > maxCalls)
		{
			throw new BudgetExceededException($"call {Calls + 1} would exceed --max-calls {maxCalls}");
		}

		var worstCase = prices.Cost(WorstCaseInputTokens, maxOutputTokens);
		if (CostUsd + worstCase > maxCostUsd)
		{
			throw new BudgetExceededException(string.Create(CultureInfo.InvariantCulture, $"call {Calls + 1} could cost up to {worstCase:F6} USD; spent {CostUsd:F6} of {maxCostUsd:F6} USD"));
		}

		var response = await base.GetResponseAsync(messages, options, cancellationToken);
		var input = response.Usage?.InputTokenCount ?? 0;
		var output = response.Usage?.OutputTokenCount ?? 0;
		Calls++;
		InputTokens += input;
		OutputTokens += output;
		CostUsd += prices.Cost(input, output);
		return response;
	}
}

/// <summary>
/// The budget ledger shared by every session on the same API account: a TSV outside the repo
/// (<c>~/.anthropic-budget/ledger.tsv</c>). Read before a paid run to know what is left; one row appended after it
/// with the real usage. Append-only: rows are never rewritten.
/// </summary>
/// <param name="Path">The ledger file.</param>
/// <param name="Session">This project's session in the allocation table.</param>
/// <param name="SessionCapUsd">The cap the user approved for it.</param>
internal sealed record Ledger(string Path, string Session, decimal SessionCapUsd)
{
	private const int SessionColumn = 1;
	private const int UsdColumn = 10;

	/// <summary>This repo's allocation, approved by the user on 2026-10-22 (see the ledger's README).</summary>
	public static Ledger Default { get; } = new(
		System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anthropic-budget", "ledger.tsv"),
		"workana-booking-agent",
		0.50m);

	public decimal Remaining() =>
		SessionCapUsd - File.ReadLines(Path)
			.Skip(1)
			.Where(static line => !string.IsNullOrWhiteSpace(line))
			.Select(static line => line.Split('\t'))
			.Where(columns => columns.Length > UsdColumn && columns[SessionColumn] == Session)
			.Sum(static columns => decimal.Parse(columns[UsdColumn], NumberStyles.Float, CultureInfo.InvariantCulture));

	public void Append(string experiment, string model, SpendingGuard guard, string note)
	{
		ArgumentNullException.ThrowIfNull(guard);
		var row = string.Join(
			'\t',
			DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
			Session,
			"booking-agent",
			experiment,
			model,
			guard.Calls.ToString(CultureInfo.InvariantCulture),
			guard.InputTokens.ToString(CultureInfo.InvariantCulture),
			"0",
			"0",
			guard.OutputTokens.ToString(CultureInfo.InvariantCulture),
			guard.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture),
			note);
		File.AppendAllText(Path, row + Environment.NewLine);
	}
}

/// <summary>Reads one <c>KEY=value</c> line from the repo's git-ignored <c>.env</c>, never printing it.</summary>
internal static class DotEnv
{
	public static string? Read(string root, string key)
	{
		var path = Path.Combine(root, ".env");
		var prefix = key + "=";
		var value = File.Exists(path)
			? File.ReadLines(path).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim().Trim('"', '\'')
			: null;
		return string.IsNullOrEmpty(value) ? Environment.GetEnvironmentVariable(key) : value;
	}
}
