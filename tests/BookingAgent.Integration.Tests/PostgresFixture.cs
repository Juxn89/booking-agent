namespace BookingAgent.Integration.Tests;

using BookingAgent.Infrastructure;
using BookingAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NodaTime.Testing;
using Testcontainers.PostgreSql;

/// <summary>
/// One real Postgres per test run, migrated once and seeded with the demo clinic, plus the API on top of it. Same
/// image as the AppHost, so the exclusion constraint under test is the one the app runs with.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	public const string Image = "postgres:18.6-alpine";

	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

	private WebApplicationFactory<Program>? _api;

	public string ConnectionString => _container.GetConnectionString();

	public WebApplicationFactory<Program> Api => _api ?? throw new InvalidOperationException("Not initialised.");

	public async Task InitializeAsync()
	{
		await _container.StartAsync();
		await using (var db = CreateContext())
		{
			await db.Database.MigrateAsync();
		}

		_api = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
			.UseEnvironment("Testing")
			.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", ConnectionString)
			// Tests that need expiry drive ExpireHolds themselves, with a fake clock.
			.UseSetting("Sweeper:Enabled", "false"));
		await DemoData.SeedAsync(_api.Services);
	}

	public async Task DisposeAsync()
	{
		if (_api is not null)
		{
			await _api.DisposeAsync();
		}

		await _container.DisposeAsync();
	}

	public BookingDbContext CreateContext()
	{
		var options = new DbContextOptionsBuilder<BookingDbContext>();
		PersistenceSetup.Configure(options, ConnectionString);
		return new BookingDbContext(options.Options);
	}

	/// <summary>The use cases on the real database, with a clock the test moves.</summary>
	/// <param name="clock">The clock.</param>
	/// <returns>The services; each scope is one request's unit of work.</returns>
	public ServiceProvider Services(FakeClock clock)
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[$"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}"] = ConnectionString,
				["Sweeper:Enabled"] = "false",
			})
			.Build();
		var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
		services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
		return services.BuildServiceProvider();
	}

	/// <summary>Empties the bookings and customers; the catalog stays.</summary>
	/// <returns>A task.</returns>
	public async Task ResetAsync()
	{
		await using var db = CreateContext();
		await db.Database.ExecuteSqlRawAsync("TRUNCATE bookings, customers");
	}
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
	public const string Name = "postgres";
}
