import { expect, test, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { mkdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { baseURL, latestCode, panelKey } from "../servers";

// The walkthrough for the README, the portfolio and the Workana listing, recorded against the real model
// (playwright.demo.config.ts): the customer in the chat on the left, the clinic's panel on the right, side by side.
// It writes the video, its poster and the screenshots to docs/demo.
//
// Frames come from Chromium's screencast of each page (960×1080 each), with their timestamps; ffmpeg, in a container
// (pinned image, no network), lays the two streams side by side into a 1920×1080 MP4, so nothing is installed.

const out = path.resolve(__dirname, "../../../docs/demo");
const ffmpegImage = "jrottenberg/ffmpeg:9.0.1-ubuntu2404@sha256:7e8154ec4779d335054de5e173f1ef52032abaa12ac7416cc7f0c88f4973bd14";
const viewport = { width: 960, height: 1080 };
const fps = 30;
const email = "laura.gomez@ejemplo.com";

declare global {
	interface Window {
		demoCaption?: (text: string) => void;
	}
}

// A pointer that follows the mouse, a label saying whose screen it is, and (on the customer's side) a caption.
async function addOverlay(page: Page, label: string) {
	await page.addInitScript((who: string) => {
		const ensure = (id: string, style: string) => {
			let element = document.getElementById(id);
			if (!element) {
				element = document.createElement("div");
				element.id = id;
				element.setAttribute("style", style);
				document.body.appendChild(element);
			}
			return element;
		};
		const pointer = () =>
			ensure(
				"demo-pointer",
				"position:fixed;z-index:2147483647;width:22px;height:22px;margin:-11px 0 0 -11px;border-radius:50%;background:rgba(21,94,117,.3);border:2px solid #155e75;pointer-events:none;transition:transform .12s",
			);
		window.addEventListener("mousemove", (event) => {
			pointer().style.left = `${event.clientX}px`;
			pointer().style.top = `${event.clientY}px`;
		});
		window.addEventListener("mousedown", () => (pointer().style.transform = "scale(.6)"));
		window.addEventListener("mouseup", () => (pointer().style.transform = "scale(1)"));
		window.addEventListener("DOMContentLoaded", () => {
			ensure(
				"demo-label",
				"position:fixed;z-index:2147483645;top:14px;right:14px;padding:6px 14px;border-radius:999px;background:#17212b;color:#fff;font:600 15px/1.3 system-ui,sans-serif;letter-spacing:.02em;pointer-events:none",
			).textContent = who;
		});
		window.demoCaption = (text: string) => {
			const caption = ensure(
				"demo-caption",
				"position:fixed;z-index:2147483646;left:50%;bottom:26px;transform:translateX(-50%);width:max-content;max-width:min(880px,92vw);padding:14px 22px;border-radius:12px;background:rgba(23,33,43,.93);color:#fff;font:500 21px/1.45 system-ui,sans-serif;text-align:center;box-shadow:0 12px 32px rgba(0,0,0,.28);pointer-events:none",
			);
			caption.textContent = text;
			caption.style.display = text ? "block" : "none";
		};
	}, label);
}

async function say(page: Page, text: string, holdMs: number) {
	await page.evaluate((caption) => window.demoCaption?.(caption), text);
	await page.waitForTimeout(holdMs);
}

// Screenshots without the overlay: they stand on their own in the listing.
async function shot(page: Page, file: string) {
	const hidden = ["demo-caption", "demo-pointer", "demo-label"];
	await page.evaluate((ids) => ids.forEach((id) => document.getElementById(id)?.style.setProperty("visibility", "hidden")), hidden);
	await page.screenshot(file.endsWith(".jpg") ? { path: file, type: "jpeg", quality: 85 } : { path: file });
	await page.evaluate((ids) => ids.forEach((id) => document.getElementById(id)?.style.removeProperty("visibility")), hidden);
}

// One JPEG per composited frame, with its timestamp: the screencast only sends what changed.
async function record(page: Page, dir: string) {
	const frames: { file: string; at: number }[] = [];
	const cdp = await page.context().newCDPSession(page);
	cdp.on("Page.screencastFrame", async ({ data, metadata, sessionId }) => {
		const file = `f${String(frames.length).padStart(5, "0")}.jpg`;
		frames.push({ file, at: metadata.timestamp ?? Date.now() / 1000 });
		await writeFile(path.join(dir, file), Buffer.from(data, "base64"));
		await cdp.send("Page.screencastFrameAck", { sessionId }).catch(() => undefined);
	});
	await cdp.send("Page.startScreencast", { format: "jpeg", quality: 92, maxWidth: viewport.width, maxHeight: viewport.height, everyNthFrame: 1 });
	return async () => {
		await cdp.send("Page.stopScreencast");
		return frames;
	};
}

// A concat list that covers [start, end]: the first frame from the start, each frame until the next one.
function concatList(frames: { file: string; at: number }[], start: number, end: number) {
	const lines = frames.map((frame, i) => {
		const from = i === 0 ? start : frame.at;
		return `file '${frame.file}'\nduration ${((frames[i + 1]?.at ?? end) - from).toFixed(4)}`;
	});
	return `${lines.join("\n")}\nfile '${frames.at(-1)?.file}'\n`;
}

async function type(page: Page, text: string) {
	const input = page.getByLabel("Tu mensaje");
	await input.click();
	await input.pressSequentially(text, { delay: 35 });
	await page.getByRole("button", { name: "Enviar" }).click();
}

// Waits for the assistant's answer; if it asks "¿te lo aparto?" instead of holding, the customer says yes.
async function untilHeld(page: Page) {
	const card = page.getByRole("region", { name: "Horario apartado" });
	await expect(page.getByText("Escribiendo…")).toHaveCount(0, { timeout: 90_000 });
	if (!(await card.count())) {
		await type(page, "Sí, apártala");
	}
	await expect(card).toBeVisible();
}

test("demo", async ({ browser }) => {
	await mkdir(out, { recursive: true });
	const dirs = { customer: test.info().outputPath("customer"), clinic: test.info().outputPath("clinic") };
	for (const dir of Object.values(dirs)) {
		await rm(dir, { recursive: true, force: true });
		await mkdir(dir, { recursive: true });
	}

	const customer = await (await browser.newContext({ viewport, locale: "es-ES", baseURL })).newPage();
	const clinic = await (await browser.newContext({ viewport, locale: "es-ES", baseURL })).newPage();
	await addOverlay(customer, "Cliente");
	await addOverlay(clinic, "Clínica");

	await clinic.goto("/panel");
	await clinic.waitForLoadState("networkidle");
	await clinic.getByLabel("Clave del panel").fill(panelKey);
	await clinic.getByRole("button", { name: "Entrar" }).click();
	await clinic.getByRole("tab", { name: /Dra. Ana López/ }).click();
	await clinic.getByRole("button", { name: "Semana siguiente" }).click();
	await expect(clinic.getByText("En vivo")).toBeVisible();
	await customer.goto("/chat");
	await customer.waitForLoadState("networkidle");
	await customer.mouse.move(480, 600);
	await clinic.mouse.move(700, 400);

	const stopCustomer = await record(customer, dirs.customer);
	const stopClinic = await record(clinic, dirs.clinic);
	const started = Date.now() / 1000;

	await say(customer, "Una clínica que recibe reservas por chat: a la izquierda, el cliente; a la derecha, el panel de la clínica.", 5500);

	await say(customer, "El cliente escribe como le habla a una recepcionista.", 0);
	await type(customer, "Hola, necesito una limpieza dental el lunes en la mañana");
	await expect(customer.getByText("Escribiendo…")).toHaveCount(0, { timeout: 90_000 });
	await say(customer, "El asistente (Claude Haiku) resuelve “el lunes” en el servidor, consulta la agenda real y ofrece horarios libres.", 6000);
	await shot(customer, path.join(out, "01-chat.png"));

	await type(customer, `A las 10, por favor. Soy Laura Gómez, ${email}`);
	await untilHeld(customer);
	await expect(clinic.getByRole("button", { name: /^10:00 Laura Gómez, Limpieza dental, apartada$/ })).toBeVisible();
	await say(customer, "Aparta el horario unos minutos, y en el panel aparece al instante, todavía sin confirmar.", 6000);
	await shot(customer, path.join(out, "02-tarjeta.png"));
	await shot(clinic, path.join(out, "03-panel-apartada.png"));

	await say(customer, "Solo la persona confirma, con el botón: el asistente no puede confirmar nada.", 3500);
	await customer.getByRole("button", { name: "Confirmar cita" }).click();
	await expect(customer.getByRole("region", { name: "Cita confirmada" })).toBeVisible();
	const block = clinic.getByRole("button", { name: /^10:00 Laura Gómez, Limpieza dental$/ });
	await expect(block).toBeVisible();
	await say(customer, "Confirmada: el panel lo refleja sin recargar, y al cliente le llega la confirmación por correo.", 5500);

	await say(customer, "La clínica abre la cita y ve el porqué: la conversación y cada paso del asistente, con su costo.", 0);
	await block.click();
	const details = clinic.getByRole("complementary", { name: "Detalle de la cita" });
	await expect(details.getByText("Pasos del asistente")).toBeVisible();
	await details.getByText("Pasos del asistente").scrollIntoViewIfNeeded();
	await clinic.waitForTimeout(6500);
	await shot(clinic, path.join(out, "04-traza.png"));
	await clinic.evaluate(() => window.scrollTo({ top: 0, behavior: "smooth" }));

	await customer.goto("/mis-citas");
	await customer.waitForLoadState("networkidle");
	await say(customer, "Para cambiar o cancelar, el cliente entra con un código que le llega al correo.", 1500);
	await customer.getByLabel("Correo").pressSequentially(email, { delay: 25 });
	const asked = new Date();
	await customer.getByRole("button", { name: "Enviarme el código" }).click();
	await customer.getByLabel("Código").pressSequentially(await latestCode(email, asked), { delay: 120 });
	await customer.getByRole("button", { name: "Entrar" }).click();
	await expect(customer.getByText("Limpieza dental con Dra. Ana López")).toBeVisible();
	await customer.waitForTimeout(2500);
	await shot(customer, path.join(out, "05-mis-citas.png"));

	await say(customer, "Cancela, y el horario queda libre en el panel al instante.", 0);
	await customer.getByRole("button", { name: "Cancelar", exact: true }).click();
	await customer.getByRole("button", { name: "Sí, cancelar cita" }).click();
	await expect(clinic.getByRole("button", { name: /Laura Gómez/ })).toHaveCount(0);
	await customer.waitForTimeout(3500);

	await say(customer, "Agenda con asistente · .NET 10 · Claude Haiku · github.com/Juxn89/booking-agent", 5000);
	const ended = Date.now() / 1000;
	const [customerFrames, clinicFrames] = [await stopCustomer(), await stopClinic()];
	await shot(clinic, path.join(out, "poster.jpg"));

	// Both streams over the same span, so they stay in step side by side.
	const start = Math.min(customerFrames[0]?.at ?? started, clinicFrames[0]?.at ?? started);
	await writeFile(path.join(dirs.customer, "frames.txt"), concatList(customerFrames, start, ended));
	await writeFile(path.join(dirs.clinic, "frames.txt"), concatList(clinicFrames, start, ended));
	const scaled = `fps=${fps},scale=${viewport.width}:${viewport.height}:flags=lanczos,setsar=1`;
	execFileSync(
		"docker",
		[
			"run", "--rm", "--network", "none", "--read-only", "--tmpfs", "/tmp",
			"-v", `${dirs.customer}:/left`, "-v", `${dirs.clinic}:/right`, "-v", `${out}:/out`, ffmpegImage,
			"-y", "-loglevel", "error",
			"-f", "concat", "-safe", "0", "-i", "/left/frames.txt",
			"-f", "concat", "-safe", "0", "-i", "/right/frames.txt",
			"-filter_complex", `[0:v]${scaled}[l];[1:v]${scaled}[r];[l][r]hstack=inputs=2,format=yuv420p`,
			"-c:v", "libx264", "-preset", "slow", "-crf", "20", "-movflags", "+faststart",
			"/out/demo.mp4",
		],
		{ stdio: "inherit" },
	);
	console.log(`${customerFrames.length}+${clinicFrames.length} frames, ${(ended - start).toFixed(1)} s → ${path.join(out, "demo.mp4")}`);
});
