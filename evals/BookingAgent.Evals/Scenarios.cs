namespace BookingAgent.Evals;

using System.Text.Json;
using BookingAgent.Domain;
using BookingAgent.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Text;

/// <summary>evals/scenarios.json: a fixed "today", a fixed agenda, and the conversations.</summary>
internal sealed record ScenarioSet(string Today, string Zone, IReadOnlyList<AgendaEntry> Agenda, IReadOnlyList<TimeOffEntry> TimeOff, IReadOnlyList<Scenario> Scenarios)
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public DateTimeZone Clock => DateTimeZoneProviders.Tzdb[Zone];

	public Instant Now => At(Today);

	public static ScenarioSet Read(string path) =>
		JsonSerializer.Deserialize<ScenarioSet>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");

	/// <summary>A local date and time of the scenarios, on the business's clock.</summary>
	public Instant At(string local) => Clock.AtStrictly(LocalDateTimePattern.GeneralIso.Parse(local).Value).ToInstant();
}

/// <summary>A booking already in the calendar when every scenario starts.</summary>
internal sealed record AgendaEntry(string Professional, string Service, string Start);

/// <summary>A professional's time off when every scenario starts.</summary>
internal sealed record TimeOffEntry(string Professional, string From, string To, string Reason);

/// <summary>The simulated customer.</summary>
internal sealed record Person(string Name, string Email);

/// <summary>The hold a scenario should end with. For a conflict, the time someone else takes mid-conversation.</summary>
internal sealed record Expectation(string Professional, string Service, string Start);

/// <summary>
/// One conversation: what the customer writes, turn by turn. <see cref="DayTurn"/> is the turn that names the day
/// (1 unless the customer asks something else first); <see cref="ConflictAfterTurn"/>, for a conflict, is when
/// another customer takes the expected time; <see cref="FirstDay"/> is the day the customer first asks for, which
/// the assistant must work out from their words.
/// </summary>
internal sealed record Scenario(
	string Id,
	string Category,
	Person Customer,
	IReadOnlyList<string> Turns,
	int? DayTurn,
	int? ConflictAfterTurn,
	string? FirstDay,
	Expectation? Expect)
{
	public const string Conflict = "conflict";

	public bool IsConflict => Category == Conflict;
}

/// <summary>The demo clinic's names as the scenarios write them.</summary>
internal static class Names
{
	public static ProfessionalId Professional(string name) => name switch
	{
		"lopez" => DemoData.Lopez,
		"martinez" => DemoData.Martinez,
		"rivera" => DemoData.Rivera,
		_ => throw new InvalidDataException($"Unknown professional '{name}'."),
	};

	public static ServiceId Service(string name) => name switch
	{
		"consultation" => DemoData.Consultation,
		"cleaning" => DemoData.Cleaning,
		"orthodontics" => DemoData.Orthodontics,
		"physiotherapy" => DemoData.Physiotherapy,
		"massage" => DemoData.Massage,
		_ => throw new InvalidDataException($"Unknown service '{name}'."),
	};
}
