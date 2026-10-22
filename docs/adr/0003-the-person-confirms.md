# ADR 0003: the person confirms, not the assistant

Status: accepted, 22 Oct 2026.

## Context

The original plan let the assistant confirm a booking after the customer said yes, with a cheap model call
classifying the reply as affirmative. That puts a model's judgement between "sí, pero…" and a booking in the
calendar, costs a call per booking, and is hard to test exhaustively.

## Decision

**No tool confirms anything.** Holding a slot returns a one-time **confirmation token** (256 random bits,
base64url) that only the caller sees: the API stores its SHA-256, never the token. The chat widget shows the hold
as a card with a Confirmar button, which calls `POST /v1/holds/{id}/confirm` with the token; the assistant never
receives it. A wrong token answers 404, the same as a missing booking, and is compared in constant time.
Confirming twice with the right token is harmless.

A reschedule is a hold that names the booking it replaces. Confirming it confirms the new booking and cancels the
old one in the same transaction, so there is never a moment with both or neither.

## Consequences

- "Zero bookings confirmed by the agent" is enforced by the code, and the evals check it on the database.
- No model call is spent on deciding whether a reply meant yes.
- A customer who types "sí" instead of pressing the button has to press it; the assistant is told to say so.
