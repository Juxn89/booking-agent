import type { ReactNode } from "react";

// A line of feedback: an alert for errors (read out at once), a status otherwise.
export function Notice({ tone, children }: { tone: "error" | "info"; children: ReactNode }) {
	return (
		<p
			role={tone === "error" ? "alert" : "status"}
			className={`rounded-lg px-4 py-3 text-sm ${tone === "error" ? "bg-danger-soft text-danger" : "bg-brand-soft text-brand-dark"}`}
		>
			{children}
		</p>
	);
}
