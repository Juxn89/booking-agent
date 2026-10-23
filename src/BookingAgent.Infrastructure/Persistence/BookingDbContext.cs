namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The database: the catalog (businesses, services, professionals and their hours), the bookings, the outbox of
/// notifications and the in-app inbox.
/// </summary>
/// <param name="options">The options.</param>
public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
	/// <summary>The extension the exclusion constraint needs, to combine <c>=</c> on a uuid with <c>&amp;&amp;</c> on a range.</summary>
	internal const string BtreeGist = "btree_gist";

	/// <summary>Businesses.</summary>
	public DbSet<Business> Businesses => Set<Business>();

	/// <summary>Services.</summary>
	public DbSet<Service> Services => Set<Service>();

	/// <summary>Professionals.</summary>
	public DbSet<Professional> Professionals => Set<Professional>();

	/// <summary>Time off.</summary>
	public DbSet<TimeOff> TimeOff => Set<TimeOff>();

	/// <summary>Customers.</summary>
	public DbSet<Customer> Customers => Set<Customer>();

	/// <summary>Bookings.</summary>
	public DbSet<Booking> Bookings => Set<Booking>();

	/// <summary>The outbox: notifications waiting to be sent, and what became of the others.</summary>
	public DbSet<Notification> Notifications => Set<Notification>();

	/// <summary>The in-app inbox.</summary>
	public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

	/// <inheritdoc />
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);
		modelBuilder.HasPostgresExtension(BtreeGist);
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookingDbContext).Assembly);
	}

	/// <inheritdoc />
	protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		ArgumentNullException.ThrowIfNull(configurationBuilder);
		configurationBuilder.Properties<BusinessId>().HaveConversion<BusinessIdConverter>();
		configurationBuilder.Properties<ProfessionalId>().HaveConversion<ProfessionalIdConverter>();
		configurationBuilder.Properties<ServiceId>().HaveConversion<ServiceIdConverter>();
		configurationBuilder.Properties<CustomerId>().HaveConversion<CustomerIdConverter>();
		configurationBuilder.Properties<BookingId>().HaveConversion<BookingIdConverter>();
	}
}

/// <summary>How the context connects: Npgsql with its NodaTime plugin (Instant → timestamptz, Interval → tstzrange).</summary>
internal static class PersistenceSetup
{
	public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
		options
			.UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
			.UseSnakeCaseNamingConvention();
}

/// <summary>Applies the migrations; run at startup in Development and by the tests.</summary>
public static class DatabaseMigrator
{
	/// <summary>Migrates the database to the latest schema.</summary>
	/// <param name="services">The app's services.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
	{
		await using var scope = services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
		await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>For <c>dotnet ef</c>: a context without the app's configuration.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
	private const string PlaceholderConnectionString = "Host=localhost;Database=booking_agent_design";

	public BookingDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<BookingDbContext>();
		PersistenceSetup.Configure(options, PlaceholderConnectionString);
		return new BookingDbContext(options.Options);
	}
}

internal sealed class BusinessIdConverter() : ValueConverter<BusinessId, Guid>(id => id.Value, value => new BusinessId(value));

internal sealed class ProfessionalIdConverter() : ValueConverter<ProfessionalId, Guid>(id => id.Value, value => new ProfessionalId(value));

internal sealed class ServiceIdConverter() : ValueConverter<ServiceId, Guid>(id => id.Value, value => new ServiceId(value));

internal sealed class CustomerIdConverter() : ValueConverter<CustomerId, Guid>(id => id.Value, value => new CustomerId(value));

internal sealed class BookingIdConverter() : ValueConverter<BookingId, Guid>(id => id.Value, value => new BookingId(value));
