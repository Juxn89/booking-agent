namespace BookingAgent.Architecture.Tests;

using System.Reflection;

/// <summary>
/// The inner layers carry no framework dependencies, checked on the references the compiler actually emitted.
/// Reflection, not ArchUnitNET: a namespace rule against an assembly that is not loaded resolves to zero types
/// and passes no matter what (found in talent-mcp by injecting EF Core into the domain).
/// </summary>
public sealed class ForbiddenAssemblyReferences
{
	// The one package the domain may use: time is what this domain is about (AGENTS.md).
	private const string NodaTime = "NodaTime";

	private static readonly string[] Adapters =
	[
		"Microsoft.EntityFrameworkCore",
		"Npgsql",
		"Microsoft.AspNetCore",
		"Anthropic",
	];

	private static readonly string[] Platform = ["System", "Microsoft.CSharp", "netstandard", "mscorlib"];

	[Fact]
	public void Domain_references_the_platform_and_NodaTime_only()
	{
		var offending = Layers.Domain.GetReferencedAssemblies()
			.Select(static reference => reference.Name ?? string.Empty)
			.Where(static name => name != NodaTime && !Platform.Any(prefix => IsPrefixMatch(name, prefix)))
			.ToArray();

		Assert.True(
			offending.Length == 0,
			$"The domain must not reference [{string.Join(", ", offending)}]: availability and booking rules run without a database, a model or a host.");
	}

	[Fact]
	public void Application_references_only_ports()
	{
		var offending = Layers.Application.GetReferencedAssemblies()
			.Select(static reference => reference.Name ?? string.Empty)
			.Where(static name => Adapters.Any(prefix => IsPrefixMatch(name, prefix)))
			.ToArray();

		Assert.True(
			offending.Length == 0,
			$"The application must not reference [{string.Join(", ", offending)}]: use cases are written against ports, so they are tested with fakes.");
	}

	/// <summary>"Microsoft.Extensions.AI" must not match "Microsoft.Extensions.AI.Abstractions".</summary>
	private static bool IsPrefixMatch(string name, string prefix) =>
		name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
		|| (name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
			&& !name.EndsWith(".Abstractions", StringComparison.OrdinalIgnoreCase));
}
