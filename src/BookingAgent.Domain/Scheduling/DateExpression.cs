namespace BookingAgent.Domain.Scheduling;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NodaTime;

/// <summary>
/// Turns what a customer writes for a day ("mañana", "el martes que viene", "27 de octubre") into a date on the
/// business's calendar. The server does this, never the model: a model that adds days to a date in its head gets
/// it wrong often enough to book the wrong day. Spanish only, like the demo business.
/// </summary>
/// <remarks>
/// A weekday on its own, with "próximo" or with "que viene" means its next occurrence after today: on a Friday,
/// "el martes" is in four days. "El martes de la semana que viene" means the Tuesday of next week (Monday to
/// Sunday). A day and month without a year is this year's, or next year's when it has passed.
/// </remarks>
public static partial class DateExpression
{
	private static readonly string[] Weekdays = ["lunes", "martes", "miercoles", "jueves", "viernes", "sabado", "domingo"];

	private static readonly string[] Months =
		["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

	// Words that change nothing: "el", "para el", "este".
	private static readonly string[] Fillers = ["para el ", "para ", "el ", "este ", "esta "];

	private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
	{
		["un"] = 1,
		["una"] = 1,
		["dos"] = 2,
		["tres"] = 3,
		["cuatro"] = 4,
		["cinco"] = 5,
		["seis"] = 6,
		["siete"] = 7,
		["ocho"] = 8,
		["nueve"] = 9,
		["diez"] = 10,
		["quince"] = 15,
	};

	/// <summary>Resolves an expression.</summary>
	/// <param name="text">What the customer wrote for the day.</param>
	/// <param name="today">Today on the business's calendar.</param>
	/// <returns>The date, or <see langword="null"/> when the text is not a day this understands.</returns>
	public static LocalDate? Resolve(string? text, LocalDate today)
	{
		var input = Normalize(text);
		if (input.Length == 0)
		{
			return null;
		}

		switch (input)
		{
			case "hoy":
				return today;
			case "manana":
				return today.PlusDays(1);
			case "pasado manana":
				return today.PlusDays(2);
		}

		return Relative(input, today) ?? Weekday(input, today) ?? Explicit(input, today);
	}

	// "en 3 dias", "dentro de una semana", "en dos semanas".
	private static LocalDate? Relative(string input, LocalDate today)
	{
		var match = RelativePattern().Match(input);
		if (!match.Success)
		{
			return null;
		}

		var amount = int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
			? n
			: NumberWords.GetValueOrDefault(match.Groups["n"].Value);
		if (amount is <= 0 or > Availability.MaxDays)
		{
			return null;
		}

		return today.PlusDays(match.Groups["unit"].Value.StartsWith("semana", StringComparison.Ordinal) ? amount * 7 : amount);
	}

	private static LocalDate? Weekday(string input, LocalDate today)
	{
		var match = WeekdayPattern().Match(input);
		if (!match.Success)
		{
			return null;
		}

		var day = (IsoDayOfWeek)(Array.IndexOf(Weekdays, match.Groups["day"].Value) + 1);
		if (match.Groups["nextweek"].Success)
		{
			var nextMonday = today.PlusDays(7 - ((int)today.DayOfWeek - 1));
			return nextMonday.PlusDays((int)day - 1);
		}

		return today.Next(day);
	}

	// "27 de octubre", "27 de octubre de 2026", "27/10", "27/10/2026", "2026-10-27"; optionally after a weekday.
	private static LocalDate? Explicit(string input, LocalDate today)
	{
		if (IsoPattern().Match(input) is { Success: true } iso)
		{
			return Date(Number(iso, "y"), Number(iso, "m"), Number(iso, "d"));
		}

		if (DayMonthPattern().Match(input) is { Success: true } written)
		{
			var month = written.Groups["mname"].Success
				? Array.IndexOf(Months, written.Groups["mname"].Value) + 1
				: Number(written, "m");
			if (written.Groups["y"].Success)
			{
				return Date(Number(written, "y"), month, Number(written, "d"));
			}

			var thisYear = Date(today.Year, month, Number(written, "d"));
			return thisYear is { } date && date < today ? Date(today.Year + 1, month, Number(written, "d")) : thisYear;
		}

		return null;
	}

	private static int Number(Match match, string group) => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

	private static LocalDate? Date(int year, int month, int day) =>
		month is >= 1 and <= 12 && year is >= 2000 and <= 2100 && day >= 1 && day <= CalendarSystem.Iso.GetDaysInMonth(year, month)
			? new LocalDate(year, month, day)
			: null;

	// Lower case, no accents, single spaces, no trailing punctuation, no filler words in front.
	private static string Normalize(string? text)
	{
		var decomposed = (text ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
		var plain = new StringBuilder(decomposed.Length);
		foreach (var c in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
			{
				plain.Append(c);
			}
		}

		var result = Spaces().Replace(plain.ToString(), " ").Trim().TrimEnd('.', ',', '?', '!');
		foreach (var filler in Fillers)
		{
			if (result.StartsWith(filler, StringComparison.Ordinal))
			{
				result = result[filler.Length..];
			}
		}

		return result;
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces();

	[GeneratedRegex(@"^(?:en|dentro de) (?<n>\d{1,2}|un|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|quince) (?<unit>dias?|semanas?)$")]
	private static partial Regex RelativePattern();

	[GeneratedRegex(@"^(?:proximo |siguiente )?(?<day>lunes|martes|miercoles|jueves|viernes|sabado|domingo)(?: (?:que viene|proximo|siguiente)| (?<nextweek>de la (?:semana que viene|proxima semana|semana siguiente)))?$")]
	private static partial Regex WeekdayPattern();

	[GeneratedRegex(@"^(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})$")]
	private static partial Regex IsoPattern();

	[GeneratedRegex(@"^(?:(?:lunes|martes|miercoles|jueves|viernes|sabado|domingo),? )?(?<d>\d{1,2})(?: de (?<mname>enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre)(?: de(?:l)? (?<y>\d{4}))?|/(?<m>\d{1,2})(?:/(?<y>\d{4}))?)$")]
	private static partial Regex DayMonthPattern();
}
