"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError, api, type InboxItem } from "@/lib/api";
import { Notice } from "../ui/Notice";
import { SignIn } from "../ui/SignIn";

const received = new Intl.DateTimeFormat("es", { dateStyle: "medium", timeStyle: "short" });

type State = { state: "loading" } | { state: "signed-out" } | { state: "error" } | { state: "ready"; items: InboxItem[] };

// The in-app channel's inbox of the signed-in customer: the same notices that go out by email.
export function Inbox() {
	const [view, setView] = useState<State>({ state: "loading" });

	const load = useCallback(() => {
		api.inbox().then(
			(items) => setView({ state: "ready", items }),
			(error: unknown) => setView(error instanceof ApiError && error.problem.status === 401 ? { state: "signed-out" } : { state: "error" }),
		);
	}, []);

	useEffect(load, [load]);

	switch (view.state) {
		case "loading":
			return <p className="text-muted" role="status">Buscando mensajes…</p>;
		case "signed-out":
			return <SignIn onSignedIn={load} />;
		case "error":
			return <Notice tone="error">No pudimos leer la bandeja. Intenta de nuevo.</Notice>;
		default:
			return view.items.length === 0 ? (
				<p className="text-muted">Aún no tienes mensajes.</p>
			) : (
				<ul className="space-y-3">
					{view.items.map((item) => (
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
			);
	}
}
