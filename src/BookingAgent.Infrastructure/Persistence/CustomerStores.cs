namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Application.Features.Customers;
using BookingAgent.Domain;
using BookingAgent.Domain.Bookings;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

/// <summary>Sign-in codes, sessions and the customers they belong to.</summary>
/// <param name="db">The database.</param>
internal sealed class SignInStore(BookingDbContext db) : ISignInStore
{
	public Task<string?> BusinessNameAsync(BusinessId business, CancellationToken cancellationToken) =>
		db.Businesses.AsNoTracking().Where(b => b.Id == business).Select(static b => b.Name).FirstOrDefaultAsync(cancellationToken);

	public Task<Customer?> FindCustomerAsync(BusinessId business, string normalizedEmail, CancellationToken cancellationToken) =>
		db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.BusinessId == business && c.Email == normalizedEmail, cancellationToken);

	public Task<Customer?> CustomerAsync(CustomerId customer, CancellationToken cancellationToken) =>
		db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customer, cancellationToken);

	public Task<SignInCode?> LatestCodeAsync(CustomerId customer, CancellationToken cancellationToken) =>
		db.SignInCodes.AsTracking().Where(c => c.CustomerId == customer).OrderByDescending(static c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);

	public Task<CustomerSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken) =>
		db.CustomerSessions.AsTracking().FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

	public void Add(SignInCode code) => db.SignInCodes.Add(code);

	public void Add(CustomerSession session) => db.CustomerSessions.Add(session);

	public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>A customer's upcoming bookings, with the names to show them by.</summary>
/// <param name="db">The database.</param>
internal sealed class MyBookingsReader(BookingDbContext db) : IMyBookingsReader
{
	public async Task<IReadOnlyList<CustomerBooking>> UpcomingAsync(CustomerId customer, Instant now, CancellationToken cancellationToken)
	{
		var rows = await (
				from booking in db.Bookings.AsNoTracking()
				where booking.CustomerId == customer && booking.Status == BookingStatus.Confirmed && booking.Period.Start > now
				join service in db.Services on booking.ServiceId equals service.Id
				join professional in db.Professionals on booking.ProfessionalId equals professional.Id
				join business in db.Businesses on professional.BusinessId equals business.Id
				orderby booking.Period.Start
				select new { booking, Service = service.Name, Professional = professional.Name, business.TimeZoneId })
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		return [.. rows.Select(static r => new CustomerBooking(r.booking, r.Service, r.Professional, DateTimeZoneProviders.Tzdb[r.TimeZoneId]))];
	}
}

internal sealed class SignInCodeConfiguration : IEntityTypeConfiguration<SignInCode>
{
	public void Configure(EntityTypeBuilder<SignInCode> builder)
	{
		builder.ToTable("sign_in_codes");
		builder.HasKey(c => c.Id);
		builder.Property(c => c.Id).ValueGeneratedNever();
		builder.HasOne<Business>().WithMany().HasForeignKey(c => c.BusinessId);
		builder.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId);
		builder.Property(c => c.CodeHash).HasMaxLength(Columns.TokenHash).IsFixedLength();

		// The latest code of a customer.
		builder.HasIndex(c => new { c.CustomerId, c.CreatedAt });
	}
}

internal sealed class CustomerSessionConfiguration : IEntityTypeConfiguration<CustomerSession>
{
	public void Configure(EntityTypeBuilder<CustomerSession> builder)
	{
		builder.ToTable("customer_sessions");
		builder.HasKey(s => s.Id);
		builder.Property(s => s.Id).ValueGeneratedNever();
		builder.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId);
		builder.HasOne<Customer>().WithMany().HasForeignKey(s => s.CustomerId);
		builder.Property(s => s.TokenHash).HasMaxLength(Columns.TokenHash).IsFixedLength();
		builder.HasIndex(s => s.TokenHash).IsUnique();
	}
}
