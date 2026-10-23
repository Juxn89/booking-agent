import { expect, test } from "@playwright/test";
import { latestCode, panelKey } from "./servers";

// The whole product in one go, as two people use it: a customer books in the chat and confirms with the card's
// button; the clinic sees it appear in the panel without reloading; the customer signs in with the emailed code
// and cancels; the panel sees the slot free again; the notices are in the customer's inbox.
test("a chat booking appears live in the panel, and the customer cancels it with an emailed code", async ({ browser }) => {
	const email = "ana.e2e@ejemplo.com";

	// The clinic: the panel, on the week of next Monday, Dra. López's column.
	const clinic = await browser.newPage();
	await clinic.goto("/panel");
	await clinic.getByLabel("Clave del panel").fill(panelKey);
	await clinic.getByRole("button", { name: "Entrar" }).click();
	await clinic.getByRole("tab", { name: /Dra. Ana López/ }).click();
	await clinic.getByRole("button", { name: "Semana siguiente" }).click();
	await expect(clinic.getByText("En vivo")).toBeVisible();
	const block = clinic.getByRole("button", { name: /^10:00 Ana Prueba, Limpieza dental/ });
	await expect(block).toHaveCount(0);

	// The customer: the chat, the card, the button.
	const customer = await browser.newPage();
	await customer.goto("/chat");
	await customer.waitForLoadState("networkidle");
	await customer.getByLabel("Tu mensaje").fill(`Quiero una limpieza dental el lunes a las 10. Soy Ana Prueba, ${email}`);
	await customer.getByRole("button", { name: "Enviar" }).click();
	const card = customer.getByRole("region", { name: "Horario apartado" });
	await expect(card).toContainText("Limpieza dental con Dra. Ana López");
	await expect(customer.getByText("Te aparté la limpieza dental")).toBeVisible();

	// The panel hears about the hold, unconfirmed, made by the assistant.
	await expect(clinic.getByRole("button", { name: /^10:00 Ana Prueba, Limpieza dental, apartada$/ })).toBeVisible();
	await expect(block.getByText("IA")).toBeVisible();

	await card.getByRole("button", { name: "Confirmar cita" }).click();
	await expect(customer.getByRole("region", { name: "Cita confirmada" })).toBeVisible();
	await expect(clinic.getByRole("button", { name: /^10:00 Ana Prueba, Limpieza dental$/ })).toBeVisible();

	// The clinic opens the booking: the conversation and the assistant's steps behind it.
	await block.click();
	const details = clinic.getByRole("complementary", { name: "Detalle de la cita" });
	await expect(details).toContainText("Confirmada · con el asistente", { ignoreCase: true });
	await expect(details).toContainText("hold_slot");

	// The customer signs in with the emailed code and cancels.
	await customer.goto("/mis-citas");
	await customer.getByLabel("Correo").fill(email);
	const asked = new Date();
	await customer.getByRole("button", { name: "Enviarme el código" }).click();
	await expect(customer.getByLabel("Código")).toBeVisible();
	await customer.getByLabel("Código").fill(await latestCode(email, asked));
	await customer.getByRole("button", { name: "Entrar" }).click();
	await expect(customer.getByText("Limpieza dental con Dra. Ana López")).toBeVisible();
	await customer.getByRole("button", { name: "Cancelar", exact: true }).click();
	await customer.getByRole("button", { name: "Sí, cancelar cita" }).click();
	await expect(customer.getByText("No tienes citas próximas.")).toBeVisible();

	// The slot is free again in the panel, without a reload.
	await expect(clinic.getByRole("button", { name: /Ana Prueba/ })).toHaveCount(0);

	// The notices reached the in-app inbox (and Mailpit). The dispatcher sends within a second of the change, and the
	// inbox page shows what is there when it loads, so it is loaded until the last one is in.
	await expect(async () => {
		await customer.goto("/bandeja");
		await expect(customer.getByRole("heading", { name: /^Cita cancelada: Limpieza dental/ })).toBeVisible({ timeout: 2_000 });
	}).toPass({ timeout: 15_000 });
	await expect(customer.getByRole("heading", { name: /^Cita confirmada: Limpieza dental/ })).toBeVisible();
});
