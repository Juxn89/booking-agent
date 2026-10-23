namespace BookingAgent.Application.Features.Notifications;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Abstractions;
using BookingAgent.Domain.Notifications;
using Microsoft.Extensions.Options;
using NodaTime;

/// <summary>How the outbox is drained, bound from the <c>Notifications</c> section.</summary>
public sealed class NotificationOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Notifications";

	/// <summary>Turns the dispatcher off, for tests that drive <see cref="DispatchNotifications"/> themselves.</summary>
	public bool DispatcherEnabled { get; set; } = true;

	/// <summary>Between passes when the outbox was empty: a confirmation reaches the inbox within this.</summary>
	[Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>The most notifications taken in one pass.</summary>
	[Range(1, 500)]
	public int BatchSize { get; set; } = 20;

	/// <summary>
	/// How long a claimed notification is one dispatcher's. Longer than a pass can take (a batch of SMTP sends with
	/// their timeouts), so a slow pass is never sent twice; a crashed one is retried when it runs out.
	/// </summary>
	[Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
	public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>After how many failed attempts a notification is given up on and left for a person.</summary>
	[Range(1, 20)]
	public int MaxAttempts { get; set; } = 6;

	/// <summary>The wait before the first retry; each next one waits twice as long (30 s, 1, 2, 4, 8 min).</summary>
	[Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
	public TimeSpan FirstRetry { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>What one pass did.</summary>
/// <param name="Sent">Delivered.</param>
/// <param name="Skipped">No longer worth sending.</param>
/// <param name="Retrying">Failed, to be tried again.</param>
/// <param name="Failed">Failed for the last time.</param>
public sealed record DispatchReport(int Sent, int Skipped, int Retrying, int Failed)
{
	/// <summary>How many notifications the pass handled.</summary>
	public int Total => Sent + Skipped + Retrying + Failed;
}

/// <summary>
/// Sends the notifications that are due, each on its channel, and records each outcome as soon as it is known:
/// one failing email does not hold back the rest, and a failure is retried later with a growing wait.
/// </summary>
/// <param name="outbox">The outbox.</param>
/// <param name="channels">The channels.</param>
/// <param name="clock">The clock.</param>
/// <param name="options">Batch, lease and retries.</param>
public sealed class DispatchNotifications(INotificationOutbox outbox, IEnumerable<INotificationChannel> channels, IClock clock, IOptions<NotificationOptions> options)
{
	/// <summary>Runs one pass.</summary>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>What it did.</returns>
	public async Task<DispatchReport> HandleAsync(CancellationToken cancellationToken)
	{
		var settings = options.Value;
		var due = await outbox.ClaimDueAsync(clock.GetCurrentInstant(), settings.BatchSize, Duration.FromTimeSpan(settings.Lease), cancellationToken).ConfigureAwait(false);

		int sent = 0, skipped = 0, retrying = 0, failed = 0;
		foreach (var (notification, subject) in due)
		{
			if (NotificationTemplates.WhyNotSend(notification, subject, clock.GetCurrentInstant()) is { } reason)
			{
				notification.Skip(reason);
				skipped++;
			}
			else
			{
				try
				{
					var channel = channels.FirstOrDefault(c => c.Channel == notification.Channel)
						?? throw new InvalidOperationException($"No {notification.Channel} channel is registered.");
					await channel.SendAsync(NotificationTemplates.Render(notification, subject), cancellationToken).ConfigureAwait(false);
					notification.MarkSent(clock.GetCurrentInstant());
					sent++;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					// Shutting down: the lease runs out and another pass sends it.
					throw;
				}
#pragma warning disable CA1031 // Any failure of a channel (SMTP down, bad address) is recorded on the row and retried.
				catch (Exception exception)
#pragma warning restore CA1031
				{
					if (notification.Fail($"{exception.GetType().Name}: {exception.Message}", clock.GetCurrentInstant(), settings.MaxAttempts, Duration.FromTimeSpan(settings.FirstRetry)))
					{
						retrying++;
					}
					else
					{
						failed++;
					}
				}
			}

			await outbox.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}

		return new DispatchReport(sent, skipped, retrying, failed);
	}
}
