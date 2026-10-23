"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError, api, type Hold, type Me, type MyBooking, type Slot } from "@/lib/api";
import { formatDay, formatTime, todayIn } from "@/lib/format";
import { HoldCard } from "../ui/HoldCard";
import { Notice } from "../ui/Notice";
import { SignIn } from "../ui/SignIn";

// The demo clinic's clock and city: every day and time here is on it.
const ZONE = "America/Managua";
const CITY = "Managua";

const describe = (error: unknown) => {
	const code = error instanceof ApiError ? error.problem.code : "";
	switch (code) {
		case "booking.too_late":
			return "Ya es muy tarde para cambiarla aquí; llámanos, por favor.";
		case "booking.slot_unavailable":
		case "booking.slot_taken":
			return "Ese horario ya no está libre. Elige otro.";
		case "booking.hold_expired":
			return "El horario apartado venció. Elige la hora de nuevo.";
		default:
			return "No pudimos completar el cambio. Intenta de nuevo.";
	}
};

type Session = { state: "checking" } | { state: "signed-out" } | { state: "signed-in"; me: Me };

export function MyBookings() {
	const [session, setSession] = useState<Session>({ state: "checking" });
	const [bookings, setBookings] = useState<MyBooking[] | null>(null);
	const [notice, setNotice] = useState<{ tone: "error" | "info"; text: string } | null>(null);

	const load = useCallback(async () => {
		try {
			setBookings(await api.myBookings());
		} catch (error) {
			if (error instanceof ApiError && error.problem.status === 401) {
				setSession({ state: "signed-out" });
			} else {
				setNotice({ tone: "error", text: "No pudimos cargar tus citas." });
			}
		}
	}, []);

	useEffect(() => {
		api.me().then(
			(me) => {
				setSession({ state: "signed-in", me });
				void load();
			},
			() => setSession({ state: "signed-out" }),
		);
	}, [load]);

	if (session.state === "checking") {
		return <p className="text-muted" role="status">Cargando…</p>;
	}

	if (session.state === "signed-out") {
		return (
			<SignIn
				onSignedIn={(me) => {
					setSession({ state: "signed-in", me });
					void load();
				}}
			/>
		);
	}

	return (
		<div className="space-y-6">
			<div className="flex flex-wrap items-center justify-between gap-3">
				<p className="text-muted">
					Hola, <span className="font-medium text-ink">{session.me.name}</span>.
				</p>
				<button
					type="button"
					onClick={async () => {
						await api.signOut();
						setSession({ state: "signed-out" });
						setBookings(null);
					}}
					className="text-sm text-muted underline-offset-2 hover:text-brand hover:underline"
				>
					Salir
				</button>
			</div>

			{notice && <Notice tone={notice.tone}>{notice.text}</Notice>}
			{!bookings && <p className="text-muted" role="status">Cargando tus citas…</p>}
			{bookings?.length === 0 && <p className="text-muted">No tienes citas próximas.</p>}
			<ul className="space-y-4">
				{bookings?.map((booking) => (
					<BookingItem
						key={booking.id}
						booking={booking}
						onChanged={(text) => {
							setNotice({ tone: "info", text });
							void load();
						}}
					/>
				))}
			</ul>
		</div>
	);
}

