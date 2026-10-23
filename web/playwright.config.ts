import { defineConfig, devices } from "@playwright/test";
import path from "node:path";
import { baseURL, servers } from "./e2e/servers";

// The end-to-end test: everything real except the model, which plays a script (no key, no cost).
export default defineConfig({
	testDir: "./e2e",
	testIgnore: "demo/**",
	forbidOnly: !!process.env.CI,
	retries: 0,
	workers: 1,
	reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
	timeout: 90_000,
	// Notifications are sent within a second; the panel hears about changes at once.
	expect: { timeout: 15_000 },
	use: {
		baseURL,
		locale: "es-ES",
		trace: "retain-on-failure",
		screenshot: "only-on-failure",
	},
	projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
	webServer: servers({ AssistantScript__File: path.resolve(__dirname, "e2e/fixtures/assistant-script.json") }),
});
