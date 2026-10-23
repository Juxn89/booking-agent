import type { Metadata } from "next";
import Link from "next/link";
import { Inbox } from "./Inbox";

export const metadata: Metadata = { title: "Bandeja · Clínica Demo" };

// Mailpit's web inbox, where the same notifications arrive by email in local runs (AppHost, port 8030).
const MAILPIT_URL = "http://localhost:8030";

export default function InboxPage() {
	return (
		<>
			<Link href="/" className="text-sm text-muted hover:text-brand">
				← Inicio
			</Link>
			<h1 className="mt-4 text-2xl font-semibold tracking-tight">Tu bandeja</h1>
			<p className="mt-2 mb-8 text-muted">
				Confirmaciones, recordatorios y cancelaciones de tus citas. Los mismos avisos llegan por correo; en local
				puedes verlos en{" "}
				<a href={MAILPIT_URL} className="text-brand underline-offset-2 hover:underline" target="_blank" rel="noreferrer">
					Mailpit
				</a>
				.
			</p>
			<Inbox />
		</>
	);
}
