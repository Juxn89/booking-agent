namespace BookingAgent.Integration.Tests;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookingAgent.Application.Common;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Domain.Assistant;
using BookingAgent.Domain.Bookings;
using BookingAgent.Infrastructure.Ai;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using static ScriptedChatClient;

// The assistant's loop, tools, limits and storage on the real database, with a scripted model. Sunday 1 Nov 2026,
// 06:00 in Managua: "mañana" is Monday 2 Nov, when Dra. López works 08:00-12:00 and 14:00-18:00.
[Collection(PostgresCollection.Name)]
public sealed class AssistantTests(PostgresFixture fixture) : IAsyncLifetime
{
	private static readonly string Lopez = DemoData.Lopez.Value.ToString();
	private static readonly string Cleaning = DemoData.Cleaning.Value.ToString();

	private readonly FakeClock _clock = new(Instant.FromUtc(2026, 11, 1, 12, 0));

	public Task InitializeAsync() => fixture.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	private static async Task<Result<AssistantReply>> SendAsync(ServiceProvider services, Guid? conversation, string message)
	{
		await using var scope = services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<AgentRunner>()
			.HandleAsync(DemoData.Clinic.Value, new AssistantRequest(conversation, message), CancellationToken.None);
	}

	private static Func<IReadOnlyList<ChatMessage>, ChatResponse> CheckTomorrow =>
		messages => Call("check_availability", new()
		{
			["professional_id"] = Lopez,
			["service_id"] = Cleaning,
			["date"] = LastResult(messages, "resolve_date").GetProperty("date").GetString(),
		});

	private static string NineOClock(IReadOnlyList<ChatMessage> messages) =>
		LastResult(messages, "check_availability").GetProperty("slots").EnumerateArray()
			.Single(static s => s.GetProperty("time").GetString() == "09:00").GetProperty("start").GetString()!;

