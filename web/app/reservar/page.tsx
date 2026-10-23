import type { Metadata } from "next";
import Link from "next/link";
import { BookingForm } from "./BookingForm";

export const metadata: Metadata = { title: "Reservar · Clínica Demo" };

// The classic way in: the same API the assistant's tools call, so the form and the chat can never disagree.
export default function ReservePage() {
	return (
		<>
			<Link href="/" className="text-sm text-muted hover:text-brand">
				← Inicio
			</Link>
			<h1 className="mt-4 text-2xl font-semibold tracking-tight">Reserva tu cita</h1>
			<p className="mt-2 mb-8 text-muted">
				Elige el servicio, el profesional y la hora. Te apartamos el horario unos minutos mientras lo confirmas.
			</p>
			<BookingForm />
		</>
	);
}
