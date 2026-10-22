namespace BookingAgent.Architecture.Tests;

/// <summary>
/// Every negative rule uses WithoutRequiringPositiveResults(), so an EMPTY layer satisfies all of
/// them. This keeps the suite from going green while asserting nothing.
/// </summary>
public sealed class LayersAreNotEmpty
{
	public static TheoryData<string> LayerNames =>
	[
		Layers.Domain.GetName().Name!,
		Layers.Application.GetName().Name!,
		Layers.Infrastructure.GetName().Name!,
		Layers.Api.GetName().Name!,
	];

	[Theory]
	[MemberData(nameof(LayerNames))]
	public void Layer_IsLoadedAndHasTypes(string assemblyName)
	{
		var types = LayerDependencyRules.Architecture.Types
			.Count(type => type.Assembly.Name.StartsWith(assemblyName + ",", StringComparison.Ordinal)
				|| type.Assembly.Name == assemblyName);

		Assert.True(types > 0, $"{assemblyName} contributed no types to the architecture under test.");
	}

	[Fact]
	public void ForbiddenReferenceCheck_DetectsARealReference()
	{
		// The domain genuinely references NodaTime; if this ever reads as empty, the reflection check has
		// stopped seeing references and the rules above are vacuous.
		var names = Layers.Domain.GetReferencedAssemblies().Select(a => a.Name);

		Assert.Contains("NodaTime", names);
	}
}
