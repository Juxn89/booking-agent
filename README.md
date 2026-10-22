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
- **API:** `GET /v1/professionals/{id}/availability`, `POST /v1/holds`, `POST /v1/holds/{id}/confirm`. Errors
  are Problem Details with a stable code ([`docs/errors.md`](docs/errors.md)).
- **Demo data:** "Clínica Demo" in Managua with three professionals and five services, seeded in Development.
- **AppHost** (.NET Aspire): Postgres 18, Mailpit, the API and the web app.

```bash
(cd web && pnpm install)                            # once
dotnet run --project src/BookingAgent.AppHost       # Postgres, Mailpit, API and web (http://localhost:3020)
dotnet test                                         # domain, application, architecture and integration (needs Docker)
```

Rules for contributors and agents are in [`AGENTS.md`](AGENTS.md).
