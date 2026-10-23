"use client";

import { useState, type FormEvent } from "react";
import { ApiError, api, type Me } from "@/lib/api";
import { Notice } from "./Notice";

// What the API's sign-in codes mean to the person (docs/errors.md).
const messages: Record<string, string> = {
	"signin.invalid_email": "Escribe el correo completo, por ejemplo ana@ejemplo.com.",
	"signin.invalid_code": "Ese no es el código que te enviamos. Revísalo e intenta de nuevo.",
	"signin.code_expired": "Ese código ya no sirve. Pide uno nuevo.",
	"signin.too_many_requests": "Demasiados intentos seguidos. Espera un minuto.",
	"signin.unavailable": "No pudimos enviar el código. Intenta de nuevo en un minuto.",
};

const describe = (error: unknown) =>
	error instanceof ApiError ? (messages[error.problem.code] ?? error.problem.title) : "No pudimos conectar. Intenta de nuevo.";

// Two steps: the email the bookings are under, then the six-digit code emailed to it. The session is an HttpOnly
// cookie the API sets; this component never sees it.
export function SignIn({ onSignedIn, compact = false }: { onSignedIn: (me: Me) => void; compact?: boolean }) {
	const [email, setEmail] = useState("");
	const [code, setCode] = useState("");
	const [sent, setSent] = useState(false);
	const [busy, setBusy] = useState(false);
	const [error, setError] = useState<string | null>(null);

	async function ask(event: FormEvent) {
		event.preventDefault();
		setBusy(true);
		setError(null);
		try {
			await api.requestCode(email.trim());
			setSent(true);
		} catch (failure) {
			setError(describe(failure));
		} finally {
			setBusy(false);
		}
	}

	async function verify(event: FormEvent) {
		event.preventDefault();
		setBusy(true);
		setError(null);
		try {
			onSignedIn(await api.signIn(email.trim(), code.trim()));
		} catch (failure) {
			setError(describe(failure));
		} finally {
			setBusy(false);
		}
	}

	return (
		<section className={`rounded-xl border border-line bg-card shadow-sm ${compact ? "p-4" : "p-6"}`} aria-label="Identifícate">
			<h2 className="font-semibold">Identifícate</h2>
			{!sent ? (
				<form onSubmit={ask} className="mt-3 space-y-3">
					<p className="text-sm text-muted">Te enviamos un código de 6 dígitos al correo con el que reservaste.</p>
					<label className="block text-sm text-muted">
						Correo
						<input
							type="email"
							value={email}
							onChange={(e) => setEmail(e.target.value)}
							required
							autoComplete="email"
							className="mt-1 block w-full rounded-lg border border-line bg-card px-3 py-2 text-ink"
						/>
					</label>
					<button type="submit" disabled={busy} className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60">
						{busy ? "Enviando…" : "Enviarme el código"}
					</button>
				</form>
			) : (
				<form onSubmit={verify} className="mt-3 space-y-3">
					<p className="text-sm text-muted">
						Si {email} tiene citas con nosotros, te llegó un código. Vence en 10 minutos.
					</p>
					<label className="block text-sm text-muted">
						Código
						<input
							inputMode="numeric"
							pattern="[0-9]{6}"
							maxLength={6}
							value={code}
							onChange={(e) => setCode(e.target.value.replace(/\D/g, ""))}
							required
							autoComplete="one-time-code"
							className="mt-1 block w-40 rounded-lg border border-line bg-card px-3 py-2 text-lg tracking-[0.3em] text-ink"
						/>
					</label>
					<div className="flex flex-wrap gap-3">
						<button type="submit" disabled={busy} className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60">
							{busy ? "Comprobando…" : "Entrar"}
						</button>
						<button type="button" onClick={() => setSent(false)} className="rounded-lg border border-line px-4 py-2 font-medium transition-colors hover:bg-brand-soft">
							Usar otro correo
						</button>
					</div>
				</form>
			)}
			{error && (
				<div className="mt-3">
					<Notice tone="error">{error}</Notice>
				</div>
			)}
		</section>
	);
}
