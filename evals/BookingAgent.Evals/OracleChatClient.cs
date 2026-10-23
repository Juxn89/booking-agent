namespace BookingAgent.Evals;

using System.Text.Json;
using Microsoft.Extensions.AI;
using NodaTime;
using NodaTime.Text;

/// <summary>
/// The ideal assistant for one scenario, read off its expectation rather than the customer's words: it resolves the
/// first day, checks it, offers what is free, holds the expected time once the customer has given their details
/// (checking that day first if it has not yet), takes the nearest alternative when the time was taken, and never
/// confirms. It scores the harness, not a model: every metric must come out perfect, or the scenarios, the
/// harness or the scoring are wrong.
/// </summary>
/// <param name="scenario">The scenario.</param>
/// <param name="set">The scenario set, for its clock.</param>
internal sealed class OracleChatClient(Scenario scenario, ScenarioSet set) : IChatClient
{
	private const string ResolveDate = "resolve_date";
	private const string CheckAvailability = "check_availability";
	private const string HoldSlot = "hold_slot";
	private const string FindMyBookings = "find_my_bookings";
	private const string ProposeCancel = "propose_cancel";
	private const string ProposeReschedule = "propose_reschedule";

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		Task.FromResult(Next([.. messages]));

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}

	private ChatResponse Next(List<ChatMessage> messages)
	{
		if (scenario.Expect is not { } expect)
		{
			return Say("Lo siento, con eso no puedo ayudarte. ¿Quieres que te busque una cita?");
		}

		if (expect.Kind is Expectation.SignIn or Expectation.Cancel or Expectation.Move)
		{
			return Existing(messages, expect);
		}

		var turn = messages.Count(static m => m.Role == ChatRole.User);
		var lastUser = messages.FindLastIndex(static m => m.Role == ChatRole.User);
		var thisTurn = messages.Skip(lastUser + 1).ToList();
		var dayTurn = scenario.DayTurn ?? 1;
		var professional = Names.Professional(expect.Professional!).Value.ToString();
		var service = Names.Service(expect.Service!).Value.ToString();

		if (turn < dayTurn)
		{
			return Say("Tenemos consulta general, limpieza dental, ortodoncia, fisioterapia y masaje. ¿Qué necesitas y para qué día?");
		}

		if (turn == dayTurn)
		{
			if (Result(thisTurn, ResolveDate) is not { } day)
			{
				return Call(ResolveDate, new() { ["expression"] = scenario.FirstDay });
			}

			if (Result(thisTurn, CheckAvailability) is not { } free)
			{
				return Call(CheckAvailability, new() { ["professional_id"] = professional, ["service_id"] = service, ["date"] = day.GetProperty("date").GetString() });
			}

			var times = free.GetProperty("slots").EnumerateArray().Take(3).Select(static s => s.GetProperty("time").GetString());
			return Say($"Tengo libre a las {string.Join(", ", times)}. ¿Cuál prefieres? Necesito también tu nombre y tu correo.");
		}

		if (Results(messages, HoldSlot).Any(static r => r.TryGetProperty("hold_id", out _)))
		{
			return Say("Recuerda que la cita se confirma con el botón Confirmar de la tarjeta; yo no puedo confirmarla.");
		}

		if (Result(thisTurn, HoldSlot) is { } refused)
		{
			var alternatives = refused.GetProperty("alternatives").EnumerateArray().Select(static a => a.GetProperty("time").GetString());
			return Say($"Esa hora ya no está libre. Tengo a las {string.Join(", ", alternatives)}. ¿Cuál prefieres?");
		}

		var target = Target(messages, set.At(expect.Start!));
		var offered = Offered(messages);
		if (!offered.Contains(target) && Result(thisTurn, CheckAvailability) is null)
		{
			var date = LocalDatePattern.Iso.Format(target.InZone(set.Clock).Date);
			return Call(CheckAvailability, new() { ["professional_id"] = professional, ["service_id"] = service, ["date"] = date });
		}

		return Call(HoldSlot, new()
		{
			["professional_id"] = professional,
			["service_id"] = service,
			["start"] = InstantPattern.ExtendedIso.Format(target),
			["customer_name"] = scenario.Customer.Name,
			["customer_email"] = scenario.Customer.Email,
			["customer_phone"] = null,
		});
	}

	// The customer's own appointment: find it, then propose the cancellation or the move (after checking the new day).
	private ChatResponse Existing(List<ChatMessage> messages, Expectation expect)
	{
		if (Result(messages, FindMyBookings) is not { } mine)
		{
			return Call(FindMyBookings, []);
		}

		if (!mine.TryGetProperty("bookings", out var bookings))
		{
			return Say("Para ver tus citas, identifícate con el código que te enviamos por correo, aquí abajo, y vuelve a preguntarme.");
		}

		var booking = bookings.EnumerateArray().First().GetProperty("booking_id").GetString();
		if (expect.Kind == Expectation.Cancel)
		{
			return Result(messages, ProposeCancel) is null
				? Call(ProposeCancel, new() { ["booking_id"] = booking })
				: Say("Si quieres cancelarla, pulsa Cancelar cita en la tarjeta.");
		}

		if (Result(messages, ResolveDate) is not { } day)
		{
			return Call(ResolveDate, new() { ["expression"] = scenario.FirstDay });
		}

		if (Result(messages, CheckAvailability) is null)
		{
			return Call(CheckAvailability, new()
			{
				["professional_id"] = Names.Professional(expect.Professional!).Value.ToString(),
				["service_id"] = Names.Service(expect.Service!).Value.ToString(),
				["date"] = day.GetProperty("date").GetString(),
			});
		}

		return Result(messages, ProposeReschedule) is null
			? Call(ProposeReschedule, new() { ["booking_id"] = booking, ["start"] = InstantPattern.ExtendedIso.Format(set.At(expect.Start!)) })
			: Say("Te aparté la nueva hora; el cambio se hace cuando pulses Confirmar en la tarjeta.");
	}

	// After a refused hold, the alternative nearest the time asked for; otherwise the expected time.
	private static Instant Target(List<ChatMessage> messages, Instant expected)
	{
		var alternatives = Results(messages, HoldSlot)
			.Where(static r => r.TryGetProperty("alternatives", out _))
			.SelectMany(static r => r.GetProperty("alternatives").EnumerateArray())
			.Select(static a => InstantPattern.ExtendedIso.Parse(a.GetProperty("start").GetString()!).Value)
			.ToList();
		return alternatives.Count == 0 ? expected : alternatives.OrderBy(a => Math.Abs((a - expected).TotalTicks)).ThenBy(static a => a).First();
	}

	private static HashSet<Instant> Offered(List<ChatMessage> messages) =>
	[
		.. Results(messages, CheckAvailability)
			.Where(static r => r.TryGetProperty("slots", out _))
			.SelectMany(static r => r.GetProperty("slots").EnumerateArray())
			.Select(static s => InstantPattern.ExtendedIso.Parse(s.GetProperty("start").GetString()!).Value),
	];

	private static JsonElement? Result(List<ChatMessage> messages, string tool) => Results(messages, tool).Cast<JsonElement?>().LastOrDefault();

	private static IEnumerable<JsonElement> Results(List<ChatMessage> messages, string tool)
	{
		var contents = messages.SelectMany(static m => m.Contents).ToList();
		var calls = contents.OfType<FunctionCallContent>().Where(c => c.Name == tool).Select(static c => c.CallId).ToHashSet(StringComparer.Ordinal);
		return contents.OfType<FunctionResultContent>()
			.Where(r => calls.Contains(r.CallId))
			.Select(static r => r.Result is JsonElement element ? element : JsonSerializer.SerializeToElement(r.Result));
	}

	private static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

	private static ChatResponse Call(string tool, Dictionary<string, object?> arguments) =>
		new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"oracle_{Guid.NewGuid():N}", tool, arguments)]));
}
