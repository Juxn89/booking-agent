# booking-agent

**Work in progress.** Appointment booking for small businesses: customers book by chatting with an assistant
built on Claude Haiku, a person confirms every booking with a button, and a Postgres exclusion constraint makes
double booking impossible.

So far:

- **Availability** (`src/BookingAgent.Domain/Scheduling`): weekly hours per professional, on the business's
  clock, with NodaTime. Slots are laid out on the local clock and last real time, so the daylight-saving
  transitions behave as a receptionist would expect: a skipped hour is never offered, a repeated hour is
  offered once. Taken periods, time off and a minimum notice are honoured.
- **Use cases** `FindAvailableSlots`, `HoldSlot`, `ConfirmHold`, `CancelBooking`, `RescheduleBooking` and
  `ExpireHolds`: what the form and the assistant's tools will share.
- **Holds and confirmation:** a slot is held for five minutes and comes with a one-time token; only the token
  confirms it, and the assistant never gets one ([ADR 0003](docs/adr/0003-the-person-confirms.md)). Holds that
  run out stop counting at once and are swept every 30 seconds ([ADR 0002](docs/adr/0002-holds-with-a-ttl.md)).
- **No double booking:** a Postgres exclusion constraint on `(professional_id, period)`, with new bookings of a
  professional saved one at a time; fifty simultaneous requests for one slot give one hold and forty-nine 409s
  in about a second ([ADR 0001](docs/adr/0001-no-double-booking-in-postgres.md)).
- **Notifications through an outbox:** confirming, moving or cancelling a booking writes its notices in the same
  transaction: one at once, and reminders a day and two hours before. A dispatcher sends them by email (Mailpit in
  local runs) and to an in-app inbox, retrying failures with a growing wait. A reminder for a booking that is no
  longer on is skipped ([ADR 0004](docs/adr/0004-notifications-through-an-outbox.md)).
- **API:** `GET /v1/businesses/{id}/catalog`, `GET /v1/professionals/{id}/availability`, `POST /v1/holds`,
  `POST /v1/holds/{id}/confirm`; in Development, `GET /v1/dev/inbox?email=`. Errors are Problem Details with a
  stable code ([`docs/errors.md`](docs/errors.md)).
- **The assistant** (`POST /v1/businesses/{id}/assistant/messages`): Claude Haiku 5.5 with five tools that are thin
  adapters over the form's use cases: list services and professionals, resolve relative dates on the server
  ("el martes que viene"), check availability and hold a slot. None of them can confirm: the hold's token goes to
  the widget's card, never to the model ([ADR 0005](docs/adr/0005-tools-are-adapters-over-the-use-cases.md)). The
  tools are declared `strict`, the history stays on the server, and every model and tool call is recorded with its
  tokens and cost. Spending is capped per reply (8 model calls), per conversation (USD 0.05) and per day, and
  each visitor may send 10 messages a minute.
- **Chat** (`/chat`): the conversation with the held slot as a card under the assistant's message, with its
  countdown and the Confirmar button. The prompt keeps the assistant to booking: no medical advice, no other
  topics, and the customer's words are never instructions.
- **Booking form** (`/reservar`): service, professional, day and time, then the held slot with a countdown and a
  Confirmar button. It calls the same API as the assistant will. The inbox is at `/bandeja`.
- **Demo data:** "Clínica Demo" in Managua with three professionals and five services, seeded in Development.
- **AppHost** (.NET Aspire): Postgres 18, Mailpit, the API and the web app.

```bash
(cd web && pnpm install)                            # once
cp .env.template .env                               # once; add an Anthropic key for the assistant (optional)
dotnet run --project src/BookingAgent.AppHost       # Postgres, Mailpit (http://localhost:8030), API and web (http://localhost:3020)
dotnet test                                         # domain, application, architecture and integration (needs Docker)
```

Rules for contributors and agents are in [`AGENTS.md`](AGENTS.md).
