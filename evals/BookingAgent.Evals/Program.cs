using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Evals;
using BookingAgent.Infrastructure.Ai;
using Microsoft.Extensions.AI;

// Paths are relative to the repo root, found by walking up to the solution file, so the tool runs the same from the
// root, from evals/ or from `dotnet run --project`.
const string SolutionFile = "BookingAgent.slnx";
const string ScenariosFile = "evals/scenarios.json";
const string ThresholdsFile = "evals/thresholds.json";
const string ResultsFolder = "evals/results";
const string ApiKeyVariable = "ANTHROPIC_API_KEY";

// A full run is about 25 conversations of 4 to 8 calls; the defaults leave room for one run.
const int DefaultMaxCalls = 220;
const decimal DefaultMaxCostUsd = 0.10m;

var root = FindRoot(Directory.GetCurrentDirectory());
var command = args.Length > 0 ? args[0] : string.Empty;
var model = args.Length > 1 ? args[1] : string.Empty;

return (command, model) switch
{
	("run", "oracle") => await RunAsync(root, "oracle", (scenario, set) => new OracleChatClient(scenario, set), Only(args)),
	("run", "claude") => await RunPaidAsync(root, args),
	_ => Usage(),
};

static int Usage()
{
	Console.Error.WriteLine("""
		usage:
		  evals run oracle [--only id,id]
		  evals run claude --allow-paid [--only id,id] [--max-calls N] [--max-cost USD]
		""");
	return 2;
}

// Calls the paid API only with --allow-paid, within the run's limits and what the shared ledger says is left, and
// records the run in the ledger even when it stops halfway.
static async Task<int> RunPaidAsync(string root, string[] args)
{
	if (!args.Contains("--allow-paid"))
	{
		Console.Error.WriteLine("This run calls the Anthropic API and costs money. Add --allow-paid to proceed.");
		return 2;
	}

	var apiKey = DotEnv.Read(root, ApiKeyVariable);
	if (string.IsNullOrEmpty(apiKey))
	{
		Console.Error.WriteLine($"{ApiKeyVariable} is not set in .env or the environment.");
		return 2;
	}

	var ledger = Ledger.Default;
	var remaining = ledger.Remaining();
	var maxCost = Math.Min(Option(args, "--max-cost", DefaultMaxCostUsd), remaining);
	if (maxCost <= 0)
	{
		Console.Error.WriteLine($"Nothing left of the {ledger.SessionCapUsd} USD allocated to {ledger.Session}.");
		return 2;
	}

	// No SDK retries: a retried call can be billed twice, and the run stops at the first error instead.
	var anthropic = new AnthropicOptions { ApiKey = apiKey, MaxRetries = 0 };
	var assistant = new AssistantOptions();
	using var guard = new SpendingGuard(AssistantPipeline.CreateModel(anthropic, assistant), (int)Option(args, "--max-calls", DefaultMaxCalls), maxCost, assistant, anthropic.MaxOutputTokens);
	Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{assistant.Model}: up to {maxCost:F4} USD ({remaining:F4} left in {ledger.Session})"));

	var only = Only(args);
	var note = "completed";
	try
	{
		return await RunAsync(root, assistant.Model, (_, _) => guard, only);
	}
	catch (BudgetExceededException exception)
	{
		note = $"stopped by the guard: {exception.Message}";
		Console.Error.WriteLine(note);
		return 1;
	}
	catch (Exception exception)
	{
		note = $"failed: {exception.GetType().Name}";
		throw;
	}
	finally
	{
		if (guard.Calls > 0)
		{
			ledger.Append(only is null ? "eval-full" : $"eval-{only.Count}", assistant.Model, guard, $"booking-agent evals, {note}; usage from each response");
			Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ledger: {guard.Calls} calls, {guard.CostUsd:F6} USD recorded"));
		}
	}
}

static async Task<int> RunAsync(string root, string model, Func<Scenario, ScenarioSet, IChatClient> modelFor, IReadOnlyCollection<string>? only)
{
	var set = ScenarioSet.Read(Path.Combine(root, ScenariosFile));
	var thresholds = JsonSerializer.Deserialize<Thresholds>(File.ReadAllText(Path.Combine(root, ThresholdsFile)), Json.Options)!;
	var scenarios = only is null ? set.Scenarios : [.. set.Scenarios.Where(s => only.Contains(s.Id))];

	await using var harness = await Harness.StartAsync();
	var scores = new List<ScenarioScore>();
	foreach (var scenario in scenarios)
	{
		var score = Scoring.Score(await harness.RunAsync(set, scenario, modelFor(scenario, set)), set);
		scores.Add(score);
		Console.WriteLine(string.Create(
			CultureInfo.InvariantCulture,
			$"{score.Id,-4} {(score.HoldCorrect ? "ok  " : "FAIL")} {score.Outcome,-45} tools {score.ToolCalls,2}  calls {score.ModelCalls,2}  {score.CostUsd:F6} USD"));
	}

	// A subset is for looking closely: show what the assistant asked the tools.
	if (only is not null)
	{
		foreach (var call in scores.SelectMany(static s => s.Tools.Select(t => $"  {s.Id} t{t.Turn} {t.Tool} {t.Input} -> {t.Output}")))
		{
			Console.WriteLine(call);
		}
	}

	var report = Scoring.Summarise(model, scores, thresholds);
	Console.WriteLine(string.Create(
		CultureInfo.InvariantCulture,
		$"""

		holds {report.HoldAccuracy:0.000} · confirmed by the agent {report.AgentConfirmed} · unverified holds {report.UnverifiedHolds} · conflicts {report.ConflictsHandled}/{report.Conflicts}
		relative dates {report.RelativeDateAccuracy:0.000} · median tool calls {report.MedianToolCalls} · average {report.AverageCostUsd:F6} USD · total {report.TotalCostUsd:F6} USD
		"""));

	// A partial run is printed, not saved: the versioned results are whole runs.
	if (only is null)
	{
		var file = Path.Combine(root, ResultsFolder, $"{model}.json");
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		await File.WriteAllTextAsync(file, JsonSerializer.Serialize(report, Json.Results) + Environment.NewLine);
		Console.WriteLine($"-> {Path.GetRelativePath(root, file)}");
	}

	foreach (var failure in report.Failures)
	{
		Console.Error.WriteLine($"FAILED: {failure}");
	}

	return report.Passed ? 0 : 1;
}

static IReadOnlyCollection<string>? Only(string[] args) =>
	Text(args, "--only")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

static decimal Option(string[] args, string name, decimal fallback) =>
	Text(args, name) is { } value ? decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;

static string? Text(string[] args, string name)
{
	var index = Array.IndexOf(args, name);
	return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindRoot(string start)
{
	for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
	{
		if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
		{
			return directory.FullName;
		}
	}

	throw new DirectoryNotFoundException($"No {SolutionFile} above {start}.");
}

/// <summary>JSON settings of the files the evals read and write.</summary>
internal static class Json
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	// Indented, camelCase, Spanish kept readable in the diff.
	public static readonly JsonSerializerOptions Results = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
	};
}
