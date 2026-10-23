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
  `POST /v1/holds/{id}/confirm`; signed in, `/v1/businesses/{id}/me/bookings` (list, cancel, reschedule) and
  `/me/inbox`. Errors are Problem Details with a stable code ([`docs/errors.md`](docs/errors.md)).
- **The assistant** (`POST /v1/businesses/{id}/assistant/messages`): Claude Haiku 5.5 with eight tools that are thin
  adapters over the form's use cases: list services and professionals, resolve relative dates on the server
  ("el martes que viene"), check availability and hold a slot; for a signed-in customer, find their appointments and
  propose to move or cancel one. None of them can confirm or cancel: the hold's token goes to
  the widget's card, never to the model ([ADR 0005](docs/adr/0005-tools-are-adapters-over-the-use-cases.md)). The
  tools are declared `strict`, the history stays on the server, and every model and tool call is recorded with its
  tokens and cost. Spending is capped per reply (8 model calls), per conversation (USD 0.05) and per day, and
  each visitor may send 10 messages a minute.
- **Chat** (`/chat`): the conversation with the held slot as a card under the assistant's message, with its
  countdown and the Confirmar button. The prompt keeps the assistant to booking: no medical advice, no other
  topics, and the customer's words are never instructions.
- **Evals** ([`evals/`](evals/README.md)): 28 multi-turn conversations against a real Postgres, scored on the
  database. Claude Haiku 5.5 ends 28/28 as expected (holds, moves, cancellation and sign-in cards), changes no booking
  itself, never holds a time no tool offered, and costs USD 0.0024 per conversation; an oracle run checks the
  harness in CI for free.
- **Sign-in with an emailed code** (`/mis-citas`): six digits that work for 10 minutes and once, sent only to an
  email that has bookings (the answer never says which), and a session in an HttpOnly, SameSite=Strict cookie.
  Signed in, customers see, move and cancel their appointments, in the page or by asking the assistant. The
  assistant only proposes: a cancellation is a card with its own button, a move a hold to confirm
  ([ADR 0006](docs/adr/0006-customers-sign-in-with-an-emailed-code.md)).
- **The business's panel** (`/panel`): each professional's week, held and confirmed, updated live over SignalR the
  moment a booking is held, confirmed, moved, cancelled or released. Bookings made with the assistant are marked,
  and open on their "why": the conversation and every model and tool call, with time, tokens and cost. It opens
  with the staff's access key (`PANEL_ACCESS_KEY` in `.env`), into a sealed HttpOnly cookie
  ([ADR 0007](docs/adr/0007-the-panel-live-over-signalr.md)).
- **Booking form** (`/reservar`): service, professional, day and time, then the held slot with a countdown and a
  Confirmar button. It calls the same API as the assistant will. The inbox, behind the sign-in, is at `/bandeja`.
- **Demo data:** "Clínica Demo" in Managua with three professionals and five services, seeded in Development.
- **AppHost** (.NET Aspire): Postgres 18, Mailpit, the API and the web app.

```bash
(cd web && pnpm install)                            # once
cp .env.template .env                               # once; an Anthropic key for the assistant, a panel key (optional)
dotnet run --project src/BookingAgent.AppHost       # Postgres, Mailpit (http://localhost:8030), API and web (http://localhost:3020)
dotnet test                                         # domain, application, architecture and integration (needs Docker)
dotnet run --project evals/BookingAgent.Evals -- run oracle   # the evals' harness (needs Docker)
```

Rules for contributors and agents are in [`AGENTS.md`](AGENTS.md).
