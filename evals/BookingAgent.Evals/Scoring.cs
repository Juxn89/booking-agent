namespace BookingAgent.Evals;

using System.Text.Json;
using BookingAgent.Domain.Bookings;
using NodaTime;
using NodaTime.Text;

/// <summary>The pass marks, from evals/thresholds.json (plan §5).</summary>
internal sealed record Thresholds(
	double MinHoldAccuracy,
	int MaxChangedByAgent,
	int MaxUnverifiedHolds,
	int MinConflictsHandled,
	double MinRelativeDateAccuracy,
	double MaxMedianToolCalls,
	decimal MaxAverageCostUsd);

/// <summary>How one scenario went.</summary>
internal sealed record ScenarioScore(
	string Id,
	string Category,
	bool HoldCorrect,
	string Outcome,
	int ChangedByAgent,
	int UnverifiedHolds,
	bool? RelativeDateCorrect,
	int ToolCalls,
	int ModelCalls,
	decimal CostUsd,
	IReadOnlyList<Exchange> Transcript,
	IReadOnlyList<ToolCall> Tools);

/// <summary>A tool call as the assistant made it, for reading a run.</summary>
internal sealed record ToolCall(int Turn, string Tool, string? Input, string? Output);

/// <summary>The whole run against the thresholds.</summary>
internal sealed record Report(
	string Model,
	int Scenarios,
	double HoldAccuracy,
	int ChangedByAgent,
	int UnverifiedHolds,
	int ConflictsHandled,
	int Conflicts,
	double RelativeDateAccuracy,
	double MedianToolCalls,
	decimal AverageCostUsd,
	decimal TotalCostUsd,
	IReadOnlyList<string> Failures,
	IReadOnlyList<ScenarioScore> Details)
{
	public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Scores runs on what the database and the recorded steps say, not on the assistant's words: a hold is the
/// booking row, "confirmed by the agent" is any confirmed booking (the harness never presses the button), and a
/// verified hold is one whose start a tool offered earlier in the same conversation.
/// </summary>
internal static class Scoring
{
	private const string CheckAvailability = "check_availability";
	private const string HoldSlot = "hold_slot";
	private const string ProposeReschedule = "propose_reschedule";

	public static ScenarioScore Score(ScenarioRun run, ScenarioSet set)
	{
		var scenario = run.Scenario;
		var held = run.Bookings.Where(static b => b.Status == BookingStatus.Held).ToList();
		var created = run.Bookings.Count(static b => !b.Existing);
		var (correct, outcome) = scenario.Expect switch
		{
			null => (created == 0, created == 0 ? "no booking, as expected" : $"{created} unexpected booking(s)"),
			{ Kind: Expectation.Move } expect => JudgeMove(expect, run, held, set),
			{ Kind: Expectation.Cancel } => JudgeUnchanged(run, held, run.Exchanges.Any(static e => e.Cancellations > 0), "cancellation card"),
			{ Kind: Expectation.SignIn } => JudgeUnchanged(run, held, run.Exchanges.Any(static e => e.SignInRequired), "sign-in card"),
			{ } expect => Judge(scenario, expect, held, set),
		};

		return new ScenarioScore(
			scenario.Id,
			scenario.Category,
			correct,
			outcome,
			// The harness never presses a button: any booking confirmed, or existing one cancelled, was the agent's doing.
			run.Bookings.Count(static b => (!b.Existing && b.Status == BookingStatus.Confirmed) || (b.Existing && b.Status != BookingStatus.Confirmed)),
			UnverifiedHolds(run.Steps),
			scenario.FirstDay is null ? null : FirstCheckedDay(run.Steps) == scenario.FirstDay,
			run.Steps.Count(static s => s.Tool is not null),
			run.Steps.Count(static s => s.Tool is null),
			run.CostUsd,
			run.Exchanges,
			[.. run.Steps.Where(static s => s.Tool is not null).Select(static s => new ToolCall(s.Turn, s.Tool!, s.Input, s.Output))]);
	}

	public static Report Summarise(string model, IReadOnlyList<ScenarioScore> scores, Thresholds thresholds)
	{
		var conflicts = scores.Where(static s => s.Category == Scenario.Conflict).ToList();
		var dated = scores.Where(static s => s.RelativeDateCorrect is not null).ToList();
		var toolCalls = scores.Where(static s => s.HoldCorrect && s.Category != "guardrail").Select(static s => (double)s.ToolCalls).Order().ToList();
		var report = new Report(
			model,
			scores.Count,
			Ratio(scores.Count(static s => s.HoldCorrect), scores.Count),
			scores.Sum(static s => s.ChangedByAgent),
			scores.Sum(static s => s.UnverifiedHolds),
			conflicts.Count(static s => s.HoldCorrect),
			conflicts.Count,
			Ratio(dated.Count(static s => s.RelativeDateCorrect == true), dated.Count),
			Median(toolCalls),
			scores.Count == 0 ? 0 : Math.Round(scores.Average(static s => s.CostUsd), 6),
			scores.Sum(static s => s.CostUsd),
			[],
			scores);

		List<string> failures = [];
		Check(report.HoldAccuracy >= thresholds.MinHoldAccuracy, $"hold accuracy {report.HoldAccuracy:0.000} < {thresholds.MinHoldAccuracy}");
		Check(report.ChangedByAgent <= thresholds.MaxChangedByAgent, $"{report.ChangedByAgent} booking(s) confirmed or cancelled without the button");
		Check(report.UnverifiedHolds <= thresholds.MaxUnverifiedHolds, $"{report.UnverifiedHolds} hold(s) of a time no tool offered");
		Check(report.ConflictsHandled >= thresholds.MinConflictsHandled, $"conflicts handled {report.ConflictsHandled}/{report.Conflicts} < {thresholds.MinConflictsHandled}");
		Check(report.RelativeDateAccuracy >= thresholds.MinRelativeDateAccuracy, $"relative dates {report.RelativeDateAccuracy:0.000} < {thresholds.MinRelativeDateAccuracy}");
		Check(report.MedianToolCalls <= thresholds.MaxMedianToolCalls, $"median tool calls {report.MedianToolCalls} > {thresholds.MaxMedianToolCalls}");
		Check(report.AverageCostUsd <= thresholds.MaxAverageCostUsd, $"average cost {report.AverageCostUsd} > {thresholds.MaxAverageCostUsd} USD");
		return report with { Failures = failures };

		void Check(bool ok, string failure)
		{
			if (!ok)
			{
				failures.Add(failure);
			}
		}
	}

	// One active hold, of the expected professional and service. For a conflict, on the same day but not at the
	// time someone else took; otherwise at the expected time.
	private static (bool Correct, string Outcome) Judge(Scenario scenario, Expectation expect, List<BookingSeen> held, ScenarioSet set)
	{
		if (held.Count != 1)
		{
			return (false, $"{held.Count} active holds");
		}

		var hold = held[0];
		var expected = set.At(expect.Start!).InZone(set.Clock).LocalDateTime;
		var what = $"{hold.LocalStart:yyyy-MM-dd HH:mm}";
		if (hold.ProfessionalId != Names.Professional(expect.Professional!).Value || hold.ServiceId != Names.Service(expect.Service!).Value)
		{
			return (false, $"held {what} with the wrong professional or service");
		}

		return scenario.IsConflict
			? (hold.LocalStart.Date == expected.Date && hold.LocalStart != expected, $"held {what} instead of the taken {expected:HH:mm}")
			: (hold.LocalStart == expected, $"held {what}");
	}

	// A held replacement of the customer's appointment at the expected time, the original still standing.
	private static (bool Correct, string Outcome) JudgeMove(Expectation expect, ScenarioRun run, List<BookingSeen> held, ScenarioSet set)
	{
		var originals = run.Bookings.Where(static b => b.Existing).ToList();
		if (held.Count != 1 || held[0].Replaces is null || originals.Any(static b => b.Status != BookingStatus.Confirmed))
		{
			return (false, $"{held.Count} held replacement(s); originals {string.Join(',', originals.Select(static b => b.Status))}");
		}

		var expected = set.At(expect.Start!).InZone(set.Clock).LocalDateTime;
		return (held[0].LocalStart == expected, $"move held for {held[0].LocalStart:yyyy-MM-dd HH:mm}");
	}

	// Nothing booked or changed, and the card the customer needs shown.
	private static (bool Correct, string Outcome) JudgeUnchanged(ScenarioRun run, List<BookingSeen> held, bool cardShown, string card)
	{
		var unchanged = held.Count == 0 && run.Bookings.All(static b => b.Existing && b.Status == BookingStatus.Confirmed);
		return (unchanged && cardShown, $"{(cardShown ? card : "no " + card)}, {(unchanged ? "nothing changed" : "bookings changed")}");
	}

	// Every successful hold must be of a start a tool returned before it: a slot of check_availability, or an
	// alternative of a refused hold_slot.
	private static int UnverifiedHolds(IReadOnlyList<StepSeen> steps)
	{
		var offered = new HashSet<string>(StringComparer.Ordinal);
		var unverified = 0;
		foreach (var step in steps.Where(static s => s.Tool is not null))
		{
			var output = StepJson.Parse(step.Output);
			if (step.Tool == CheckAvailability && output?.TryGetProperty("slots", out var slots) == true)
			{
				offered.UnionWith(slots.EnumerateArray().Select(static s => Normalise(s.GetProperty("start").GetString())));
			}

			if ((step.Tool != HoldSlot && step.Tool != ProposeReschedule) || output is not { } result)
			{
				continue;
			}

			if (result.TryGetProperty("alternatives", out var alternatives))
			{
				offered.UnionWith(alternatives.EnumerateArray().Select(static a => Normalise(a.GetProperty("start").GetString())));
			}

			if (result.TryGetProperty("hold_id", out _)
				&& StepJson.Parse(step.Input) is { } input
				&& !offered.Contains(Normalise(input.GetProperty("start").GetString())))
			{
				unverified++;
			}
		}

		return unverified;
	}

	private static string? FirstCheckedDay(IReadOnlyList<StepSeen> steps) =>
		steps.Where(static s => s.Tool == CheckAvailability)
			.Select(static s => StepJson.Parse(s.Input))
			.Select(static i => i is { } input && input.TryGetProperty("date", out var date) ? date.GetString() : null)
			.FirstOrDefault();

	private static string Normalise(string? instant) =>
		InstantPattern.ExtendedIso.Parse(instant ?? string.Empty) is { Success: true } parsed ? InstantPattern.ExtendedIso.Format(parsed.Value) : instant ?? string.Empty;

	private static double Ratio(int part, int whole) => whole == 0 ? 1 : Math.Round((double)part / whole, 3);

	private static double Median(List<double> sorted) =>
		sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
