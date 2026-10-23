namespace BookingAgent.Infrastructure;

using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Features.Assistant;
using BookingAgent.Application.Features.Assistant.Tools;
using BookingAgent.Application.Features.Availability;
using BookingAgent.Application.Features.Bookings;
using BookingAgent.Application.Features.Catalog;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Application.Features.Notifications;
using BookingAgent.Application.Features.Panel;
using BookingAgent.Infrastructure.Ai;
using BookingAgent.Infrastructure.Notifications;
using BookingAgent.Infrastructure.Persistence;
using BookingAgent.Infrastructure.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
		AddOptions<AssistantOptions>(services, configuration, AssistantOptions.SectionName);
		AddOptions<AnthropicOptions>(services, configuration, AnthropicOptions.SectionName);
		AddOptions<SignInOptions>(services, configuration, SignInOptions.SectionName);

		services.AddScoped<IAvailabilitySource, AvailabilitySource>();
		services.AddScoped<IBookingRepository, BookingRepository>();
		services.AddScoped<ICatalogReader, CatalogReader>();
		services.AddScoped<INotificationOutbox, NotificationOutbox>();
		services.AddScoped<IInboxReader, InboxReader>();
		services.AddScoped<SmtpMailer>();
		services.AddScoped<INotificationChannel, SmtpEmailChannel>();
		services.AddScoped<ISignInCodeSender, SmtpSignInCodeSender>();
		services.AddScoped<ISignInStore, SignInStore>();
		services.AddScoped<IMyBookingsReader, MyBookingsReader>();
		services.AddScoped<IPanelReader, PanelReader>();

		// The API replaces it with the SignalR hub; other hosts announce nothing.
		services.TryAddSingleton<ICalendarUpdates, NoCalendarUpdates>();
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
		services.AddScoped<RequestSignInCode>();
		services.AddScoped<VerifySignInCode>();
		services.AddScoped<CustomerSessions>();
		services.AddScoped<ListMyBookings>();
		services.AddScoped<GetPanelCalendar>();
		services.AddScoped<GetBookingTrace>();

		// The assistant: the bare model once per app (keyed, so tests can swap it), the tool loop and the meter per
		// request around the request's turn.
		services.AddKeyedSingleton<IChatClient>(
			AssistantPipeline.ModelKey,
			static (provider, _) => AssistantPipeline.CreateModel(
				provider.GetRequiredService<IOptions<AnthropicOptions>>().Value,
				provider.GetRequiredService<IOptions<AssistantOptions>>().Value));
		services.AddScoped<AgentTurn>();
		services.AddScoped<IChatClient>(static provider => AssistantPipeline.Build(
			provider.GetRequiredKeyedService<IChatClient>(AssistantPipeline.ModelKey),
			provider.GetRequiredService<AgentTurn>(),
			provider.GetRequiredService<IOptions<AssistantOptions>>().Value,
			provider.GetRequiredService<IClock>()));
		services.AddScoped<IConversationStore, ConversationStore>();
		services.AddScoped<ListServicesTool>();
		services.AddScoped<ListProfessionalsTool>();
		services.AddScoped<ResolveDateTool>();
		services.AddScoped<CheckAvailabilityTool>();
		services.AddScoped<HoldSlotTool>();
		services.AddScoped<FindMyBookingsTool>();
		services.AddScoped<ProposeCancelTool>();
		services.AddScoped<ProposeRescheduleTool>();
		services.AddScoped<BookingTools>();
		services.AddScoped<AgentRunner>();

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
