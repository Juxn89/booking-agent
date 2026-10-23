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

// A slot the assistant held: the card shows it, and only its token (never seen by the model) confirms it.
export type HeldByAssistant = {
	holdId: string;
	confirmationToken: string;
	serviceName: string;
	professionalName: string;
	localStart: string;
	expiresAt: string;
	// For a move: the appointment it replaces once confirmed.
	replaces: string | null;
};

// A cancellation the assistant proposes; the customer cancels it, signed in, with the card's button.
export type CancelProposal = { bookingId: string; serviceName: string; professionalName: string; localStart: string };

export type AssistantReply = {
	conversationId: string;
	reply: string;
	holds: HeldByAssistant[];
	cancellations: CancelProposal[];
	signInRequired: boolean;
};

export type Me = { name: string; email: string; expiresAt: string };

export type MyBooking = {
	id: string;
	serviceId: string;
	serviceName: string;
	professionalId: string;
	professionalName: string;
	start: string;
	localStart: string;
	canChange: boolean;
};

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
	if (response.status === 202 || response.status === 204) {
		return undefined as T;
	}
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
	send: (conversationId: string | null, message: string) =>
		call<AssistantReply>(`/v1/businesses/${BUSINESS_ID}/assistant/messages`, {
			method: "POST",
			body: JSON.stringify({ conversationId, message }),
		}),
	requestCode: (email: string) => call<void>(`/v1/businesses/${BUSINESS_ID}/sign-in/code`, { method: "POST", body: JSON.stringify({ email }) }),
	signIn: (email: string, code: string) =>
		call<Me>(`/v1/businesses/${BUSINESS_ID}/sign-in`, { method: "POST", body: JSON.stringify({ email, code }) }),
	signOut: () => call<void>(`/v1/businesses/${BUSINESS_ID}/sign-out`, { method: "POST" }),
	me: () => call<Me>(`/v1/businesses/${BUSINESS_ID}/me`),
	myBookings: () => call<MyBooking[]>(`/v1/businesses/${BUSINESS_ID}/me/bookings`),
	cancelMine: (id: string) => call<Booking>(`/v1/businesses/${BUSINESS_ID}/me/bookings/${id}/cancel`, { method: "POST" }),
	moveMine: (id: string, start: string) =>
		call<Hold>(`/v1/businesses/${BUSINESS_ID}/me/bookings/${id}/reschedule`, { method: "POST", body: JSON.stringify({ start }) }),
	inbox: () => call<InboxItem[]>(`/v1/businesses/${BUSINESS_ID}/me/inbox`),
};
