namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Domain;
using BookingAgent.Domain.Catalog;
using BookingAgent.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

/// <summary>
/// "Clínica Demo": three professionals and five services in Managua, the business the demo, the evals and the
/// end-to-end tests run against. The ids are fixed, so scenarios can name them.
/// </summary>
public static class DemoData
{
	/// <summary>The clinic.</summary>
	public static readonly BusinessId Clinic = new(Guid.Parse("01920000-0000-7000-8000-000000000001"));

	/// <summary>Dra. Ana López, dentistry.</summary>
	public static readonly ProfessionalId Lopez = new(Guid.Parse("01920000-0000-7000-8000-000000000101"));

	/// <summary>Dr. Carlos Martínez, general medicine.</summary>
	public static readonly ProfessionalId Martinez = new(Guid.Parse("01920000-0000-7000-8000-000000000102"));

	/// <summary>Lic. Sofía Rivera, physiotherapy.</summary>
	public static readonly ProfessionalId Rivera = new(Guid.Parse("01920000-0000-7000-8000-000000000103"));

	/// <summary>General consultation, 30 minutes.</summary>
	public static readonly ServiceId Consultation = new(Guid.Parse("01920000-0000-7000-8000-000000000201"));

	/// <summary>Dental cleaning, 45 minutes.</summary>
	public static readonly ServiceId Cleaning = new(Guid.Parse("01920000-0000-7000-8000-000000000202"));

	/// <summary>Orthodontic check-up, 30 minutes.</summary>
	public static readonly ServiceId Orthodontics = new(Guid.Parse("01920000-0000-7000-8000-000000000203"));

	/// <summary>Physiotherapy session, 60 minutes.</summary>
	public static readonly ServiceId Physiotherapy = new(Guid.Parse("01920000-0000-7000-8000-000000000204"));

	/// <summary>Therapeutic massage, 60 minutes.</summary>
	public static readonly ServiceId Massage = new(Guid.Parse("01920000-0000-7000-8000-000000000205"));

	/// <summary>The clinic's clock: Nicaragua, UTC-6 all year.</summary>
	public const string Zone = "America/Managua";

	private static readonly Duration HalfHour = Duration.FromMinutes(30);

	private static readonly Duration OneHour = Duration.FromHours(1);

	/// <summary>Adds the clinic when it is not there yet; never touches it otherwise.</summary>
	/// <param name="services">The app's services.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>A task.</returns>
	public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
	{
		await using var scope = services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
		if (await db.Businesses.AnyAsync(b => b.Id == Clinic, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		db.Businesses.Add(Business.Create(Clinic, "Clínica Demo", Zone, slotGranularity: HalfHour, minimumNotice: OneHour));
		db.Services.AddRange(
			Service.Create(Consultation, Clinic, "Consulta general", HalfHour, 25.00m),
			Service.Create(Cleaning, Clinic, "Limpieza dental", Duration.FromMinutes(45), 40.00m),
			Service.Create(Orthodontics, Clinic, "Control de ortodoncia", HalfHour, 30.00m),
			Service.Create(Physiotherapy, Clinic, "Sesión de fisioterapia", OneHour, 35.00m),
			Service.Create(Massage, Clinic, "Masaje terapéutico", OneHour, 30.00m));

		db.Professionals.AddRange(
			Professional.Create(Lopez, Clinic, "Dra. Ana López", "Odontología", Weekdays((8, 12), (14, 18)), [Cleaning, Orthodontics]),
			Professional.Create(Martinez, Clinic, "Dr. Carlos Martínez", "Medicina general", [.. Weekdays((7, 13)), At(IsoDayOfWeek.Saturday, 8, 12)], [Consultation]),
			Professional.Create(
				Rivera,
				Clinic,
				"Lic. Sofía Rivera",
				"Fisioterapia",
				[.. Days([IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday], (9, 13), (15, 19)), At(IsoDayOfWeek.Saturday, 9, 13)],
				[Physiotherapy, Massage]));

		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	private static IEnumerable<WorkingHours> Weekdays(params (int From, int To)[] stretches) =>
		Days([IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday], stretches);

	private static IEnumerable<WorkingHours> Days(IsoDayOfWeek[] days, params (int From, int To)[] stretches) =>
		days.SelectMany(day => stretches.Select(s => At(day, s.From, s.To)));

	private static WorkingHours At(IsoDayOfWeek day, int from, int to) => new(day, new LocalTime(from, 0), new LocalTime(to, 0));
}
