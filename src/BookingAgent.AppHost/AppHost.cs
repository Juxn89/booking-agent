const int WebPort = 3020;
const int MailpitHttpPort = 8030;
const string AnthropicKey = "ANTHROPIC_API_KEY";
const string PanelKey = "PANEL_ACCESS_KEY";

var builder = DistributedApplication.CreateBuilder(args);

// Pinned so local runs and the integration tests (Testcontainers) use the same Postgres. The volume goes on
// /var/lib/postgresql, not the data folder: Postgres 18 images keep PGDATA in a versioned subdirectory.
var postgres = builder.AddPostgres("postgres")
	.WithImage("postgres")
	.WithImageTag("18.6-alpine")
	.WithVolume("booking-agent-pgdata", "/var/lib/postgresql")
	.WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("bookingsdb");

// Where confirmations and reminders land in local runs: an SMTP server with a web inbox, nothing leaves the box.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31.2")
	.WithHttpEndpoint(port: MailpitHttpPort, targetPort: 8025, name: "http")
	.WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");

var api = builder.AddProject<Projects.BookingAgent_Api>("api")
	.WithReference(database)
	.WaitFor(database)
	.WithEnvironment("Smtp__Host", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Host))
	.WithEnvironment("Smtp__Port", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Port))
	.WithEnvironment("Smtp__Security", "None")
	.WithHttpHealthCheck("/health");

// The model is called only when the developer has a key: in the environment, or in the repo's git-ignored .env
// (see .env.template). It is passed on as a secret parameter and never printed.
if ((Environment.GetEnvironmentVariable(AnthropicKey) ?? DotEnv(Path.Combine(builder.AppHostDirectory, "..", "..", ".env"), AnthropicKey)) is { Length: > 0 } key)
{
	api.WithEnvironment("Anthropic__ApiKey", builder.AddParameter("anthropic-api-key", key, secret: true));
}

// The business's panel opens with the staff's access key, from the environment or the .env; without one it stays
// closed.
if ((Environment.GetEnvironmentVariable(PanelKey) ?? DotEnv(Path.Combine(builder.AppHostDirectory, "..", "..", ".env"), PanelKey)) is { Length: > 0 } panelKey)
{
	api.WithEnvironment("Panel__AccessKey", builder.AddParameter("panel-access-key", panelKey, secret: true));
}

// The web app on http://localhost:3020, /api forwarded to the API wherever Aspire runs it. Aspire's proxy holds
// the port and hands Next its own in PORT, which "dev:aspire" ("next dev" without --port) honours.
builder.AddJavaScriptApp("web", "../../web", "dev:aspire")
	.WithPnpm(install: false)
	.WithHttpEndpoint(port: WebPort, env: "PORT")
	.WithEnvironment("API_URL", api.GetEndpoint("http"))
	.WithEnvironment("NEXT_TELEMETRY_DISABLED", "1")
	.WithExternalHttpEndpoints()
	.WaitFor(api);

await builder.Build().RunAsync();

// KEY=value lines; blank lines and # comments skipped. Only the one variable is read.
static string? DotEnv(string path, string name) =>
	File.Exists(path)
		? File.ReadLines(path)
			.Select(static line => line.Trim())
			.Where(line => line.StartsWith(name + "=", StringComparison.Ordinal))
			.Select(line => line[(name.Length + 1)..].Trim().Trim('"'))
			.LastOrDefault(static value => value.Length > 0)
		: null;
