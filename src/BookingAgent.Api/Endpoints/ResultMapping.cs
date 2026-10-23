namespace BookingAgent.Api.Endpoints;

using BookingAgent.Application.Common;

/// <summary>The single place where use-case errors become HTTP. Endpoints never pick status codes ad hoc.</summary>
internal static class ResultMapping
{
	private const string ErrorsDocument = "https://github.com/Juxn89/booking-agent/blob/main/docs/errors.md";

	/// <summary>An error as RFC 9457 problem details, with its stable code.</summary>
	/// <param name="error">The error.</param>
	/// <returns>The result.</returns>
	public static IResult ToProblem(this Error error)
	{
		ArgumentNullException.ThrowIfNull(error);

		var status = error.Kind switch
		{
			ErrorKind.Validation => StatusCodes.Status400BadRequest,
			ErrorKind.NotFound => StatusCodes.Status404NotFound,
			ErrorKind.Conflict => StatusCodes.Status409Conflict,
			ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
			ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
			ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
			_ => StatusCodes.Status500InternalServerError,
		};

		var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
		foreach (var (key, value) in error.Details ?? new Dictionary<string, string>())
		{
			extensions[key] = value;
		}

		return Results.Problem(
			statusCode: status,
			title: error.Message,
			type: $"{ErrorsDocument}#{error.Code.Replace('.', '-')}",
			extensions: extensions);
	}

	/// <summary>A result as HTTP.</summary>
	/// <typeparam name="T">The value's type.</typeparam>
	/// <param name="result">The result.</param>
	/// <param name="onSuccess">How a success is returned.</param>
	/// <returns>The HTTP result.</returns>
	public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(onSuccess);
		return result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();
	}
}
