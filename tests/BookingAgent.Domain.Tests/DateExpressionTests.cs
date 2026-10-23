namespace BookingAgent.Domain.Tests;

using BookingAgent.Domain.Scheduling;
using NodaTime;

public sealed class DateExpressionTests
{
	// Friday 23 Oct 2026.
	private static readonly LocalDate Friday = new(2026, 10, 23);

	[Theory]
	[InlineData("hoy", "2026-10-23")]
	[InlineData("Mañana", "2026-10-24")]
	[InlineData("pasado mañana", "2026-10-25")]
	[InlineData("el martes", "2026-10-27")]
	[InlineData("el martes que viene", "2026-10-27")]
	[InlineData("el próximo martes", "2026-10-27")]
	[InlineData("martes de la semana que viene", "2026-10-27")]
	[InlineData("el viernes", "2026-10-30")]
	[InlineData("el viernes de la próxima semana", "2026-10-30")]
	[InlineData("Sábado", "2026-10-24")]
	[InlineData("el miércoles.", "2026-10-28")]
	[InlineData("en 3 días", "2026-10-26")]
	[InlineData("dentro de una semana", "2026-10-30")]
	[InlineData("en dos semanas", "2026-11-06")]
	[InlineData("27 de octubre", "2026-10-27")]
	[InlineData("martes 27 de octubre", "2026-10-27")]
	[InlineData("el 2 de noviembre", "2026-11-02")]
	[InlineData("15 de enero", "2027-01-15")]
	[InlineData("20 de octubre", "2027-10-20")]
	[InlineData("27/10", "2026-10-27")]
	[InlineData("3/11/2026", "2026-11-03")]
	[InlineData("2026-12-01", "2026-12-01")]
	public void Spanish_day_expressions_resolve_on_the_business_calendar(string text, string expected)
	{
		Assert.Equal(LocalDate.FromDateTime(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)), DateExpression.Resolve(text, Friday));
	}

	[Fact]
	public void The_week_after_next_differs_from_the_next_occurrence_early_in_the_week()
	{
		var monday = new LocalDate(2026, 10, 26);

		Assert.Equal(new LocalDate(2026, 10, 28), DateExpression.Resolve("el miércoles", monday));
		Assert.Equal(new LocalDate(2026, 11, 4), DateExpression.Resolve("el miércoles de la semana que viene", monday));
		Assert.Equal(new LocalDate(2026, 11, 2), DateExpression.Resolve("el lunes", monday));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(null)]
	[InlineData("cuando puedas")]
	[InlineData("la semana que viene")]
	[InlineData("31 de febrero")]
	[InlineData("13/13")]
	[InlineData("en 400 días")]
	[InlineData("martes y jueves")]
	public void What_is_not_one_day_is_not_guessed(string? text)
	{
		Assert.Null(DateExpression.Resolve(text, Friday));
	}
}
