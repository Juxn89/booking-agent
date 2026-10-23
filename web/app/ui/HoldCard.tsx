"use client";

import { useEffect, useState } from "react";
import { formatDay, formatTime } from "@/lib/format";

// A held slot with its countdown and the Confirmar button: the only way a booking is confirmed, from the form or
// from the chat (ADR 0003). The assistant never has the token this button sends.
export function HoldCard({
	expiresAt,
	localStart,
	summary,
	city,
	busy,
	onConfirm,
	onChange,
	compact = false,
}: {
	expiresAt: string | null;
	localStart: string;
	summary: string;
	city: string;
	busy: boolean;
	onConfirm: () => void;
	onChange: () => void;
	compact?: boolean;
}) {
	const deadline = expiresAt ? Date.parse(expiresAt) : 0;
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		const timer = setInterval(() => setNow(Date.now()), 1000);
		return () => clearInterval(timer);
	}, []);
	const left = Math.max(0, Math.ceil((deadline - now) / 1000));
	const expired = left === 0;

	return (
		<section
			className={`rounded-xl border border-held bg-card shadow-sm ${compact ? "p-4" : "p-6 sm:p-8"}`}
			aria-live="polite"
			aria-label="Horario apartado"
		>
			<p className="text-sm font-medium uppercase tracking-wide text-held">Horario apartado</p>
			<h2 className={`mt-1 font-semibold ${compact ? "text-lg" : "text-xl"}`}>{summary}</h2>
			<p className="mt-2 text-muted">
				{formatDay(localStart)}, {formatTime(localStart)} (hora de {city}).
			</p>
			<p className="mt-4 rounded-lg bg-held-soft px-3 py-2 text-sm">
				{expired
					? "La reserva temporal venció y el horario quedó libre de nuevo."
					: `Lo guardamos para ti ${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")} min más. Confírmalo para que sea tuyo.`}
			</p>
			<div className={`flex flex-wrap gap-3 ${compact ? "mt-4" : "mt-6"}`}>
				<button
					type="button"
					onClick={onConfirm}
					disabled={busy || expired}
					className="rounded-lg bg-brand px-5 py-2.5 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60"
				>
					{busy ? "Confirmando…" : "Confirmar cita"}
				</button>
				<button
					type="button"
					onClick={onChange}
					disabled={busy}
					className="rounded-lg border border-line px-5 py-2.5 font-medium transition-colors hover:bg-brand-soft"
				>
					Cambiar
				</button>
			</div>
		</section>
	);
}
