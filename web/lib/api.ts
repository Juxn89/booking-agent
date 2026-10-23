// The API's shapes and the calls the screens make. Everything goes through /api, which next.config.ts forwards to
// the .NET API: one origin for the browser.

// The demo clinic (DemoData.Clinic in the API); a real deployment would set its own.
export const BUSINESS_ID = process.env.NEXT_PUBLIC_BUSINESS_ID ?? "01920000-0000-7000-8000-000000000001";

export type Service = { id: string; name: string; durationMinutes: number; price: number };
export type Professional = { id: string; name: string; specialty: string; serviceIds: string[] };
export type Catalog = {
	business: { id: string; name: string; timeZone: string };
	services: Service[];
	professionals: Professional[];
};

// Instants are ISO 8601 in UTC; localStart is the same moment on the business's clock, without an offset.
export type Slot = { start: string; end: string; localStart: string };

export type Booking = {
	id: string;
	professionalId: string;
	serviceId: string;
	start: string;
	end: string;
	localStart: string;
	status: "Held" | "Confirmed" | "Cancelled";
	holdExpiresAt: string | null;
};

export type Hold = { booking: Booking; confirmationToken: string };

export type InboxItem = { id: string; bookingId: string; subject: string; body: string; receivedAt: string };

// RFC 9457 with the API's stable code (docs/errors.md) and, on a taken slot, the nearest free starts.
export type Problem = { status: number; title: string; code: string; alternatives?: string };

export class ApiError extends Error {
	constructor(readonly problem: Problem) {
		super(problem.title);
	}
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
	const response = await fetch(`/api${path}`, {
		...init,
		headers: { Accept: "application/json", ...(init?.body ? { "Content-Type": "application/json" } : {}) },
	});
	if (!response.ok) {
		const problem = (await response.json().catch(() => null)) as Problem | null;
		throw new ApiError(problem ?? { status: response.status, title: response.statusText, code: "http.error" });
	}
	return (await response.json()) as T;
}

export const api = {
	catalog: () => call<Catalog>(`/v1/businesses/${BUSINESS_ID}/catalog`),
	availability: (professionalId: string, serviceId: string, date: string) =>
		call<Slot[]>(`/v1/professionals/${professionalId}/availability?serviceId=${serviceId}&from=${date}`),
	hold: (body: {
		professionalId: string;
		serviceId: string;
		start: string;
		customer: { name: string; email: string; phone: string | null };
	}) => call<Hold>("/v1/holds", { method: "POST", body: JSON.stringify(body) }),
	confirm: (holdId: string, token: string) =>
		call<Booking>(`/v1/holds/${holdId}/confirm`, { method: "POST", body: JSON.stringify({ token }) }),
	inbox: (email: string) => call<InboxItem[]>(`/v1/dev/inbox?email=${encodeURIComponent(email)}`),
};
