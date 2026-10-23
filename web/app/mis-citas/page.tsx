import type { Metadata } from "next";
import Link from "next/link";
import { MyBookings } from "./MyBookings";

export const metadata: Metadata = { title: "Mis citas · Clínica Demo" };

export default function MyBookingsPage() {
	return (
		<>
			<Link href="/" className="text-sm text-muted hover:text-brand">
				← Inicio
			</Link>
			<h1 className="mt-4 text-2xl font-semibold tracking-tight">Mis citas</h1>
			<p className="mt-2 mb-8 text-muted">Mira, cambia o cancela tus próximas citas.</p>
			<MyBookings />
		</>
	);
}
