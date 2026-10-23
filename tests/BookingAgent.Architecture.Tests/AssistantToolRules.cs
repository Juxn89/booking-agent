namespace BookingAgent.Architecture.Tests;

using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using BookingAgent.Application.Features.Bookings;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

/// <summary>
/// The assistant proposes, the person confirms (ADR 0003): nothing in the tools' namespace may reach the use cases
/// that confirm, cancel or move a booking. A confirmation needs the hold's token, which only the widget's card has.
/// </summary>
public sealed class AssistantToolRules
{
	private const string ToolsNamespace = "BookingAgent.Application.Features.Assistant.Tools";

	private static readonly IObjectProvider<IType> Tools =
		Types().That().ResideInNamespace(ToolsNamespace).As("Assistant tools");

	[Fact]
	public void No_tool_can_confirm_cancel_or_move_a_booking() =>
		Types().That().Are(Tools)
			.Should().NotDependOnAny(typeof(ConfirmHold))
			.AndShould().NotDependOnAny(typeof(CancelBooking))
			.AndShould().NotDependOnAny(typeof(RescheduleBooking))
			.Check(LayerDependencyRules.Architecture);
}
