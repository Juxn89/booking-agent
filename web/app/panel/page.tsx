import type { Metadata } from "next";
import Link from "next/link";
import { Panel } from "./Panel";

export const metadata: Metadata = { title: "Panel · Clínica Demo" };

// The business's side: the week of each professional, live, and the "why" behind each booking the assistant made.
export default function PanelPage() {
	return (
		<div className="lg:-mx-40">
			<Link href="/" className="text-sm text-muted hover:text-brand">
				← Inicio
			</Link>
			<h1 className="mt-4 text-2xl font-semibold tracking-tight">Panel de la clínica</h1>
			<p className="mt-2 mb-6 text-muted">La agenda de la semana, al instante: lo que se reserva en el chat o en el formulario aparece aquí sin recargar.</p>
			<Panel />
		</div>
	);
}
