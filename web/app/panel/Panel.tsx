"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { ApiError, BUSINESS_ID, api, type BookingTrace, type PanelBooking, type PanelCalendar } from "@/lib/api";
import { formatDay, formatTime, todayIn } from "@/lib/format";
import { Notice } from "../ui/Notice";

// The demo clinic's clock: the calendar is laid out on it.
const ZONE = "America/Managua";
const DAYS = 7;
const FIRST_HOUR = 7;
const LAST_HOUR = 19;
const HOUR_PX = 56;

const dayNames = ["Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom"];

function addDays(iso: string, days: number): string {
	const date = new Date(`${iso}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + days);
	return date.toISOString().slice(0, 10);
}

// Monday of the week a day falls in.
function mondayOf(iso: string): string {
	const weekday = (new Date(`${iso}T00:00:00Z`).getUTCDay() + 6) % 7;
	return addDays(iso, -weekday);
}

const minutesOf = (local: string) => Number(local.slice(11, 13)) * 60 + Number(local.slice(14, 16));

type Session = "checking" | "signed-out" | "signed-in";
type Live = "connecting" | "live" | "offline";

export function Panel() {
	const [session, setSession] = useState<Session>("checking");
	const [week, setWeek] = useState(() => mondayOf(todayIn(ZONE)));
	const [calendar, setCalendar] = useState<PanelCalendar | null>(null);
	const [professionalId, setProfessionalId] = useState<string | null>(null);
	const [selectedId, setSelectedId] = useState<string | null>(null);
	const [fresh, setFresh] = useState<Set<string>>(new Set());
	const [live, setLive] = useState<Live>("connecting");
	const [version, setVersion] = useState(0);
	const refresh = useCallback(() => setVersion((v) => v + 1), []);

	// The week, again whenever it changes or a refresh is asked for; an answer for an older request is dropped.
	useEffect(() => {
		let current = true;
		api.panelCalendar(week, DAYS).then(
			(loaded) => {
				if (current) {
					setCalendar(loaded);
					setSession("signed-in");
				}
			},
			(error: unknown) => {
				if (current && error instanceof ApiError && error.problem.status === 401) {
					setSession("signed-out");
				}
			},
		);
		return () => {
			current = false;
		};
	}, [week, version]);

	// Live updates: any booking change of the business reloads the week, and the changed blocks glow for a moment.
	useEffect(() => {
		if (session !== "signed-in") {
			return;
		}
		const connection: HubConnection = new HubConnectionBuilder()
			.withUrl(`/api/hubs/calendar?businessId=${BUSINESS_ID}`)
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();
		connection.on("bookingsChanged", (ids: string[]) => {
			setFresh(new Set(ids));
			refresh();
			setTimeout(() => setFresh(new Set()), 4000);
		});
		connection.onreconnecting(() => setLive("connecting"));
		connection.onreconnected(() => {
			setLive("live");
			refresh();
		});
		connection.onclose(() => setLive("offline"));
		connection.start().then(
			() => setLive("live"),
			() => setLive("offline"),
		);
		return () => {
			if (connection.state !== HubConnectionState.Disconnected) {
				void connection.stop();
			}
		};
	}, [session, refresh]);

	const professionals = calendar?.professionals ?? [];
	const shownProfessional = professionalId ?? professionals[0]?.id ?? null;
	const bookings = useMemo(
		() => (calendar?.bookings ?? []).filter((b) => b.professionalId === shownProfessional),
		[calendar, shownProfessional],
	);
	// The selection is read from the latest calendar: a booking cancelled or moved elsewhere closes its details.
	const selected = bookings.find((b) => b.id === selectedId) ?? null;

	if (session === "checking") {
		return <p className="text-muted" role="status">Cargando…</p>;
	}

	if (session === "signed-out") {
		return <KeyForm onSignedIn={refresh} />;
	}

	const today = todayIn(ZONE);
	return (
		<div className="space-y-4">
			<div className="flex flex-wrap items-center justify-between gap-3">
				<div className="flex items-center gap-2">
					<button type="button" onClick={() => setWeek(addDays(week, -7))} className="rounded-lg border border-line px-3 py-1.5 hover:bg-brand-soft" aria-label="Semana anterior">
						←
					</button>
					<button type="button" onClick={() => setWeek(mondayOf(today))} className="rounded-lg border border-line px-3 py-1.5 hover:bg-brand-soft">
						Hoy
					</button>
					<button type="button" onClick={() => setWeek(addDays(week, 7))} className="rounded-lg border border-line px-3 py-1.5 hover:bg-brand-soft" aria-label="Semana siguiente">
						→
					</button>
					<p className="ml-2 font-medium">Semana del {formatDay(`${week}T00:00:00`)}</p>
				</div>
				<div className="flex items-center gap-4">
					<p className="flex items-center gap-2 text-sm text-muted" role="status">
						<span className={`inline-block size-2.5 rounded-full ${live === "live" ? "bg-emerald-500" : live === "connecting" ? "bg-held" : "bg-danger"}`} />
						{live === "live" ? "En vivo" : live === "connecting" ? "Conectando…" : "Sin conexión"}
					</p>
					<button
						type="button"
						onClick={async () => {
							await api.panelSignOut();
							setSession("signed-out");
						}}
						className="text-sm text-muted underline-offset-2 hover:text-brand hover:underline"
					>
						Salir
					</button>
				</div>
			</div>

			<div className="flex flex-wrap gap-2" role="tablist" aria-label="Profesionales">
				{professionals.map((p) => (
					<button
						key={p.id}
						type="button"
						role="tab"
						aria-selected={p.id === shownProfessional}
						onClick={() => {
							setProfessionalId(p.id);
							setSelectedId(null);
						}}
						className={`rounded-full border px-4 py-1.5 text-sm transition-colors ${
							p.id === shownProfessional ? "border-brand bg-brand text-white" : "border-line bg-card hover:border-brand"
						}`}
					>
						{p.name} <span className={p.id === shownProfessional ? "text-white/80" : "text-muted"}>· {p.specialty}</span>
					</button>
				))}
			</div>

			<div className="grid gap-4 lg:grid-cols-[1fr_22rem]">
				<Week week={week} today={today} bookings={bookings} fresh={fresh} selected={selected} onSelect={(booking) => setSelectedId(booking.id)} />
				<Details
					key={selected?.id ?? "none"}
					booking={selected}
					onCancelled={() => {
						setSelectedId(null);
						refresh();
					}}
				/>
			</div>

			<p className="flex flex-wrap gap-4 text-sm text-muted">
				<span className="flex items-center gap-2"><span className="inline-block size-3 rounded-sm border border-brand bg-brand-soft" />Confirmada</span>
				<span className="flex items-center gap-2"><span className="inline-block size-3 rounded-sm border border-dashed border-held bg-held-soft" />Apartada, esperando confirmación</span>
				<span className="flex items-center gap-2"><span className="rounded bg-ink px-1 text-[0.65rem] font-semibold text-white">IA</span>Hecha con el asistente</span>
			</p>
		</div>
	);
}

function KeyForm({ onSignedIn }: { onSignedIn: () => void }) {
	const [key, setKey] = useState("");
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);

	async function submit(event: FormEvent) {
		event.preventDefault();
		setBusy(true);
		setError(null);
		try {
			await api.panelSignIn(key);
			onSignedIn();
		} catch (failure) {
			const code = failure instanceof ApiError ? failure.problem.code : "";
			setError(
				code === "panel.invalid_key"
					? "Esa no es la clave del panel."
					: code === "panel.not_configured"
						? "El panel no tiene clave configurada (PANEL_ACCESS_KEY)."
						: code === "signin.too_many_requests"
							? "Demasiados intentos. Espera un minuto."
							: "No pudimos entrar. Intenta de nuevo.",
			);
		} finally {
			setBusy(false);
		}
	}

	return (
		<form onSubmit={submit} className="max-w-md space-y-3 rounded-xl border border-line bg-card p-6 shadow-sm" aria-label="Entrar al panel">
			<label className="block text-sm text-muted">
				Clave del panel
				<input
					type="password"
					value={key}
					onChange={(e) => setKey(e.target.value)}
					required
					autoComplete="current-password"
					className="mt-1 block w-full rounded-lg border border-line bg-card px-3 py-2 text-ink"
				/>
			</label>
			<button type="submit" disabled={busy} className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60">
				{busy ? "Entrando…" : "Entrar"}
			</button>
			{error && <Notice tone="error">{error}</Notice>}
		</form>
	);
}

function Week({
	week,
	today,
	bookings,
	fresh,
	selected,
	onSelect,
}: {
	week: string;
	today: string;
	bookings: PanelBooking[];
	fresh: Set<string>;
	selected: PanelBooking | null;
	onSelect: (booking: PanelBooking) => void;
}) {
	const days = Array.from({ length: DAYS }, (_, i) => addDays(week, i));
	const hours = Array.from({ length: LAST_HOUR - FIRST_HOUR }, (_, i) => FIRST_HOUR + i);
	const height = (LAST_HOUR - FIRST_HOUR) * HOUR_PX;

	return (
		<div className="overflow-x-auto rounded-xl border border-line bg-card shadow-sm">
			<div className="grid min-w-[44rem] grid-cols-[3.5rem_repeat(7,1fr)]">
				<div className="border-b border-line" />
				{days.map((day, i) => (
					<div key={day} className={`border-b border-l border-line px-2 py-2 text-center text-sm ${day === today ? "bg-brand-soft font-semibold text-brand-dark" : ""}`}>
						{dayNames[i]} {Number(day.slice(8, 10))}
					</div>
				))}

				<div className="relative" style={{ height }}>
					{hours.map((hour) => (
						<span key={hour} className="absolute right-2 -translate-y-1/2 text-xs text-muted tabular-nums" style={{ top: (hour - FIRST_HOUR) * HOUR_PX }}>
							{hour}:00
						</span>
					))}
				</div>
				{days.map((day) => (
					<div key={day} className="relative border-l border-line" style={{ height }}>
						{hours.map((hour) => (
							<div key={hour} className="absolute inset-x-0 border-t border-line/70" style={{ top: (hour - FIRST_HOUR) * HOUR_PX }} />
						))}
						{bookings
							.filter((b) => b.localStart.slice(0, 10) === day)
							.map((booking) => {
								const top = ((minutesOf(booking.localStart) - FIRST_HOUR * 60) / 60) * HOUR_PX;
								const span = ((minutesOf(booking.localEnd) - minutesOf(booking.localStart)) / 60) * HOUR_PX;
								const held = booking.status === "Held";
								return (
									<button
										key={booking.id}
										type="button"
										onClick={() => onSelect(booking)}
										aria-label={`${formatTime(booking.localStart)} ${booking.customerName}, ${booking.serviceName}${held ? ", apartada" : ""}`}
										className={`absolute inset-x-1 overflow-hidden rounded-md border px-1.5 py-1 text-left text-xs transition-shadow ${
											held ? "border-dashed border-held bg-held-soft" : "border-brand bg-brand-soft"
										} ${fresh.has(booking.id) ? "animate-pulse ring-2 ring-held" : ""} ${selected?.id === booking.id ? "ring-2 ring-brand" : ""}`}
										style={{ top, height: Math.max(span - 2, 18) }}
									>
										<span className="flex items-center gap-1 font-semibold">
											{formatTime(booking.localStart)}
											{booking.viaAssistant && <span className="rounded bg-ink px-1 text-[0.6rem] text-white">IA</span>}
										</span>
										<span className="block truncate">{booking.customerName}</span>
										<span className="block truncate text-muted">{booking.serviceName}</span>
									</button>
								);
							})}
					</div>
				))}
			</div>
		</div>
	);
}

function Details({ booking, onCancelled }: { booking: PanelBooking | null; onCancelled: () => void }) {
	const [trace, setTrace] = useState<BookingTrace | null>(null);
	const [confirming, setConfirming] = useState(false);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		if (!booking?.viaAssistant) {
			return;
		}
		let current = true;
		api.panelTrace(booking.id).then(
			(loaded) => current && setTrace(loaded),
			() => current && setTrace(null),
		);
		return () => {
			current = false;
		};
	}, [booking]);

	if (!booking) {
		return (
			<aside className="rounded-xl border border-dashed border-line p-5 text-sm text-muted">
				Elige una cita para ver sus detalles y, si la hizo el asistente, la conversación y los pasos que siguió.
			</aside>
		);
	}

	async function cancel() {
		setError(null);
		try {
			await api.panelCancel(booking!.id);
			onCancelled();
		} catch (failure) {
			setError(failure instanceof ApiError && failure.problem.code === "booking.too_late" ? "Ya es muy tarde para cancelarla." : "No se pudo cancelar.");
		}
	}

	return (
		<aside className="space-y-4 rounded-xl border border-line bg-card p-5 shadow-sm" aria-label="Detalle de la cita">
			<div>
				<p className={`text-sm font-medium uppercase tracking-wide ${booking.status === "Held" ? "text-held" : "text-brand"}`}>
					{booking.status === "Held" ? "Apartada" : "Confirmada"}
					{booking.viaAssistant && " · con el asistente"}
				</p>
				<h2 className="mt-1 text-lg font-semibold">{booking.customerName}</h2>
				<p className="text-sm text-muted">{booking.customerEmail}</p>
				<p className="mt-2">
					{booking.serviceName}, {formatDay(booking.localStart)} de {formatTime(booking.localStart)} a {formatTime(booking.localEnd)}
				</p>
			</div>

			{!confirming ? (
				<button type="button" onClick={() => setConfirming(true)} className="rounded-lg border border-line px-4 py-2 text-sm font-medium text-danger hover:bg-danger-soft">
					Cancelar cita
				</button>
			) : (
				<div className="flex flex-wrap items-center gap-2 text-sm">
					<span>¿Cancelarla? Le avisaremos al cliente.</span>
					<button type="button" onClick={cancel} className="rounded-lg bg-danger px-3 py-1.5 font-medium text-white">
						Sí, cancelar
					</button>
					<button type="button" onClick={() => setConfirming(false)} className="rounded-lg border border-line px-3 py-1.5">
						No
					</button>
				</div>
			)}
			{error && <Notice tone="error">{error}</Notice>}

			{booking.viaAssistant && !trace && <p className="text-sm text-muted" role="status">Cargando la conversación…</p>}
			{trace && <Trace trace={trace} />}
		</aside>
	);
}

// The "why" of a booking: what was said, and every model and tool call behind it, with its time and cost.
function Trace({ trace }: { trace: BookingTrace }) {
	const money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", minimumFractionDigits: 4, maximumFractionDigits: 4 });
	return (
		<div className="space-y-4 border-t border-line pt-4">
			<div>
				<h3 className="text-sm font-semibold">Conversación</h3>
				<ol className="mt-2 space-y-2 text-sm">
					{trace.transcript.map((message, i) => (
						<li key={i} className={message.role === "customer" ? "rounded-lg bg-brand px-3 py-1.5 text-white" : "rounded-lg bg-paper px-3 py-1.5"}>
							{message.text}
						</li>
					))}
				</ol>
			</div>
			<div>
				<h3 className="text-sm font-semibold">Pasos del asistente · {money.format(trace.costUsd)}</h3>
				<ol className="mt-2 space-y-1.5 text-xs">
					{trace.steps.map((step) => (
						<li key={`${step.turn}-${step.ordinal}`} className="rounded-md border border-line px-2 py-1.5">
							{step.kind === "Model" ? (
								<span className="text-muted">
									Modelo · {step.inputTokens.toLocaleString("es")} + {step.outputTokens.toLocaleString("es")} tokens · {step.latencyMs} ms · {money.format(step.costUsd)}
								</span>
							) : (
								<details>
									<summary className="cursor-pointer font-mono font-medium">
										{step.tool} <span className="font-sans font-normal text-muted">· {step.latencyMs} ms</span>
									</summary>
									<pre className="mt-1 overflow-x-auto whitespace-pre-wrap break-all text-[0.7rem] text-muted">{step.input}</pre>
									<pre className="mt-1 overflow-x-auto whitespace-pre-wrap break-all text-[0.7rem]">{step.output}</pre>
								</details>
							)}
						</li>
					))}
				</ol>
			</div>
		</div>
	);
}
