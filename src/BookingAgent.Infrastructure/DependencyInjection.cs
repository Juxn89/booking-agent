namespace BookingAgent.Infrastructure;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Infrastructure.Notifications;
using BookingAgent.Infrastructure.Persistence;
using BookingAgent.Infrastructure.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

/// <summary>Wires the adapters and the use cases.</summary>
public static class DependencyInjection
{
	/// <summary>Connection string name. Aspire injects it as <c>ConnectionStrings__bookingsdb</c>.</summary>
	public const string DatabaseConnectionName = "bookingsdb";

	/// <summary>Registers everything the API needs.</summary>
	/// <param name="services">The services.</param>
	/// <param name="configuration">The configuration.</param>
	/// <returns>The services.</returns>
	/// <exception cref="InvalidOperationException">The connection string is missing.</exception>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
			?? throw new InvalidOperationException(
				$"Missing connection string '{DatabaseConnectionName}'. Run through the AppHost, or set ConnectionStrings__{DatabaseConnectionName}.");

		services.AddSingleton<IClock>(SystemClock.Instance);
		services.AddDbContext<BookingDbContext>(options => PersistenceSetup.Configure(options, connectionString));

		AddOptions<BookingOptions>(services, configuration, BookingOptions.SectionName);
		AddOptions<SweeperOptions>(services, configuration, SweeperOptions.SectionName);
		AddOptions<NotificationOptions>(services, configuration, NotificationOptions.SectionName);
		AddOptions<SmtpOptions>(services, configuration, SmtpOptions.SectionName);

		services.AddScoped<IAvailabilitySource, AvailabilitySource>();
		services.AddScoped<IBookingRepository, BookingRepository>();
		services.AddScoped<ICatalogReader, CatalogReader>();
		services.AddScoped<INotificationOutbox, NotificationOutbox>();
		services.AddScoped<IInboxReader, InboxReader>();
		services.AddScoped<INotificationChannel, SmtpEmailChannel>();
		services.AddScoped<INotificationChannel, InAppChannel>();

		services.AddScoped<FindAvailableSlots>();
		services.AddScoped<HoldSlot>();
		services.AddScoped<ConfirmHold>();
		services.AddScoped<CancelBooking>();
		services.AddScoped<RescheduleBooking>();
		services.AddScoped<ExpireHolds>();
		services.AddScoped<GetCatalog>();
		services.AddScoped<ReadInbox>();
		services.AddScoped<DispatchNotifications>();

		services.AddHostedService<HoldSweeper>();
		services.AddHostedService<NotificationDispatcher>();
		return services;
	}

	/// <summary>Bound, validated with data annotations, and checked at startup rather than on first use.</summary>
	private static void AddOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
		where TOptions : class =>
		services.AddOptions<TOptions>()
			.Bind(configuration.GetSection(section))
			.ValidateDataAnnotations()
			.ValidateOnStart();
}
