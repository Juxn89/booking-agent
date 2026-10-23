import { defineConfig } from "@playwright/test";
import { readFileSync } from "node:fs";
import path from "node:path";
import { servers } from "./e2e/servers";

// Records the demo video and screenshots (docs/demo) with the real model: a few tenths of a cent per take, so it is
// run by hand, never in CI. The key comes from ANTHROPIC_API_KEY or the repo's git-ignored .env, never printed;
// DEMO_SCRIPTED=1 rehearses with a script instead, for free. Docker encodes the video.
function anthropicKey(): string | undefined {
	if (process.env.ANTHROPIC_API_KEY) {
		return process.env.ANTHROPIC_API_KEY;
	}
	try {
		return /^ANTHROPIC_API_KEY=(.+)$/m.exec(readFileSync(path.resolve(__dirname, "../.env"), "utf8"))?.[1]?.trim();
	} catch {
		return undefined;
	}
}

const scripted = process.env.DEMO_SCRIPTED === "1";
const key = scripted ? undefined : anthropicKey();
if (!scripted && !key) {
	throw new Error("The demo chats with Claude: set ANTHROPIC_API_KEY (or .env), or DEMO_SCRIPTED=1 to rehearse.");
}

export default defineConfig({
	testDir: "./e2e/demo",
	workers: 1,
	reporter: "list",
	timeout: 300_000,
	expect: { timeout: 90_000 },
	use: {
		// Slow enough for a viewer to follow every click.
		launchOptions: { slowMo: 120 },
	},
	webServer: servers(
		scripted ? { AssistantScript__File: path.resolve(__dirname, "e2e/demo/rehearsal-script.json") } : { Anthropic__ApiKey: key! },
	),
});
