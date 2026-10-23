"use client";

import Link from "next/link";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { ApiError, api, type Booking, type Catalog, type Hold, type Slot } from "@/lib/api";
import { formatDay, formatPrice, formatTime, todayIn, zoneCity } from "@/lib/format";
import { HoldCard } from "../ui/HoldCard";
import { Notice } from "../ui/Notice";

type Phase = { name: "form" } | { name: "held"; hold: Hold } | { name: "confirmed"; booking: Booking };

// What the API's codes mean to the person filling the form (docs/errors.md).
const messages: Record<string, string> = {
	"booking.slot_unavailable": "Ese horario ya no está libre. Estos son los que quedan.",
	"booking.slot_taken": "Alguien tomó ese horario hace un momento. Estos son los que quedan.",
	"booking.invalid_customer": "Revisa tu nombre y tu correo.",
	"booking.hold_expired": "La reserva temporal venció antes de confirmarla. Elige el horario de nuevo.",
	"booking.cancelled": "Esta reserva ya no está vigente. Elige el horario de nuevo.",
};

const describe = (error: unknown) =>
	error instanceof ApiError ? (messages[error.problem.code] ?? error.problem.title) : "No pudimos conectar con la agenda. Intenta de nuevo.";

export function BookingForm() {
	const [catalog, setCatalog] = useState<Catalog | null>(null);
	const [catalogError, setCatalogError] = useState<string | null>(null);
	const [serviceId, setServiceId] = useState("");
	const [professionalId, setProfessionalId] = useState("");
	const [date, setDate] = useState("");
	const [start, setStart] = useState("");
	const [customer, setCustomer] = useState({ name: "", email: "", phone: "" });
	const [phase, setPhase] = useState<Phase>({ name: "form" });
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);

	// Slots are kept with the query they answer, so a stale answer is never shown for a new question.
	const [reload, setReload] = useState(0);
	const [slots, setSlots] = useState<{ key: string; slots: Slot[] | null; error: string | null }>({ key: "", slots: null, error: null });
	const query = serviceId && professionalId && date ? `${professionalId}|${serviceId}|${date}|${reload}` : "";

	useEffect(() => {
		api.catalog().then(
			(loaded) => {
				setCatalog(loaded);
				setDate(todayIn(loaded.business.timeZone));
			},
			(failure: unknown) => setCatalogError(describe(failure)),
		);
	}, []);

	useEffect(() => {
		if (!query) {
			return;
		}
		let current = true;
		api.availability(professionalId, serviceId, date).then(
			(found) => current && setSlots({ key: query, slots: found, error: null }),
			(failure: unknown) => current && setSlots({ key: query, slots: null, error: describe(failure) }),
		);
		return () => {
			current = false;
		};
	}, [query, professionalId, serviceId, date]);

	const service = catalog?.services.find((s) => s.id === serviceId);
	const professionals = useMemo(
		() => catalog?.professionals.filter((p) => p.serviceIds.includes(serviceId)) ?? [],
		[catalog, serviceId],
	);
	const professional = professionals.find((p) => p.id === professionalId);
	const shown = slots.key === query ? slots : null;
	const chosen = shown?.slots?.find((s) => s.start === start);

	function chooseService(id: string) {
		setServiceId(id);
		setStart("");
		if (!catalog?.professionals.find((p) => p.id === professionalId)?.serviceIds.includes(id)) {
			const offering = catalog?.professionals.filter((p) => p.serviceIds.includes(id)) ?? [];
			setProfessionalId(offering.length === 1 ? (offering[0]?.id ?? "") : "");
		}
	}

	async function holdSlot(event: FormEvent) {
		event.preventDefault();
		if (!chosen) {
			return;
		}
		setBusy(true);
		setError(null);
		try {
			const hold = await api.hold({
				professionalId,
				serviceId,
				start: chosen.start,
				customer: { name: customer.name, email: customer.email, phone: customer.phone || null },
			});
			setPhase({ name: "held", hold });
		} catch (failure) {
			setError(describe(failure));
			setStart("");
			setReload((n) => n + 1);
		} finally {
			setBusy(false);
		}
	}

	async function confirm(hold: Hold) {
		setBusy(true);
		setError(null);
		try {
			setPhase({ name: "confirmed", booking: await api.confirm(hold.booking.id, hold.confirmationToken) });
		} catch (failure) {
			setError(describe(failure));
			setPhase({ name: "form" });
			setStart("");
			setReload((n) => n + 1);
		} finally {
			setBusy(false);
		}
	}

	if (catalogError) {
		return <Notice tone="error">{catalogError}</Notice>;
	}
	if (!catalog) {
		return <p className="text-muted" role="status">Cargando la agenda…</p>;
	}

	const city = zoneCity(catalog.business.timeZone);

	if (phase.name === "confirmed") {
		return (
			<section className="rounded-xl border border-line bg-card p-6 shadow-sm sm:p-8" aria-live="polite">
				<p className="text-sm font-medium uppercase tracking-wide text-brand">Cita confirmada</p>
				<h2 className="mt-1 text-xl font-semibold">
					{service?.name} con {professional?.name}
				</h2>
				<p className="mt-2 text-muted">
					{formatDay(phase.booking.localStart)}, {formatTime(phase.booking.localStart)} (hora de {city}).
				</p>
				<p className="mt-4">
					Te enviamos la confirmación por correo y a tu bandeja; te recordaremos la cita un día y dos horas antes.
				</p>
				<div className="mt-6 flex flex-wrap gap-3">
					<Link
						href={`/bandeja?email=${encodeURIComponent(customer.email)}`}
						className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark"
					>
						Ver mi bandeja
					</Link>
					<button
						type="button"
						onClick={() => {
							setPhase({ name: "form" });
							setStart("");
							setReload((n) => n + 1);
						}}
						className="rounded-lg border border-line px-4 py-2 font-medium transition-colors hover:bg-brand-soft"
					>
						Reservar otra cita
					</button>
				</div>
			</section>
		);
	}

	if (phase.name === "held") {
		return (
			<HoldCard
				expiresAt={phase.hold.booking.holdExpiresAt}
				localStart={phase.hold.booking.localStart}
				summary={`${service?.name} con ${professional?.name}`}
				city={city}
				busy={busy}
				onConfirm={() => confirm(phase.hold)}
				onChange={() => {
					setPhase({ name: "form" });
					setStart("");
					setReload((n) => n + 1);
				}}
			/>
		);
	}

	return (
		<form onSubmit={holdSlot} className="space-y-8">
			{error && <Notice tone="error">{error}</Notice>}

			<fieldset>
				<legend className="text-lg font-semibold">1. ¿Qué necesitas?</legend>
				<div className="mt-3 grid gap-3 sm:grid-cols-2">
					{catalog.services.map((s) => (
						<label
							key={s.id}
							className="flex cursor-pointer items-start gap-3 rounded-lg border border-line bg-card p-4 transition-colors hover:border-brand has-checked:border-brand has-checked:bg-brand-soft"
						>
							<input
								type="radio"
								name="service"
								value={s.id}
								checked={serviceId === s.id}
								onChange={() => chooseService(s.id)}
								className="mt-1 accent-brand"
							/>
							<span>
								<span className="block font-medium">{s.name}</span>
								<span className="text-sm text-muted">
									{s.durationMinutes} min · {formatPrice(s.price)}
								</span>
							</span>
						</label>
					))}
				</div>
			</fieldset>

			{serviceId && (
				<fieldset>
					<legend className="text-lg font-semibold">2. ¿Con quién?</legend>
					<div className="mt-3 grid gap-3 sm:grid-cols-2">
						{professionals.map((p) => (
							<label
								key={p.id}
								className="flex cursor-pointer items-start gap-3 rounded-lg border border-line bg-card p-4 transition-colors hover:border-brand has-checked:border-brand has-checked:bg-brand-soft"
							>
								<input
									type="radio"
									name="professional"
									value={p.id}
									checked={professionalId === p.id}
									onChange={() => {
										setProfessionalId(p.id);
										setStart("");
									}}
									className="mt-1 accent-brand"
								/>
								<span>
									<span className="block font-medium">{p.name}</span>
									<span className="text-sm text-muted">{p.specialty}</span>
								</span>
							</label>
						))}
					</div>
				</fieldset>
			)}

			{professionalId && (
				<fieldset>
					<legend className="text-lg font-semibold">3. ¿Cuándo?</legend>
					<label className="mt-3 block text-sm text-muted">
						Día (hora de {city})
						<input
							type="date"
							value={date}
							min={todayIn(catalog.business.timeZone)}
							onChange={(e) => {
								setDate(e.target.value);
								setStart("");
							}}
							required
							className="mt-1 block w-full max-w-xs rounded-lg border border-line bg-card px-3 py-2 text-ink"
						/>
					</label>
					<div className="mt-4" aria-live="polite">
						{!shown && <p className="text-muted" role="status">Buscando horarios…</p>}
						{shown?.error && <Notice tone="error">{shown.error}</Notice>}
						{shown?.slots?.length === 0 && (
							<p className="text-muted">No hay horarios libres ese día. Prueba con otro.</p>
						)}
						{shown?.slots && shown.slots.length > 0 && (
							<div className="flex flex-wrap gap-2" role="radiogroup" aria-label="Horarios libres">
								{shown.slots.map((slot) => (
									<button
										key={slot.start}
										type="button"
										role="radio"
										aria-checked={start === slot.start}
										onClick={() => setStart(slot.start)}
										className={`rounded-lg border px-3 py-2 font-medium tabular-nums transition-colors ${
											start === slot.start
												? "border-brand bg-brand text-white"
												: "border-line bg-card hover:border-brand hover:bg-brand-soft"
										}`}
									>
										{formatTime(slot.localStart)}
									</button>
								))}
							</div>
						)}
					</div>
				</fieldset>
			)}

			{chosen && (
				<fieldset>
					<legend className="text-lg font-semibold">4. Tus datos</legend>
					<div className="mt-3 grid gap-4 sm:grid-cols-2">
						<Field label="Nombre" autoComplete="name" value={customer.name} onChange={(name) => setCustomer({ ...customer, name })} required />
						<Field
							label="Correo"
							type="email"
							autoComplete="email"
							value={customer.email}
							onChange={(email) => setCustomer({ ...customer, email })}
							required
						/>
						<Field
							label="Teléfono (opcional)"
							type="tel"
							autoComplete="tel"
							value={customer.phone}
							onChange={(phone) => setCustomer({ ...customer, phone })}
						/>
					</div>
					<button
						type="submit"
						disabled={busy}
						className="mt-6 rounded-lg bg-brand px-5 py-2.5 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60"
					>
						{busy ? "Apartando…" : `Apartar ${formatDay(chosen.localStart)}, ${formatTime(chosen.localStart)}`}
					</button>
				</fieldset>
			)}
		</form>
	);
}

function Field({
	label,
	value,
	onChange,
	type = "text",
	autoComplete,
	required,
}: {
	label: string;
	value: string;
	onChange: (value: string) => void;
	type?: string;
	autoComplete?: string;
	required?: boolean;
}) {
	return (
		<label className="block text-sm text-muted">
			{label}
			<input
				type={type}
				value={value}
				autoComplete={autoComplete}
				required={required}
				onChange={(e) => onChange(e.target.value)}
				className="mt-1 block w-full rounded-lg border border-line bg-card px-3 py-2 text-ink"
			/>
		</label>
	);
}
