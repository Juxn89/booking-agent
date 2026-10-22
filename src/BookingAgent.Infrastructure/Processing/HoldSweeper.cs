namespace BookingAgent.Infrastructure.Processing;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Features.Bookings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>How often holds that ran out are released, bound from the <c>Sweeper</c> section.</summary>
public sealed class SweeperOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Sweeper";

	/// <summary>Turns the sweeper off, for tests that drive <see cref="ExpireHolds"/> themselves.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Between passes. Correctness does not depend on it (a hold that ran out stops counting at once); the sweep
	/// only keeps the calendar and the panel tidy.
	/// </summary>
	[Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
	public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Releases holds nobody confirmed in time, every <see cref="SweeperOptions.Interval"/>.</summary>
/// <param name="scopes">A fresh unit of work per pass.</param>
/// <param name="options">The interval.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class HoldSweeper(IServiceScopeFactory scopes, IOptions<SweeperOptions> options, ILogger<HoldSweeper> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!options.Value.Enabled)
		{
			return;
		}

		using var timer = new PeriodicTimer(options.Value.Interval);
		do
		{
			try
			{
				var scope = scopes.CreateAsyncScope();
				await using (scope.ConfigureAwait(false))
				{
					var released = await scope.ServiceProvider.GetRequiredService<ExpireHolds>().HandleAsync(stoppingToken).ConfigureAwait(false);
					if (released > 0)
					{
						LogReleased(released);
					}
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
#pragma warning disable CA1031 // The sweeper must outlive a database outage; it logs and tries again next pass.
			catch (Exception exception)
#pragma warning restore CA1031
			{
				LogFailed(exception);
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Released {Count} holds that ran out.")]
	private partial void LogReleased(int count);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Releasing expired holds failed; trying again next pass.")]
	private partial void LogFailed(Exception exception);
}