function BookingItem({ booking, onChanged }: { booking: MyBooking; onChanged: (text: string) => void }) {
	const [mode, setMode] = useState<"idle" | "cancel" | "move">("idle");
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);
	const summary = `${booking.serviceName} con ${booking.professionalName}`;

	async function cancel() {
		setBusy(true);
		setError(null);
		try {
			await api.cancelMine(booking.id);
			onChanged(`Cancelamos tu cita de ${booking.serviceName} del ${formatDay(booking.localStart)}.`);
		} catch (failure) {
			setError(describe(failure));
		} finally {
			setBusy(false);
		}
	}

	return (
		<li className="rounded-xl border border-line bg-card p-5 shadow-sm">
			<p className="font-semibold">{summary}</p>
			<p className="mt-1 text-muted">
				{formatDay(booking.localStart)}, {formatTime(booking.localStart)} (hora de {CITY}).
			</p>
			{!booking.canChange && <p className="mt-2 text-sm text-muted">Falta poco: para cambiarla, llámanos.</p>}
			{booking.canChange && mode === "idle" && (
				<div className="mt-4 flex flex-wrap gap-3">
					<button type="button" onClick={() => setMode("move")} className="rounded-lg border border-line px-4 py-2 font-medium transition-colors hover:bg-brand-soft">
						Cambiar hora
					</button>
					<button type="button" onClick={() => setMode("cancel")} className="rounded-lg border border-line px-4 py-2 font-medium text-danger transition-colors hover:bg-danger-soft">
						Cancelar
					</button>
				</div>
			)}
			{mode === "cancel" && (
				<div className="mt-4 flex flex-wrap items-center gap-3">
					<p className="text-sm">¿Seguro que quieres cancelarla?</p>
					<button type="button" onClick={cancel} disabled={busy} className="rounded-lg bg-danger px-4 py-2 font-medium text-white disabled:opacity-60">
						{busy ? "Cancelando…" : "Sí, cancelar cita"}
					</button>
					<button type="button" onClick={() => setMode("idle")} className="rounded-lg border border-line px-4 py-2 font-medium">
						No
					</button>
				</div>
			)}
			{mode === "move" && (
				<Mover
					booking={booking}
					onMoved={(localStart) => onChanged(`Listo: tu cita ahora es el ${formatDay(localStart)} a las ${formatTime(localStart)}.`)}
					onClose={() => setMode("idle")}
				/>
			)}
			{error && (
				<div className="mt-3">
					<Notice tone="error">{error}</Notice>
				</div>
			)}
		</li>
	);
}

// Choose a new day and time; the new time is held, and the move happens when the customer confirms it.
function Mover({ booking, onMoved, onClose }: { booking: MyBooking; onMoved: (localStart: string) => void; onClose: () => void }) {
	const [date, setDate] = useState(() => todayIn(ZONE));
	const [slots, setSlots] = useState<{ date: string; slots: Slot[] } | null>(null);
	const [hold, setHold] = useState<Hold | null>(null);
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		let current = true;
		api.availability(booking.professionalId, booking.serviceId, date).then(
			(found) => current && setSlots({ date, slots: found }),
			() => current && setSlots({ date, slots: [] }),
		);
		return () => {
			current = false;
		};
	}, [booking.professionalId, booking.serviceId, date]);

	async function choose(slot: Slot) {
		setBusy(true);
		setError(null);
		try {
			setHold(await api.moveMine(booking.id, slot.start));
		} catch (failure) {
			setError(describe(failure));
		} finally {
			setBusy(false);
		}
	}

	async function confirm(held: Hold) {
		setBusy(true);
		setError(null);
		try {
			const moved = await api.confirm(held.booking.id, held.confirmationToken);
			onMoved(moved.localStart);
		} catch (failure) {
			setError(describe(failure));
			setHold(null);
		} finally {
			setBusy(false);
		}
	}

	if (hold) {
		return (
			<div className="mt-4">
				<HoldCard
					compact
					expiresAt={hold.booking.holdExpiresAt}
					localStart={hold.booking.localStart}
					summary={`Cambio: ${booking.serviceName} con ${booking.professionalName}`}
					city={CITY}
					busy={busy}
					onConfirm={() => confirm(hold)}
					onChange={() => setHold(null)}
				/>
				{error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
			</div>
		);
	}

	const shown = slots?.date === date ? slots.slots : null;
	return (
		<div className="mt-4 space-y-3">
			<label className="block text-sm text-muted">
				Nuevo día (hora de {CITY})
				<input
					type="date"
					value={date}
					min={todayIn(ZONE)}
					onChange={(e) => setDate(e.target.value)}
					className="mt-1 block w-full max-w-xs rounded-lg border border-line bg-card px-3 py-2 text-ink"
				/>
			</label>
			{!shown && <p className="text-sm text-muted" role="status">Buscando horarios…</p>}
			{shown?.length === 0 && <p className="text-sm text-muted">No hay horarios libres ese día.</p>}
			{shown && shown.length > 0 && (
				<div className="flex flex-wrap gap-2" role="group" aria-label="Horarios libres">
					{shown.map((slot) => (
						<button
							key={slot.start}
							type="button"
							disabled={busy}
							onClick={() => choose(slot)}
							className="rounded-lg border border-line bg-card px-3 py-2 font-medium tabular-nums transition-colors hover:border-brand hover:bg-brand-soft disabled:opacity-60"
						>
							{formatTime(slot.localStart)}
						</button>
					))}
				</div>
			)}
			<button type="button" onClick={onClose} className="text-sm text-muted underline-offset-2 hover:underline">
				Mejor no
			</button>
			{error && <p role="alert" className="text-sm text-danger">{error}</p>}
		</div>
	);
}
