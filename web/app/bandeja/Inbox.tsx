"use client";

import { useEffect, useState, type FormEvent } from "react";
import { ApiError, api, type InboxItem } from "@/lib/api";
import { Notice } from "../ui/Notice";

const received = new Intl.DateTimeFormat("es", { dateStyle: "medium", timeStyle: "short" });

const describe = (error: unknown) =>
	error instanceof ApiError && error.problem.code === "inbox.invalid_email"
		? "Escribe el correo completo, por ejemplo ana@ejemplo.com."
		: "No pudimos leer la bandeja. Intenta de nuevo.";

// The in-app channel's inbox, by email. Development only until customers sign in with a code (day 7).
export function Inbox({ initialEmail }: { initialEmail: string }) {
	const [email, setEmail] = useState(initialEmail);
	const [shown, setShown] = useState<{ email: string; items: InboxItem[] | null; error: string | null } | null>(null);
	const [asked, setAsked] = useState(initialEmail);

	useEffect(() => {
		if (!asked) {
			return;
		}
		let current = true;
		api.inbox(asked).then(
			(items) => current && setShown({ email: asked, items, error: null }),
			(failure: unknown) => current && setShown({ email: asked, items: null, error: describe(failure) }),
		);
		return () => {
			current = false;
		};
	}, [asked]);

	function look(event: FormEvent) {
		event.preventDefault();
		setAsked(email.trim());
	}

	const current = shown?.email === asked ? shown : null;

	return (
		<div className="space-y-6">
			<form onSubmit={look} className="flex flex-wrap items-end gap-3">
				<label className="block grow text-sm text-muted">
					Correo con el que reservaste
					<input
						type="email"
						value={email}
						onChange={(e) => setEmail(e.target.value)}
						required
						autoComplete="email"
						className="mt-1 block w-full rounded-lg border border-line bg-card px-3 py-2 text-ink"
					/>
				</label>
				<button type="submit" className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark">
					Ver mensajes
				</button>
			</form>

			<div aria-live="polite">
				{asked && !current && <p className="text-muted" role="status">Buscando mensajes…</p>}
				{current?.error && <Notice tone="error">{current.error}</Notice>}
				{current?.items?.length === 0 && <p className="text-muted">Aún no hay mensajes para este correo.</p>}
				{current?.items && current.items.length > 0 && (
					<ul className="space-y-3">
						{current.items.map((item) => (
							<li key={item.id} className="rounded-lg border border-line bg-card p-4 shadow-sm">
								<div className="flex flex-wrap items-baseline justify-between gap-2">
									<h2 className="font-semibold">{item.subject}</h2>
									<time dateTime={item.receivedAt} className="text-sm text-muted">
										{received.format(new Date(item.receivedAt))}
									</time>
								</div>
								<p className="mt-2 whitespace-pre-line text-muted">{item.body}</p>
							</li>
						))}
					</ul>
				)}
			</div>
		</div>
	);
}
