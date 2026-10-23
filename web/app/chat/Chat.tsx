"use client";

import Link from "next/link";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { ApiError, api, type HeldByAssistant } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/format";
import { HoldCard } from "../ui/HoldCard";

type Card = { hold: HeldByAssistant; state: "held" | "confirming" | "confirmed" | "dismissed"; error: string | null };

type Entry =
	| { id: number; from: "customer"; text: string }
	| { id: number; from: "assistant"; text: string; cards: Card[] }
	| { id: number; from: "notice"; text: string; action?: "form" | "restart" };

type WithoutId<T> = T extends unknown ? Omit<T, "id"> : never;

const GREETING = "¡Hola! Soy el asistente de la clínica. Cuéntame qué cita necesitas y para cuándo, y te busco un horario.";

const SUGGESTIONS = ["Quiero una limpieza dental mañana en la mañana", "¿Qué servicios tienen?", "Una sesión de fisioterapia el viernes"];

// What the API's codes mean to the person chatting (docs/errors.md).
function noticeFor(error: unknown): WithoutId<Extract<Entry, { from: "notice" }>> {
	const code = error instanceof ApiError ? error.problem.code : "";
	switch (code) {
		case "assistant.unavailable":
			return { from: "notice", text: "El asistente no está disponible en este momento. Puedes reservar con el formulario.", action: "form" };
		case "assistant.conversation_limit":
		case "assistant.conversation_not_found":
			return { from: "notice", text: "Esta conversación llegó a su límite.", action: "restart" };
		case "assistant.too_many_requests":
			return { from: "notice", text: "Vas muy rápido; espera un momento y vuelve a escribir." };
		case "assistant.invalid_message":
			return { from: "notice", text: "Escribe un mensaje más corto, por favor." };
		default:
			return { from: "notice", text: "No pudimos conectar con el asistente. Intenta de nuevo." };
	}
}

const confirmError = (error: unknown) =>
	error instanceof ApiError && error.problem.code === "booking.hold_expired"
		? "La reserva temporal venció. Pídele al asistente otro horario."
		: "No se pudo confirmar. Pídele al asistente que lo intente de nuevo.";

