import type { PlaywrightTestConfig } from "@playwright/test";

// Ports of their own, so a run never lands on the dev servers (5120 and 3020) or their Mailpit (8030).
const apiPort = 5198;
const webPort = 3021;

export const baseURL = `http://localhost:${webPort}`;

// Mailpit's HTTP API, where the sign-in codes are read: the one CI starts as a service, or `pnpm e2e:deps`.
export const mailpitURL = process.env.E2E_MAILPIT ?? "http://localhost:8026";

// The panel's key in these runs only.
export const panelKey = "e2e-panel-key-not-a-secret";

/**
 * The real API (notifications, sign-in, panel, SignalR) and a production build of the web app. Each run gets its
 * own database, so every slot is free and every customer new.
 * @param assistant How the assistant answers: a script (the end-to-end test) or the real model (the demo).
 */
export function servers(assistant: Record<string, string>): PlaywrightTestConfig["webServer"] {
	const run = (process.env.E2E_RUN ??= `${Date.now()}`);
	const database =
		process.env.E2E_DATABASE ?? `Host=localhost;Port=5434;Username=postgres;Password=postgres;Database=bookings_e2e_${run}`;

	return [
		{
			command: "dotnet run --project ../src/BookingAgent.Api --no-launch-profile",
			url: `http://localhost:${apiPort}/health`,
			env: {
				ASPNETCORE_ENVIRONMENT: "Development",
				ASPNETCORE_URLS: `http://localhost:${apiPort}`,
				ConnectionStrings__bookingsdb: database,
				Panel__AccessKey: panelKey,
				Smtp__Host: "localhost",
				Smtp__Port: process.env.E2E_SMTP_PORT ?? "1026",
				Smtp__Security: "None",
				Notifications__PollInterval: "00:00:01",
				...assistant,
			},
			timeout: 180_000,
		},
		{
			// The rewrite to the API is fixed at build time, hence API_URL here.
			command: `pnpm build && pnpm exec next start --port ${webPort}`,
			url: baseURL,
			env: { API_URL: `http://localhost:${apiPort}`, NEXT_TELEMETRY_DISABLED: "1" },
			timeout: 180_000,
		},
	];
}

/**
 * The sign-in code Mailpit received for an email after a moment: Mailpit outlives the runs, so an older code for the
 * same address may still be there.
 */
export async function latestCode(email: string, since: Date): Promise<string> {
	for (let attempt = 0; attempt < 20; attempt++) {
		const found = (await (await fetch(`${mailpitURL}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json()) as {
			messages?: { Subject: string; Created: string }[];
		};
		const code = found.messages
			?.filter((m) => new Date(m.Created) >= since)
			.map((m) => /código.*?([0-9]{6})/.exec(m.Subject)?.[1])
			.find(Boolean);
		if (code) {
			return code;
		}
		await new Promise((resolve) => setTimeout(resolve, 500));
	}
	throw new Error(`No sign-in code reached ${email}.`);
}
