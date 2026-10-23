namespace BookingAgent.Application.Features.Assistant;

using System.Text.RegularExpressions;

/// <summary>Keeps personal data out of the traces: they are for understanding the assistant, not for finding people.</summary>
public static partial class Pii
{
	/// <summary>"ana.perez@ejemplo.com" → "a***@ejemplo.com". The domain stays: it says something about delivery problems.</summary>
	/// <param name="text">Any text.</param>
	/// <returns>The text with every email's local part masked.</returns>
	public static string MaskEmails(string text) => string.IsNullOrEmpty(text) ? text : Email().Replace(text, "$1***@");

	[GeneratedRegex(@"([A-Za-z0-9])[A-Za-z0-9._%+\-]*@")]
	private static partial Regex Email();
}
