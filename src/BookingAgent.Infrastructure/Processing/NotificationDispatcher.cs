namespace BookingAgent.Infrastructure.Processing;

using BookingAgent.Application.Features.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Drains the outbox: a pass every <see cref="NotificationOptions.PollInterval"/>, or straight away after a full
/// batch, since more are probably waiting.
/// </summary>
/// <param name="scopes">A fresh unit of work per pass.</param>
/// <param name="options">The interval and batch size.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class NotificationDispatcher(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<NotificationDispatcher> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var settings = options.Value;
		if (!settings.DispatcherEnabled)
		{
			return;
		}

		using var timer = new PeriodicTimer(settings.PollInterval);
		do
		{
			DispatchReport report;
			do
			{
				try
				{
					var scope = scopes.CreateAsyncScope();
					await using (scope.ConfigureAwait(false))
					{
						report = await scope.ServiceProvider.GetRequiredService<DispatchNotifications>().HandleAsync(stoppingToken).ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
#pragma warning disable CA1031 // The dispatcher must outlive a database outage; it logs and tries again next pass.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					LogFailed(exception);
					break;
				}

				if (report.Total > 0)
				{
					LogPass(report.Sent, report.Skipped, report.Retrying, report.Failed);
				}
			}
			while (report.Total == settings.BatchSize && !stoppingToken.IsCancellationRequested);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Notifications: {Sent} sent, {Skipped} skipped, {Retrying} to retry, {Failed} given up.")]
	private partial void LogPass(int sent, int skipped, int retrying, int failed);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Dispatching notifications failed; trying again next pass.")]
	private partial void LogFailed(Exception exception);
}