export function Chat({ city }: { city: string }) {
	const [entries, setEntries] = useState<Entry[]>([{ id: 0, from: "assistant", text: GREETING, cards: [] }]);
	const [conversationId, setConversationId] = useState<string | null>(null);
	const [draft, setDraft] = useState("");
	const [busy, setBusy] = useState(false);
	const nextId = useRef(1);
	const end = useRef<HTMLDivElement>(null);
	const input = useRef<HTMLTextAreaElement>(null);

	useEffect(() => {
		end.current?.scrollIntoView({ behavior: "smooth", block: "end" });
	}, [entries.length]);

	const add = (entry: WithoutId<Entry>) => setEntries((all) => [...all, { ...entry, id: nextId.current++ } as Entry]);

	const updateCard = (holdId: string, change: Partial<Card>) =>
		setEntries((all) =>
			all.map((entry) =>
				entry.from === "assistant" && entry.cards.some((c) => c.hold.holdId === holdId)
					? { ...entry, cards: entry.cards.map((c) => (c.hold.holdId === holdId ? { ...c, ...change } : c)) }
					: entry,
			),
		);

	async function send(text: string) {
		const message = text.trim();
		if (!message || busy) {
			return;
		}
		add({ from: "customer", text: message });
		setDraft("");
		setBusy(true);
		try {
			const reply = await api.send(conversationId, message);
			setConversationId(reply.conversationId);
			add({ from: "assistant", text: reply.reply, cards: reply.holds.map((hold) => ({ hold, state: "held", error: null })) });
		} catch (error) {
			add(noticeFor(error));
		} finally {
			setBusy(false);
			input.current?.focus();
		}
	}

	async function confirm(card: Card) {
		updateCard(card.hold.holdId, { state: "confirming", error: null });
		try {
			await api.confirm(card.hold.holdId, card.hold.confirmationToken);
			updateCard(card.hold.holdId, { state: "confirmed" });
		} catch (error) {
			updateCard(card.hold.holdId, { state: "held", error: confirmError(error) });
		}
	}

	function restart() {
		setConversationId(null);
		setEntries([{ id: nextId.current++, from: "assistant", text: GREETING, cards: [] }]);
	}

	function submit(event: FormEvent) {
		event.preventDefault();
		void send(draft);
	}

	return (
		<div className="flex h-[70vh] min-h-[28rem] flex-col rounded-xl border border-line bg-card shadow-sm">
			<ol className="flex-1 space-y-4 overflow-y-auto p-4 sm:p-6" aria-live="polite" aria-label="Conversación">
				{entries.map((entry) => (
					<li key={entry.id} className={entry.from === "customer" ? "flex justify-end" : "flex justify-start"}>
						{entry.from === "customer" && (
							<p className="max-w-[85%] whitespace-pre-line rounded-2xl rounded-br-sm bg-brand px-4 py-2 text-white">{entry.text}</p>
						)}
						{entry.from === "assistant" && (
							<div className="max-w-[90%] space-y-3">
								<p className="whitespace-pre-line rounded-2xl rounded-bl-sm bg-paper px-4 py-2">{entry.text}</p>
								{entry.cards.map((card) => (
									<CardView key={card.hold.holdId} card={card} city={city} onConfirm={() => confirm(card)} onChange={() => {
										updateCard(card.hold.holdId, { state: "dismissed" });
										input.current?.focus();
									}} />
								))}
							</div>
						)}
						{entry.from === "notice" && (
							<p role="alert" className="max-w-[90%] rounded-lg bg-danger-soft px-4 py-2 text-sm text-danger">
								{entry.text}{" "}
								{entry.action === "form" && (
									<Link href="/reservar" className="font-medium underline">
										Ir al formulario
									</Link>
								)}
								{entry.action === "restart" && (
									<button type="button" onClick={restart} className="font-medium underline">
										Empezar de nuevo
									</button>
								)}
							</p>
						)}
					</li>
				))}
				{busy && (
					<li className="flex justify-start" role="status">
						<p className="rounded-2xl rounded-bl-sm bg-paper px-4 py-2 text-muted">Escribiendo…</p>
					</li>
				)}
				<div ref={end} />
			</ol>

			{entries.length === 1 && (
				<div className="flex flex-wrap gap-2 px-4 pb-3 sm:px-6">
					{SUGGESTIONS.map((suggestion) => (
						<button
							key={suggestion}
							type="button"
							onClick={() => void send(suggestion)}
							className="rounded-full border border-line px-3 py-1.5 text-sm transition-colors hover:border-brand hover:bg-brand-soft"
						>
							{suggestion}
						</button>
					))}
				</div>
			)}

			<form onSubmit={submit} className="flex items-end gap-2 border-t border-line p-3 sm:p-4">
				<label htmlFor="chat-input" className="sr-only">
					Tu mensaje
				</label>
				<textarea
					id="chat-input"
					ref={input}
					value={draft}
					onChange={(e) => setDraft(e.target.value)}
					onKeyDown={(e) => {
						if (e.key === "Enter" && !e.shiftKey) {
							e.preventDefault();
							void send(draft);
						}
					}}
					rows={1}
					maxLength={1000}
					placeholder="Escribe tu mensaje…"
					className="max-h-32 flex-1 resize-none rounded-lg border border-line bg-card px-3 py-2"
				/>
				<button
					type="submit"
					disabled={busy || !draft.trim()}
					className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark disabled:opacity-60"
				>
					Enviar
				</button>
			</form>
		</div>
	);
}

function CardView({ card, city, onConfirm, onChange }: { card: Card; city: string; onConfirm: () => void; onChange: () => void }) {
	const summary = `${card.hold.serviceName} con ${card.hold.professionalName}`;
	if (card.state === "dismissed") {
		return <p className="text-sm text-muted">Dejaste pasar este horario. Dime cuál prefieres.</p>;
	}
	if (card.state === "confirmed") {
		return (
			<section className="rounded-xl border border-brand bg-brand-soft p-4" aria-label="Cita confirmada">
				<p className="text-sm font-medium uppercase tracking-wide text-brand">Cita confirmada</p>
				<p className="mt-1 font-semibold">{summary}</p>
				<p className="mt-1 text-muted">
					{formatDay(card.hold.localStart)}, {formatTime(card.hold.localStart)} (hora de {city}). Te enviamos la confirmación por correo.
				</p>
			</section>
		);
	}
	return (
		<div className="space-y-2">
			<HoldCard
				compact
				expiresAt={card.hold.expiresAt}
				localStart={card.hold.localStart}
				summary={summary}
				city={city}
				busy={card.state === "confirming"}
				onConfirm={onConfirm}
				onChange={onChange}
			/>
			{card.error && (
				<p role="alert" className="text-sm text-danger">
					{card.error}
				</p>
			)}
		</div>
	);
}
