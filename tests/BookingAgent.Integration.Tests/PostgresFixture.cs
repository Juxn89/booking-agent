namespace BookingAgent.Integration.Tests;

using BookingAgent.Infrastructure;
using BookingAgent.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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
/// One real Postgres per test run, migrated once and seeded with the demo clinic, plus the API on top of it, and a
/// Mailpit for the emails. Same images as the AppHost, so the exclusion constraint under test is the one the app
/// runs with, and the emails go through a real SMTP conversation.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	public const string Image = "postgres:18.6-alpine";

	public const string MailpitImage = "axllent/mailpit:v1.31.2";

	private const int SmtpPort = 1025;

	private const int MailpitHttpPort = 8025;

	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

	private readonly IContainer _mailpit = new ContainerBuilder(MailpitImage)
		.WithPortBinding(SmtpPort, assignRandomHostPort: true)
		.WithPortBinding(MailpitHttpPort, assignRandomHostPort: true)
		.WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(MailpitHttpPort).ForPath("/livez")))
		.Build();

	private WebApplicationFactory<Program>? _api;

	public string ConnectionString => _container.GetConnectionString();

	public WebApplicationFactory<Program> Api => _api ?? throw new InvalidOperationException("Not initialised.");

	/// <summary>Mailpit's HTTP API, to read what was sent.</summary>
	public Uri MailpitApi => new($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(MailpitHttpPort)}/");

	public async Task InitializeAsync()
	{
		await Task.WhenAll(_container.StartAsync(), _mailpit.StartAsync());
		await using (var db = CreateContext())
		{
			await db.Database.MigrateAsync();
		}

		_api = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
			.UseEnvironment("Testing")
			.UseSetting($"ConnectionStrings:{DependencyInjection.DatabaseConnectionName}", ConnectionString)
			// Tests that need expiry or notifications drive ExpireHolds and DispatchNotifications themselves.
			.UseSetting("Sweeper:Enabled", "false")
			.UseSetting("Notifications:DispatcherEnabled", "false"));
		await DemoData.SeedAsync(_api.Services);
	}

	public async Task DisposeAsync()
	{
		if (_api is not null)
		{
			await _api.DisposeAsync();
		}

		await _container.DisposeAsync();
		await _mailpit.DisposeAsync();
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
				["Notifications:DispatcherEnabled"] = "false",
				["Smtp:Host"] = _mailpit.Hostname,
				["Smtp:Port"] = _mailpit.GetMappedPublicPort(SmtpPort).ToString(System.Globalization.CultureInfo.InvariantCulture),
				["Smtp:Security"] = "None",
			})
			.Build();
		var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
		services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
		return services.BuildServiceProvider();
	}

	/// <summary>Empties the bookings, customers, notifications and Mailpit's inbox; the catalog stays.</summary>
	/// <returns>A task.</returns>
	public async Task ResetAsync()
	{
		await using (var db = CreateContext())
		{
			await db.Database.ExecuteSqlRawAsync("TRUNCATE inbox_messages, notifications, bookings, customers");
		}

		using var mailpit = new HttpClient { BaseAddress = MailpitApi };
		(await mailpit.DeleteAsync(new Uri("api/v1/messages", UriKind.Relative))).EnsureSuccessStatusCode();
	}
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
	public const string Name = "postgres";
}
