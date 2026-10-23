namespace BookingAgent.Architecture.Tests;

using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using BookingAgent.Application.Features.Bookings;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

/// <summary>
/// The assistant proposes, the person confirms (ADR 0003): nothing in the tools' namespace may reach the use cases
/// that confirm or cancel a booking. A confirmation needs the hold's token, which only the widget's card has; a
/// cancellation, the customer's session and the card's button. Moving is allowed because it only holds the new time:
/// the move itself is the confirmation of that hold.
/// </summary>
public sealed class AssistantToolRules
{
	private const string ToolsNamespace = "BookingAgent.Application.Features.Assistant.Tools";

	private static readonly IObjectProvider<IType> Tools =
		Types().That().ResideInNamespace(ToolsNamespace).As("Assistant tools");

	[Fact]
	public void No_tool_can_confirm_or_cancel_a_booking() =>
		Types().That().Are(Tools)
			.Should().NotDependOnAny(typeof(ConfirmHold))
			.AndShould().NotDependOnAny(typeof(CancelBooking))
			.Check(LayerDependencyRules.Architecture);
}
