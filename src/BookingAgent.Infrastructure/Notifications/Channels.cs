namespace BookingAgent.Infrastructure.Notifications;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Abstractions;
using BookingAgent.Domain.Notifications;
using BookingAgent.Infrastructure.Persistence;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using NodaTime;

/// <summary>How the SMTP connection is secured.</summary>
public enum SmtpSecurity
{
	/// <summary>Plain SMTP: only for a local catcher such as Mailpit.</summary>
	None = 0,

	/// <summary>Upgraded with STARTTLS (port 587).</summary>
	StartTls = 1,

	/// <summary>TLS from the first byte (port 465).</summary>
	SslOnConnect = 2,
}

/// <summary>The SMTP server, bound from the <c>Smtp</c> section. The AppHost points it at Mailpit.</summary>
public sealed class SmtpOptions
{
	/// <summary>The configuration section.</summary>
	public const string SectionName = "Smtp";

	/// <summary>The server. Without one, emails fail and are retried until one is configured.</summary>
	public string? Host { get; set; }

	/// <summary>The port.</summary>
	[Range(1, 65535)]
	public int Port { get; set; } = 587;

	/// <summary>How the connection is secured; the AppHost sets <see cref="SmtpSecurity.None"/> for Mailpit.</summary>
	public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

	/// <summary>The user, when the server wants one.</summary>
	public string? UserName { get; set; }

	/// <summary>The password, from user secrets or the environment.</summary>
	public string? Password { get; set; }

	/// <summary>The sender address.</summary>
	[EmailAddress]
	public string FromAddress { get; set; } = "citas@clinica-demo.local";

	/// <summary>The sender name customers see.</summary>
	public string FromName { get; set; } = "Clínica Demo";

	/// <summary>How long one send may take.</summary>
	[Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Email through MailKit, one connection per message: notifications are few, and a pooled connection would only
/// add a way to fail (servers drop idle ones). The Message-ID is the notification's id, so a message delivered
/// twice after a crash can be recognised as the same one.
/// </summary>
/// <param name="options">The server.</param>
internal sealed class SmtpEmailChannel(IOptions<SmtpOptions> options) : INotificationChannel
{
	private const string MessageIdDomain = "booking-agent";

	public NotificationChannel Channel => NotificationChannel.Email;

	public async Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(message);
		var settings = options.Value;
		if (string.IsNullOrWhiteSpace(settings.Host))
		{
			throw new InvalidOperationException("No SMTP server is configured (Smtp:Host).");
		}

		using var email = new MimeMessage();
		email.MessageId = $"{message.NotificationId}@{MessageIdDomain}";
		email.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
		email.To.Add(new MailboxAddress(message.ToName, message.ToEmail));
		email.Subject = message.Subject;
		email.Body = new TextPart(MimeKit.Text.TextFormat.Plain) { Text = message.Body };

		using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
		await client.ConnectAsync(settings.Host, settings.Port, Map(settings.Security), cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrEmpty(settings.UserName))
		{
			await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
		}

		await client.SendAsync(email, cancellationToken).ConfigureAwait(false);
		await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
	}

	private static SecureSocketOptions Map(SmtpSecurity security) => security switch
	{
		SmtpSecurity.None => SecureSocketOptions.None,
		SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
		_ => SecureSocketOptions.StartTls,
	};
}

/// <summary>
/// The in-app inbox. The message is staged in the dispatcher's unit of work and saved with the notification's
/// outcome, in one transaction: on this channel delivery is exactly once.
/// </summary>
/// <param name="db">The database, shared with the outbox.</param>
/// <param name="clock">The clock.</param>
internal sealed class InAppChannel(BookingDbContext db, IClock clock) : INotificationChannel
{
	public NotificationChannel Channel => NotificationChannel.InApp;

	public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(message);
		db.Inbox.Add(InboxMessage.Deliver(message.NotificationId, message.CustomerId, message.BookingId, message.Subject, message.Body, clock.GetCurrentInstant()));
		return Task.CompletedTask;
	}
}
