import Link from "next/link";

// The assistant's chat arrives on days 4-5 and the business's panel on day 8 (plan §10).
export default function Home() {
	return (
		<section className="rounded-xl border border-line bg-card p-8 shadow-sm">
			<h1 className="text-2xl font-semibold tracking-tight">Clínica Demo · Agenda</h1>
			<p className="mt-2 text-muted">
				Reserva tu cita en un par de pasos. Muy pronto, también conversando con nuestro asistente.
			</p>
			<div className="mt-6 flex flex-wrap gap-3">
				<Link href="/reservar" className="rounded-lg bg-brand px-4 py-2 font-medium text-white transition-colors hover:bg-brand-dark">
					Reservar una cita
				</Link>
				<Link href="/bandeja" className="rounded-lg border border-line px-4 py-2 font-medium transition-colors hover:bg-brand-soft">
					Ver mi bandeja
				</Link>
			</div>
		</section>
	);
}
