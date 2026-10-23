namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Notifications;
using BookingAgent.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal static class Columns
{
	// Enums are stored by name, so the table reads on its own and reordering an enum cannot corrupt it.
	public const int Enum = 32;

	public const int Name = 200;

	// IANA zone ids top out around 30 characters ("America/Argentina/ComodRivadavia").
	public const int ZoneId = 64;

	// SHA-256, hex.
	public const int TokenHash = 64;

	public const string Money = "numeric(10,2)";

	// Room for a subject line built from a service name and a date.
	public const int Subject = 300;

	// A rendered notification: a few sentences.
	public const int Body = 4000;
}

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
	public void Configure(EntityTypeBuilder<Business> builder)
	{
		builder.HasKey(b => b.Id);
		builder.Property(b => b.Name).HasMaxLength(Columns.Name);
		builder.Property(b => b.TimeZoneId).HasMaxLength(Columns.ZoneId);
		builder.Ignore(b => b.Zone);
		builder.Ignore(b => b.Rules);
	}
}

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
	public void Configure(EntityTypeBuilder<Service> builder)
	{
		builder.HasKey(s => s.Id);
		builder.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId);
		builder.Property(s => s.Name).HasMaxLength(Columns.Name);
		builder.Property(s => s.Price).HasColumnType(Columns.Money);
	}
}

internal sealed class ProfessionalConfiguration : IEntityTypeConfiguration<Professional>
{
	public void Configure(EntityTypeBuilder<Professional> builder)
	{
		builder.HasKey(p => p.Id);
		builder.HasOne<Business>().WithMany().HasForeignKey(p => p.BusinessId);
		builder.Property(p => p.Name).HasMaxLength(Columns.Name);
		builder.Property(p => p.Specialty).HasMaxLength(Columns.Name);
		builder.Ignore(p => p.Schedule);

		// The services they offer, as a uuid[]: read with the professional, never queried on their own.
		builder.PrimitiveCollection(p => p.Services)
			.HasField("_services")
			.UsePropertyAccessMode(PropertyAccessMode.Field)
			.ElementType(element => element.HasConversion<ServiceIdConverter>());

		builder.OwnsMany(p => p.Hours, hours =>
		{
			hours.ToTable("working_hours");
			hours.WithOwner().HasForeignKey("ProfessionalId");
			hours.Property<int>("Id");
			hours.HasKey("Id");
			hours.Property(h => h.Day).HasConversion<int>();
		});
		builder.Navigation(p => p.Hours).HasField("_hours").UsePropertyAccessMode(PropertyAccessMode.Field);
	}
}

internal sealed class TimeOffConfiguration : IEntityTypeConfiguration<TimeOff>
{
	public void Configure(EntityTypeBuilder<TimeOff> builder)
	{
		builder.HasKey(t => t.Id);
		builder.HasOne<Professional>().WithMany().HasForeignKey(t => t.ProfessionalId);
		builder.Property(t => t.Reason).HasMaxLength(Columns.Name);
		builder.HasIndex(t => t.ProfessionalId);
	}
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
	public void Configure(EntityTypeBuilder<Customer> builder)
	{
		builder.HasKey(c => c.Id);
		builder.HasOne<Business>().WithMany().HasForeignKey(c => c.BusinessId);
		builder.Property(c => c.Name).HasMaxLength(Customer.MaxLength);
		builder.Property(c => c.Email).HasMaxLength(Customer.MaxLength);
		builder.Property(c => c.Phone).HasMaxLength(Columns.Name);

		// A customer is found again by email within the business.
		builder.HasIndex(c => new { c.BusinessId, c.Email }).IsUnique();
	}
}

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
	/// <summary>The exclusion constraint created by the migration (raw SQL: EF Core does not model it).</summary>
	public const string NoOverlap = "ex_bookings_no_overlap";

	/// <summary>Postgres' row version, for optimistic concurrency.</summary>
	public const string RowVersion = "xmin";

	public void Configure(EntityTypeBuilder<Booking> builder)
	{
		builder.HasKey(b => b.Id);
		builder.HasOne<Professional>().WithMany().HasForeignKey(b => b.ProfessionalId);
		builder.HasOne<Service>().WithMany().HasForeignKey(b => b.ServiceId);
		builder.HasOne<Customer>().WithMany().HasForeignKey(b => b.CustomerId);
		builder.HasOne<Booking>().WithMany().HasForeignKey(b => b.Replaces);

		builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(b => b.CancellationReason).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(b => b.ConfirmationTokenHash).HasMaxLength(Columns.TokenHash).IsFixedLength();

		// The sweeper looks for holds that ran out.
		builder.HasIndex(b => b.HoldExpiresAt).HasFilter("status = 'Held'").HasDatabaseName("ix_bookings_live_holds");
		builder.HasIndex(b => b.CustomerId);

		// A hold confirmed while the sweeper expires it: one of the two saves must lose.
		builder.Property<uint>(RowVersion).IsRowVersion();

		// Turned into notifications when saved, never stored.
		builder.Ignore(b => b.Changes);
	}
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
	public void Configure(EntityTypeBuilder<Notification> builder)
	{
		builder.HasKey(n => n.Id);
		builder.HasOne<Booking>().WithMany().HasForeignKey(n => n.BookingId);
		builder.Property(n => n.Channel).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(n => n.Detail).HasMaxLength(Notification.MaxDetailLength);

		// The dispatcher's query: pending, due first. Sent and skipped rows stay out of the index.
		builder.HasIndex(n => n.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_notifications_due");
		builder.HasIndex(n => n.BookingId);
	}
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
	public void Configure(EntityTypeBuilder<InboxMessage> builder)
	{
		builder.ToTable("inbox_messages");

		// The notification's id: a notification delivered twice is one message.
		builder.HasKey(m => m.Id);
		builder.Property(m => m.Id).ValueGeneratedNever();
		builder.HasOne<Customer>().WithMany().HasForeignKey(m => m.CustomerId);
		builder.HasOne<Booking>().WithMany().HasForeignKey(m => m.BookingId);
		builder.Property(m => m.Subject).HasMaxLength(Columns.Subject);
		builder.Property(m => m.Body).HasMaxLength(Columns.Body);
		builder.HasIndex(m => new { m.CustomerId, m.ReceivedAt });
	}
}
