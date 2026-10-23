using System.Text.Json.Serialization;
using BookingAgent.Api.Endpoints;
using BookingAgent.Infrastructure;
using BookingAgent.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

// Instants, local dates and times as ISO 8601; enums by name.
builder.Services.ConfigureHttpJsonOptions(options =>
{
	options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
	options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddRateLimits(builder.Configuration);

var app = builder.Build();

// A malformed request (bad JSON, wrong content type) is the client's error, not a 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
	StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
		? badRequest.StatusCode
		: StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
app.UseForwardedHeaders();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
	await DatabaseMigrator.MigrateAsync(app.Services);
	await DemoData.SeedAsync(app.Services);
}

app.MapDefaultEndpoints();
app.MapCatalogEndpoints();
app.MapBookingEndpoints();
app.MapAssistantEndpoints();
app.MapCustomerEndpoints();

await app.RunAsync();

/// <summary>The entry point, visible to the integration tests' <c>WebApplicationFactory</c>.</summary>
public partial class Program;
