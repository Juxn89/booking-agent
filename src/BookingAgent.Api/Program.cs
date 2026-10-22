using BookingAgent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapDefaultEndpoints();

await app.RunAsync();

/// <summary>The entry point, visible to the integration tests' <c>WebApplicationFactory</c>.</summary>
public partial class Program;