	[Fact]
	public async Task A_conversation_holds_a_slot_and_its_token_goes_to_the_card_never_to_the_model()
	{
		var model = new ScriptedChatClient(
			_ => Call("resolve_date", new() { ["expression"] = "mañana" }),
			CheckTomorrow,
			_ => Say("Mañana la Dra. López tiene libre a las 8:00, 9:00 y 10:00. ¿Cuál prefieres?"),
			messages => Call("hold_slot", new()
			{
				["professional_id"] = Lopez,
				["service_id"] = Cleaning,
				["start"] = NineOClock(messages),
				["customer_name"] = "Ana Pérez",
				["customer_email"] = "ana@ejemplo.com",
				["customer_phone"] = null,
			}),
			_ => Say("Listo, te aparté las 9:00. Confírmala con el botón Confirmar."));
		await using var services = fixture.Services(_clock, model);

		var first = (await SendAsync(services, null, "Quiero una limpieza dental mañana")).Value;
		var second = (await SendAsync(services, first.ConversationId, "A las 9. Soy Ana Pérez, ana@ejemplo.com")).Value;

		Assert.Empty(first.Holds);
		Assert.Equal(first.ConversationId, second.ConversationId);
		var card = Assert.Single(second.Holds);
		Assert.Equal(("Limpieza dental", "Dra. Ana López", new LocalDateTime(2026, 11, 2, 9, 0)), (card.ServiceName, card.ProfessionalName, card.LocalStart));

		// The second reply saw the first one: the history is the server's.
		Assert.Contains(model.Calls[3], static m => m.Role == ChatRole.User && m.Text == "Quiero una limpieza dental mañana");

		await using var db = fixture.CreateContext();
		var booking = await db.Bookings.SingleAsync();
		Assert.Equal((BookingStatus.Held, card.HoldId), (booking.Status, booking.Id.Value));

		var conversation = await db.AgentConversations.SingleAsync();
		Assert.Equal(2, conversation.Turns);
		Assert.DoesNotContain(card.ConfirmationToken, conversation.History, StringComparison.Ordinal);
		Assert.Equal(5 * ScriptedChatClient.InputTokens, conversation.InputTokens);
		Assert.Equal(5 * 0.000125m, conversation.CostUsd);

		var steps = await db.AgentSteps.OrderBy(static s => s.Turn).ThenBy(static s => s.Ordinal).ToListAsync();
		Assert.Equal(
			["1:Model", "1:resolve_date", "1:Model", "1:check_availability", "1:Model", "2:Model", "2:hold_slot", "2:Model"],
			steps.Select(static s => $"{s.Turn}:{s.ToolName ?? s.Kind.ToString()}"));
		var hold = steps.Single(static s => s.ToolName == "hold_slot");
		Assert.Contains("a***@ejemplo.com", hold.Input, StringComparison.Ordinal);
		Assert.DoesNotContain("ana@ejemplo.com", hold.Input + hold.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(card.ConfirmationToken, hold.Output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_bad_tool_input_is_answered_with_what_to_do_and_a_taken_slot_with_alternatives()
	{
		JsonElement badDate = default, taken = default;
		var model = new ScriptedChatClient(
			_ => Call("check_availability", new() { ["professional_id"] = Lopez, ["service_id"] = Cleaning, ["date"] = "el martes" }),
			messages =>
			{
				badDate = LastResult(messages, "check_availability");
				return Call("resolve_date", new() { ["expression"] = "mañana" });
			},
			CheckTomorrow,
			messages => Call("hold_slot", new()
			{
				["professional_id"] = Lopez,
				["service_id"] = Cleaning,
				["start"] = Instant.FromUtc(2026, 11, 2, 15, 0).ToString("g", CultureInfo.InvariantCulture),
				["customer_name"] = "Beto",
				["customer_email"] = "beto@ejemplo.com",
				["customer_phone"] = null,
			}),
			messages =>
			{
				taken = LastResult(messages, "hold_slot");
				return Say("Las 9:00 ya no están libres; ¿te sirven las 8:00 o las 10:00?");
			});
		await using var services = fixture.Services(_clock, model);

		// Someone else holds 9:00 first, through the form.
		await using (var scope = services.CreateAsyncScope())
		{
			var request = new Application.Features.Bookings.HoldRequest(DemoData.Lopez.Value, DemoData.Cleaning.Value, Instant.FromUtc(2026, 11, 2, 15, 0), new("Ana", "ana@ejemplo.com", null));
			Assert.True((await scope.ServiceProvider.GetRequiredService<Application.Features.Bookings.HoldSlot>().HandleAsync(request, CancellationToken.None)).IsSuccess);
		}

		var reply = await SendAsync(services, null, "Una limpieza el martes a las 9, soy Beto, beto@ejemplo.com");

		Assert.Empty(reply.Value.Holds);
		Assert.Contains("resolve_date", badDate.GetProperty("error").GetString(), StringComparison.Ordinal);
		Assert.Contains("no longer free", taken.GetProperty("error").GetString(), StringComparison.Ordinal);
		Assert.Equal(["08:00", "10:00", "10:30"], taken.GetProperty("alternatives").EnumerateArray().Select(static a => a.GetProperty("time").GetString()));
	}

	[Fact]
	public async Task A_conversation_that_spends_its_money_stops_calling_the_model()
	{
		// Each scripted call costs USD 0.000125: the third would go over 0.0002.
		var model = new ScriptedChatClient(
			_ => Call("resolve_date", new() { ["expression"] = "mañana" }),
			_ => Call("resolve_date", new() { ["expression"] = "pasado mañana" }),
			_ => Say("never reached"));
		await using var services = fixture.Services(_clock, model, new Dictionary<string, string?> { ["Assistant:MaxCostPerConversationUsd"] = "0.0002" });

		var reply = (await SendAsync(services, null, "Hola")).Value;

		Assert.Equal(AgentRunner.OutOfBudgetReply, reply.Reply);
		Assert.Equal(1, model.Remaining);
		var next = await SendAsync(services, reply.ConversationId, "¿Sigues ahí?");
		Assert.Equal(AssistantErrors.ConversationLimit, next.Error!.Code);
	}

	[Fact]
	public async Task A_model_going_round_in_circles_is_stopped_and_the_history_stays_valid()
	{
		var loop = Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(_ => Call("resolve_date", new() { ["expression"] = "mañana" }), 10);
		var model = new ScriptedChatClient([.. loop, _ => Say("¿En qué te ayudo?")]);
		await using var services = fixture.Services(_clock, model, new Dictionary<string, string?> { ["Assistant:MaxModelCallsPerTurn"] = "3" });

		var reply = (await SendAsync(services, null, "Hola")).Value;

		Assert.True(model.Calls.Count <= 4, $"{model.Calls.Count} model calls");
		await using var db = fixture.CreateContext();
		var history = JsonSerializer.Deserialize<List<ChatMessage>>((await db.AgentConversations.SingleAsync()).History, AIJsonUtilities.DefaultOptions)!;
		var contents = history.SelectMany(static m => m.Contents).ToList();
		var calls = contents.OfType<FunctionCallContent>().Select(static c => c.CallId);
		Assert.All(calls, id => Assert.Contains(contents.OfType<FunctionResultContent>(), r => r.CallId == id));
		Assert.False(string.IsNullOrWhiteSpace(reply.Reply));
	}

	[Fact]
	public async Task When_the_days_budget_is_spent_the_model_is_not_called()
	{
		var model = new ScriptedChatClient(_ => Say("never reached"));
		await using var services = fixture.Services(_clock, model, new Dictionary<string, string?> { ["Assistant:DailyBudgetUsd"] = "0.01" });
		await using (var db = fixture.CreateContext())
		{
			var earlier = AgentConversation.Start(DemoData.Clinic, _clock.GetCurrentInstant() - Duration.FromHours(1));
			db.AgentConversations.Add(earlier);
			db.AgentSteps.Add(AgentStep.ModelCall(earlier.Id, 1, 1, _clock.GetCurrentInstant() - Duration.FromHours(1), Duration.Zero, 0, 0, 0.02m));
			await db.SaveChangesAsync();
		}

		var reply = await SendAsync(services, null, "Hola");

		Assert.Equal((ErrorKind.Unavailable, AssistantErrors.Unavailable), (reply.Error!.Kind, reply.Error.Code));
		Assert.Empty(model.Calls);
	}

	[Fact]
	public async Task Over_http_without_a_key_the_assistant_is_a_503_and_a_bad_message_a_400()
	{
		using var client = fixture.Api.CreateClient();
		var url = new Uri($"/v1/businesses/{DemoData.Clinic.Value}/assistant/messages", UriKind.Relative);

		using var noKey = await client.PostAsJsonAsync(url, new { conversationId = (Guid?)null, message = "Hola" });
		using var empty = await client.PostAsJsonAsync(url, new { conversationId = (Guid?)null, message = "  " });
		using var unknown = await client.PostAsJsonAsync(url, new { conversationId = Guid.NewGuid(), message = "Hola" });

		Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.StatusCode);
		Assert.Equal(AssistantErrors.Unavailable, (await noKey.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
		Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
	}

	[Fact]
	public async Task A_visitor_writing_faster_than_a_person_is_told_to_wait()
	{
		await using var api = fixture.Api.WithWebHostBuilder(static host => host.UseSetting("AssistantRateLimit:PermitsPerMinute", "2"));
		using var client = api.CreateClient();
		var url = new Uri($"/v1/businesses/{DemoData.Clinic.Value}/assistant/messages", UriKind.Relative);

		var statuses = new List<HttpStatusCode>();
		for (var i = 0; i < 3; i++)
		{
			using var response = await client.PostAsJsonAsync(url, new { conversationId = (Guid?)null, message = "Hola" });
			statuses.Add(response.StatusCode);
			if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				Assert.Equal("assistant.too_many_requests", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
				Assert.Equal("60", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture));
			}
		}

		// Without a key the first two are 503s, but they still count: the limit is on asking, not on answers.
		Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests], statuses);
	}

	[Fact]
	public async Task Requests_to_anthropic_declare_the_tools_strict_with_low_effort_and_failures_become_unavailable()
	{
		using var handler = new CapturingHandler();
		using var http = new HttpClient(handler);
		var model = AssistantPipeline.CreateModel(new AnthropicOptions { ApiKey = "test", MaxRetries = 0 }, new AssistantOptions(), http);
		await using var services = fixture.Services(_clock);
		await using var scope = services.CreateAsyncScope();
		var tools = scope.ServiceProvider.GetRequiredService<Application.Features.Assistant.Tools.BookingTools>();

		await Assert.ThrowsAsync<AssistantUnavailableException>(() =>
			model.GetResponseAsync([new(ChatRole.System, "sys"), new(ChatRole.User, "hola")], new ChatOptions { Tools = tools.All }));

		var request = JsonDocument.Parse(handler.Body!).RootElement;
		Assert.Equal(("claude-haiku-5-5", 2048, "low"), (request.GetProperty("model").GetString(), request.GetProperty("max_tokens").GetInt32(), request.GetProperty("output_config").GetProperty("effort").GetString()));
		var declared = request.GetProperty("tools").EnumerateArray().ToList();
		Assert.Equal(["list_services", "list_professionals", "resolve_date", "check_availability", "hold_slot"], declared.Select(static t => t.GetProperty("name").GetString()));
		Assert.All(declared, static t =>
		{
			Assert.True(t.GetProperty("strict").GetBoolean());
			Assert.False(t.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean());
		});
	}

	private sealed class CapturingHandler : HttpMessageHandler
	{
		public string? Body { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Body = await request.Content!.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"type":"error","error":{"type":"api_error","message":"boom"}}""") };
		}
	}
}
