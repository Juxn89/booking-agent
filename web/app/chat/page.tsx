import type { Metadata } from "next";
import Link from "next/link";
import { Chat } from "./Chat";

export const metadata: Metadata = { title: "Asistente · Clínica Demo" };

// The demo clinic's clock; the chat says every time in it.
const CITY = "Managua";

export default function ChatPage() {
	return (
		<>
			<Link href="/" className="text-sm text-muted hover:text-brand">
				← Inicio
			</Link>
			<h1 className="mt-4 text-2xl font-semibold tracking-tight">Reserva conversando</h1>
			<p className="mt-2 mb-6 text-muted">
				El asistente busca el horario y te lo aparta; tú lo confirmas con el botón. Él no puede confirmar por ti.
			</p>
			<Chat city={CITY} />
		</>
	);
}
