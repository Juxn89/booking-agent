namespace BookingAgent.Infrastructure.Notifications;

using System.ComponentModel.DataAnnotations;
using BookingAgent.Application.Abstractions;
using BookingAgent.Application.Features.Customers;
using BookingAgent.Domain.Catalog;
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
/// Email through MailKit, one connection per message: notifications and codes are few, and a pooled connection would
/// only add a way to fail (servers drop idle ones).
/// </summary>
/// <param name="options">The server.</param>
internal sealed class SmtpMailer(IOptions<SmtpOptions> options)
{
	/// <summary>Sends a plain-text email.</summary>
	/// <param name="toName">The recipient's name.</param>
	/// <param name="toEmail">The recipient's address.</param>
	/// <param name="subject">The subject.</param>
	/// <param name="body">The text.</param>
	/// <param name="messageId">A Message-ID that recognises the same message sent twice, or none.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	public async Task SendAsync(string toName, string toEmail, string subject, string body, string? messageId, CancellationToken cancellationToken)
	{
		var settings = options.Value;
		if (string.IsNullOrWhiteSpace(settings.Host))
		{
			throw new InvalidOperationException("No SMTP server is configured (Smtp:Host).");
		}

		using var email = new MimeMessage();
		if (messageId is not null)
		{
			email.MessageId = messageId;
		}

		email.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
		email.To.Add(new MailboxAddress(toName, toEmail));
		email.Subject = subject;
		email.Body = new TextPart(MimeKit.Text.TextFormat.Plain) { Text = body };

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
/// The email channel. The Message-ID is the notification's id, so a message delivered twice after a crash can be
/// recognised as the same one.
/// </summary>
/// <param name="mailer">The SMTP server.</param>
internal sealed class SmtpEmailChannel(SmtpMailer mailer) : INotificationChannel
{
	private const string MessageIdDomain = "booking-agent";

	public NotificationChannel Channel => NotificationChannel.Email;

	public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(message);
		return mailer.SendAsync(message.ToName, message.ToEmail, message.Subject, message.Body, $"{message.NotificationId}@{MessageIdDomain}", cancellationToken);
	}
}

/// <summary>Emails sign-in codes, at once: the customer is waiting for it, so it does not go through the outbox.</summary>
/// <param name="mailer">The SMTP server.</param>
internal sealed class SmtpSignInCodeSender(SmtpMailer mailer) : ISignInCodeSender
{
	public Task SendAsync(Customer customer, string businessName, string digits, Duration lifetime, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(customer);
		var minutes = (int)lifetime.TotalMinutes;
		return mailer.SendAsync(
			customer.Name,
			customer.Email,
			$"Tu código para {businessName}: {digits}",
			$"Hola, {customer.Name}: tu código para ver y cambiar tus citas en {businessName} es {digits}. Vence en {minutes} minutos.\n\n" +
			"Si no lo pediste, ignora este mensaje: nadie puede entrar sin él.",
			messageId: null,
			cancellationToken);
	}
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
