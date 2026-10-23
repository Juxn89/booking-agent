namespace BookingAgent.Evals;

using System.Text.Json;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using BookingAgent.Infrastructure;
using BookingAgent.Infrastructure.Ai;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NodaTime.Testing;
using Testcontainers.PostgreSql;

/// <summary>One message and its answer.</summary>
internal sealed record Exchange(string Customer, string? Assistant, int Holds, int Cancellations, bool SignInRequired, string? Error);

/// <summary>A booking of the scenario's customer, as the database has it at the end.</summary>
internal sealed record BookingSeen(BookingStatus Status, Guid ProfessionalId, Guid ServiceId, LocalDateTime LocalStart, bool Existing, Guid? Replaces);

/// <summary>A step the assistant took.</summary>
internal sealed record StepSeen(int Turn, int Ordinal, string? Tool, string? Input, string? Output, decimal CostUsd);

/// <summary>Everything a scenario's scoring needs.</summary>
internal sealed record ScenarioRun(Scenario Scenario, IReadOnlyList<Exchange> Exchanges, IReadOnlyList<BookingSeen> Bookings, IReadOnlyList<StepSeen> Steps, decimal CostUsd);

/// <summary>
/// A real Postgres with the demo clinic, reset before each scenario to the same agenda, and the assistant wired as
/// the API wires it, with a fixed clock and the model under evaluation.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
	private const string Image = "postgres:18.6-alpine";
	private const string AgendaEmail = "agenda@ejemplo.com";
	private const string OtherCustomerEmail = "otro.cliente@ejemplo.com";

	private readonly PostgreSqlContainer _container;

	private Harness(PostgreSqlContainer container) => _container = container;

	public static async Task<Harness> StartAsync()
	{
		var container = new PostgreSqlBuilder(Image).Build();
		await container.StartAsync();
		var harness = new Harness(container);
		await using var services = harness.Services(new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), model: null);
		await DatabaseMigrator.MigrateAsync(services);
		await DemoData.SeedAsync(services);
		return harness;
	}

	public async ValueTask DisposeAsync() => await _container.DisposeAsync();

	public async Task<ScenarioRun> RunAsync(ScenarioSet set, Scenario scenario, IChatClient model)
	{
		var clock = new FakeClock(set.Now);
		await using var services = Services(clock, model);
		await ResetAsync(set, services);
		foreach (var own in scenario.Bookings ?? [])
		{
			await BookAsync(set, services, own.Professional, own.Service, own.Start, scenario.Customer.Email, scenario.Customer.Name);
		}

		HashSet<Guid> existing;
		SignedInCustomer? customer = null;
		await using (var scope = services.CreateAsyncScope())
		{
			var before = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
			existing = [.. await before.Bookings.Select(static b => b.Id.Value).ToListAsync()];
			if (scenario.SignedIn)
			{
				var known = await before.Customers.SingleAsync(c => c.Email == scenario.Customer.Email);
				customer = new SignedInCustomer(known.Id, known.BusinessId, known.Name, known.Email, set.Now + Duration.FromHours(2));
			}
		}

		var exchanges = new List<Exchange>();
		Guid? conversation = null;
		for (var turn = 1; turn <= scenario.Turns.Count; turn++)
		{
			var message = scenario.Turns[turn - 1];
			await using (var scope = services.CreateAsyncScope())
			{
				var result = await scope.ServiceProvider.GetRequiredService<AgentRunner>()
					.HandleAsync(DemoData.Clinic.Value, new AssistantRequest(conversation, message), customer, CancellationToken.None);
				if (!result.IsSuccess)
				{
					exchanges.Add(new Exchange(message, null, 0, 0, false, result.Error!.Code));
					break;
				}

				conversation = result.Value.ConversationId;
				exchanges.Add(new Exchange(message, result.Value.Reply, result.Value.Holds.Count, result.Value.Cancellations.Count, result.Value.SignInRequired, null));
			}

			if (scenario.ConflictAfterTurn == turn && scenario.Expect is { Professional: { } professional, Service: { } service, Start: { } start })
			{
				await BookAsync(set, services, professional, service, start, OtherCustomerEmail, "Otra persona");
			}
		}

		await using var reading = services.CreateAsyncScope();
		var db = reading.ServiceProvider.GetRequiredService<BookingDbContext>();
		var bookings = await db.Bookings.AsNoTracking()
			.Join(db.Customers.Where(c => c.Email == scenario.Customer.Email), static b => b.CustomerId, static c => c.Id, static (b, _) => b)
			.ToListAsync();
		var steps = await db.AgentSteps.AsNoTracking()
			.Where(s => s.ConversationId == conversation)
			.OrderBy(static s => s.Turn).ThenBy(static s => s.Ordinal)
			.ToListAsync();
		var zone = set.Clock;
		return new ScenarioRun(
			scenario,
			exchanges,
			[.. bookings.Select(b => new BookingSeen(b.Status, b.ProfessionalId.Value, b.ServiceId.Value, b.Period.Start.InZone(zone).LocalDateTime, existing.Contains(b.Id.Value), b.Replaces?.Value))],
			[.. steps.Select(static s => new StepSeen(s.Turn, s.Ordinal, s.ToolName, s.Input, s.Output, s.CostUsd))],
			steps.Sum(static s => s.CostUsd));
	}

	private ServiceProvider Services(FakeClock clock, IChatClient? model)
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = _container.GetConnectionString(),
				["Sweeper:Enabled"] = "false",
				["Notifications:DispatcherEnabled"] = "false",
			})
			.Build();
		var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
		services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
		if (model is not null)
		{
			services.AddKeyedSingleton(AssistantPipeline.ModelKey, model);
		}

		return services.BuildServiceProvider();
	}

	// Every scenario starts from the same calendar: the agenda booked and confirmed, the time off in place.
	private static async Task ResetAsync(ScenarioSet set, ServiceProvider services)
	{
		await using (var scope = services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
			await db.Database.ExecuteSqlRawAsync("TRUNCATE agent_steps, agent_conversations, customer_sessions, sign_in_codes, inbox_messages, notifications, bookings, customers, time_off");
			db.TimeOff.AddRange(set.TimeOff.Select(t => TimeOff.Create(Names.Professional(t.Professional), new Interval(set.At(t.From), set.At(t.To)), t.Reason)));
			await db.SaveChangesAsync();
		}

		foreach (var entry in set.Agenda)
		{
			await BookAsync(set, services, entry.Professional, entry.Service, entry.Start, AgendaEmail, "Otra persona");
		}
	}

	private static async Task BookAsync(ScenarioSet set, ServiceProvider services, string professional, string service, string start, string email, string name)
	{
		await using var scope = services.CreateAsyncScope();
		var request = new HoldRequest(Names.Professional(professional).Value, Names.Service(service).Value, set.At(start), new CustomerDetails(name, email, null));
		var hold = await scope.ServiceProvider.GetRequiredService<HoldSlot>().HandleAsync(request, CancellationToken.None);
		if (!hold.IsSuccess)
		{
			throw new InvalidDataException($"The agenda cannot book {professional} {service} at {start}: {hold.Error!.Code}.");
		}

		var confirmed = await scope.ServiceProvider.GetRequiredService<ConfirmHold>()
			.HandleAsync(hold.Value.Booking.Id, hold.Value.ConfirmationToken, CancellationToken.None);
		if (!confirmed.IsSuccess)
		{
			throw new InvalidDataException($"The agenda cannot confirm {professional} {service} at {start}: {confirmed.Error!.Code}.");
		}
	}
}

/// <summary>Reads a tool's recorded JSON.</summary>
internal static class StepJson
{
	public static JsonElement? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement;
}
