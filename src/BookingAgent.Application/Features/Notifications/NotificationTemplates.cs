namespace BookingAgent.Application.Features.Notifications;

using System.Globalization;
using BookingAgent.Application.Abstractions;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Notifications;
using NodaTime;

/// <summary>
/// What the notifications say, in Spanish: the demo business is in Managua. Day and month names are spelled out
/// here rather than taken from the operating system's culture data, which a container may not carry.
/// </summary>
public static class NotificationTemplates
{
	private static readonly string[] Days = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

	private static readonly string[] Months =
		["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

	/// <summary>Why a due notification should not go out any more, or <see langword="null"/> when it should.</summary>
	/// <param name="notification">The notification.</param>
	/// <param name="subject">The booking as it is now.</param>
	/// <param name="now">The current instant.</param>
	/// <returns>The reason, for the outbox row.</returns>
	public static string? WhyNotSend(Notification notification, NotificationSubject subject, Instant now)
	{
		ArgumentNullException.ThrowIfNull(notification);
		ArgumentNullException.ThrowIfNull(subject);

		// Notices tell what happened, and it did happen. Reminders are about an appointment that must still be on.
		if (notification.Kind != NotificationKind.Reminder)
		{
			return null;
		}

		if (subject.Status != BookingStatus.Confirmed)
		{
			return "The booking is no longer confirmed.";
		}

		return now >= subject.Period.Start ? "The appointment has already started." : null;
	}

	/// <summary>Renders a notification.</summary>
	/// <param name="notification">The notification.</param>
	/// <param name="subject">The booking.</param>
	/// <returns>The message.</returns>
	public static OutgoingMessage Render(Notification notification, NotificationSubject subject)
	{
		ArgumentNullException.ThrowIfNull(notification);
		ArgumentNullException.ThrowIfNull(subject);

		var when = When(subject.Period.Start.InZone(subject.Zone).LocalDateTime);
		var where = $"(hora de {City(subject.Zone)})";
		var (title, sentence) = notification.Kind switch
		{
			NotificationKind.Confirmed => ("Cita confirmada", $"tu cita de {subject.ServiceName} con {subject.ProfessionalName} quedó confirmada para el {when} {where}."),
			NotificationKind.Rescheduled => ("Cita cambiada", $"tu cita de {subject.ServiceName} con {subject.ProfessionalName} ahora es el {when} {where}. La hora anterior quedó libre."),
			NotificationKind.Cancelled => ("Cita cancelada", $"tu cita de {subject.ServiceName} con {subject.ProfessionalName} del {when} {where} quedó cancelada."),
			NotificationKind.Reminder => ("Recordatorio", $"te recordamos tu cita de {subject.ServiceName} con {subject.ProfessionalName} el {when} {where}."),
			_ => throw new ArgumentOutOfRangeException(nameof(notification), notification.Kind, "Unknown kind."),
		};

		var body = $"Hola, {subject.CustomerName}: {sentence}\n\n{subject.BusinessName}";
		return new OutgoingMessage(
			notification.Id,
			subject.CustomerId,
			notification.BookingId,
			subject.CustomerName,
			subject.CustomerEmail,
			$"{title}: {subject.ServiceName}, {when}",
			body);
	}

	/// <summary>"lunes 2 de noviembre a las 9:00".</summary>
	/// <param name="local">A local date and time.</param>
	/// <returns>The text.</returns>
	public static string When(LocalDateTime local) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"{Days[(int)local.DayOfWeek - 1]} {local.Day} de {Months[local.Month - 1]} a las {local.Hour}:{local.Minute:00}");

	// "America/Managua" → "Managua", "America/El_Salvador" → "El Salvador".
	private static string City(DateTimeZone zone) => zone.Id[(zone.Id.LastIndexOf('/') + 1)..].Replace('_', ' ');
}
