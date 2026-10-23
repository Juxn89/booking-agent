// Dates as the clinic says them. The API sends localStart already on the business's clock ("2026-11-02T09:00:00"),
// so it is formatted as is, in UTC, never shifted by the browser's own time zone.

function localParts(local: string): Date {
	const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(local);
	if (!parts) {
		throw new Error(`Not a local date and time: ${local}`);
	}
	const [year, month, day, hour, minute] = parts.slice(1).map(Number) as [number, number, number, number, number];
	return new Date(Date.UTC(year, month - 1, day, hour, minute));
}

const dayFormat = new Intl.DateTimeFormat("es", { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" });
const timeFormat = new Intl.DateTimeFormat("es", { hour: "numeric", minute: "2-digit", hourCycle: "h23", timeZone: "UTC" });
const moneyFormat = new Intl.NumberFormat("es", { style: "currency", currency: "USD" });

// "lunes, 2 de noviembre"
export const formatDay = (local: string) => dayFormat.format(localParts(local));

// "9:00"
export const formatTime = (local: string) => timeFormat.format(localParts(local));

export const formatPrice = (amount: number) => moneyFormat.format(amount);

// "America/Managua" → "Managua"
export const zoneCity = (zone: string) => zone.slice(zone.lastIndexOf("/") + 1).replaceAll("_", " ");

// Today on the business's clock, as the API's dates are written: 2026-10-27.
export function todayIn(zone: string): string {
	return new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}
